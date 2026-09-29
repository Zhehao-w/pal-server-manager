using System.Globalization;
using System.Text.Json;
using HaoHaoTianTian.PalHR.Models;

namespace HaoHaoTianTian.PalHR.Services;

public sealed class PlayerRosterService(PalContext context, SafeFileService files) : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly SemaphoreSlim _gate = new(1, 1);
    private PlayerActivityDocument? _document;
    private string? _worldGuid;
    private string? _playersDirectory;
    private PlayerPresenceMode _presenceMode = PlayerPresenceMode.Unknown;
    private readonly HashSet<string> _savedIds = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _onlineIds = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, RestPlayer> _livePlayers = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DateTimeOffset> _sessionStarts = new(StringComparer.OrdinalIgnoreCase);
    private DateTimeOffset _nextSaveScanUtc = DateTimeOffset.MinValue;
    private DateTimeOffset _nextCheckpointUtc = DateTimeOffset.MinValue;
    private RosterReadState _currentReadState = RosterReadState.Empty;
    private bool _dirty;
    private int _disposed;

    public async Task<PlayerRosterSnapshot> RefreshAsync(
        string worldGuid,
        string playersDirectory,
        IReadOnlyList<RestPlayer>? onlinePlayers,
        PlayerPresenceMode mode,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            await EnsureLoadedAsync(cancellationToken).ConfigureAwait(false);
            var importantChange = SwitchWorldIfNeeded(worldGuid, playersDirectory, now);

            if (now >= _nextSaveScanUtc || mode == PlayerPresenceMode.ServerStopped)
            {
                importantChange |= ScanSaveFiles();
                _nextSaveScanUtc = now.AddMinutes(1);
            }

            switch (mode)
            {
                case PlayerPresenceMode.OnlineSnapshot:
                    importantChange |= ApplyOnlineSnapshot(onlinePlayers ?? [], now);
                    break;
                case PlayerPresenceMode.ServerStopped:
                    importantChange |= EndAllSessions(now);
                    _onlineIds.Clear();
                    _livePlayers.Clear();
                    _presenceMode = PlayerPresenceMode.ServerStopped;
                    break;
                case PlayerPresenceMode.Unknown:
                    _presenceMode = PlayerPresenceMode.Unknown;
                    break;
            }

            if (importantChange || (_dirty && now >= _nextCheckpointUtc))
            {
                await SaveDocumentAsync(cancellationToken).ConfigureAwait(false);
                _nextCheckpointUtc = now.AddMinutes(5);
            }

            var snapshot = CreateSnapshot(now);
            var readState = new RosterReadState(
                snapshot,
                new Dictionary<string, DateTimeOffset>(_sessionStarts, StringComparer.OrdinalIgnoreCase));
            Volatile.Write(ref _currentReadState, readState);
            return snapshot;
        }
        finally
        {
            _gate.Release();
        }
    }

    // UI callers only need a stable read model. Never synchronously wait on the
    // refresh gate here; elapsed online durations are rendered from the supplied
    // timestamp using the immutable session-start copy captured by RefreshAsync.
    public PlayerRosterSnapshot GetCurrentSnapshot(DateTimeOffset now)
    {
        ThrowIfDisposed();
        var state = Volatile.Read(ref _currentReadState);
        if (state.SessionStarts.Count == 0) return state.Snapshot;

        var rows = state.Snapshot.Players.Select(row =>
        {
            if (!string.Equals(row.Status, "在线", StringComparison.Ordinal) ||
                !state.SessionStarts.TryGetValue(row.PlayerId, out var started)) return row;
            return row with { OnlineDuration = FormatOnlineDuration(now - started) };
        }).ToArray();

        return state.Snapshot with { Players = rows };
    }

    private async Task EnsureLoadedAsync(CancellationToken cancellationToken)
    {
        if (_document is not null) return;
        if (!File.Exists(context.StatePaths.PlayerActivityPath))
        {
            _document = new PlayerActivityDocument();
            return;
        }

        try
        {
            await using var stream = new FileStream(context.StatePaths.PlayerActivityPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 4096, true);
            _document = await JsonSerializer.DeserializeAsync<PlayerActivityDocument>(stream, JsonOptions, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException("PlayerActivity.json 为空。");
            _document.Worlds ??= [];
            foreach (var world in _document.Worlds.Values) world.Players ??= [];
        }
        catch (JsonException exception) { throw new InvalidOperationException($"PlayerActivity.json 已损坏：{exception.Message}", exception); }
    }

    private bool SwitchWorldIfNeeded(string worldGuid, string playersDirectory, DateTimeOffset now)
    {
        var normalizedWorld = worldGuid.Trim().ToUpperInvariant();
        if (string.Equals(_worldGuid, normalizedWorld, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(_playersDirectory, playersDirectory, StringComparison.OrdinalIgnoreCase)) return false;

        var changed = EndAllSessions(now);
        _worldGuid = normalizedWorld;
        _playersDirectory = playersDirectory;
        _savedIds.Clear();
        _onlineIds.Clear();
        _livePlayers.Clear();
        _sessionStarts.Clear();
        _presenceMode = PlayerPresenceMode.Unknown;
        _nextSaveScanUtc = DateTimeOffset.MinValue;
        EnsureWorld();
        return changed;
    }

    private bool ScanSaveFiles()
    {
        if (string.IsNullOrWhiteSpace(_playersDirectory)) return false;
        var discovered = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (Directory.Exists(_playersDirectory))
        {
            foreach (var path in Directory.EnumerateFiles(_playersDirectory, "*.sav", SearchOption.TopDirectoryOnly))
            {
                var name = Path.GetFileNameWithoutExtension(path);
                if (name.EndsWith("_dps", StringComparison.OrdinalIgnoreCase)) continue;
                var id = NormalizePlayerId(name);
                if (!string.IsNullOrWhiteSpace(id)) discovered.Add(id);
            }
        }

        var changed = !_savedIds.SetEquals(discovered);
        _savedIds.Clear();
        _savedIds.UnionWith(discovered);
        foreach (var id in discovered) EnsurePlayer(id);
        if (changed) _dirty = true;
        return changed;
    }

    private bool ApplyOnlineSnapshot(IReadOnlyList<RestPlayer> players, DateTimeOffset now)
    {
        var incoming = new Dictionary<string, RestPlayer>(StringComparer.OrdinalIgnoreCase);
        foreach (var player in players)
        {
            var id = NormalizePlayerId(player.PlayerId);
            if (string.IsNullOrWhiteSpace(id)) id = $"USER-{NormalizeFallback(player.UserId, player.Name)}";
            incoming[id] = player;
        }

        var importantChange = false;
        foreach (var id in _onlineIds.Where(id => !incoming.ContainsKey(id)).ToArray())
        {
            var record = EnsurePlayer(id);
            record.LastLeftUtc = now.ToUniversalTime();
            _sessionStarts.Remove(id);
            importantChange = _dirty = true;
        }

        foreach (var (id, player) in incoming)
        {
            var record = EnsurePlayer(id);
            if (_sessionStarts.TryAdd(id, now)) importantChange = _dirty = true;
            importantChange |= UpdateMetadata(record, player, now);
        }

        _onlineIds.Clear();
        _onlineIds.UnionWith(incoming.Keys);
        _livePlayers.Clear();
        foreach (var pair in incoming) _livePlayers[pair.Key] = pair.Value;
        _presenceMode = PlayerPresenceMode.OnlineSnapshot;
        return importantChange;
    }

    private bool UpdateMetadata(PlayerActivityRecord record, RestPlayer player, DateTimeOffset now)
    {
        var changed = false;
        if (Useful(player.Name) && !string.Equals(record.DisplayName, player.Name.Trim(), StringComparison.Ordinal))
        {
            record.DisplayName = player.Name.Trim();
            changed = true;
        }
        if (Useful(player.UserId) && !string.Equals(record.UserId, player.UserId.Trim(), StringComparison.Ordinal))
        {
            record.UserId = player.UserId.Trim();
            changed = true;
        }
        if (Useful(player.AccountName) && !string.Equals(record.AccountName, player.AccountName.Trim(), StringComparison.Ordinal))
        {
            record.AccountName = player.AccountName.Trim();
            changed = true;
        }
        if (player.Level > 0 && record.LastKnownLevel != player.Level)
        {
            record.LastKnownLevel = player.Level;
            changed = true;
        }
        record.FirstSeenUtc ??= now.ToUniversalTime();
        record.LastSeenUtc = now.ToUniversalTime();
        _dirty = true;
        return changed;
    }

    private bool EndAllSessions(DateTimeOffset now)
    {
        if (_sessionStarts.Count == 0) return false;
        foreach (var id in _sessionStarts.Keys)
        {
            var record = EnsurePlayer(id);
            record.LastLeftUtc = now.ToUniversalTime();
            record.LastSeenUtc ??= now.ToUniversalTime();
        }
        _sessionStarts.Clear();
        _dirty = true;
        return true;
    }

    private PlayerRosterSnapshot CreateSnapshot(DateTimeOffset now)
    {
        if (_document is null || string.IsNullOrWhiteSpace(_worldGuid)) return new PlayerRosterSnapshot([], 0, 0, false);
        var world = EnsureWorld();
        var visibleIds = new HashSet<string>(_savedIds, StringComparer.OrdinalIgnoreCase);
        visibleIds.UnionWith(_onlineIds);
        var presenceKnown = _presenceMode != PlayerPresenceMode.Unknown;

        var rows = visibleIds.Select(id =>
        {
            var record = EnsurePlayer(id);
            var online = presenceKnown && _onlineIds.Contains(id);
            var status = presenceKnown ? (online ? "在线" : "离线") : "未知";
            var hasSave = _savedIds.Contains(id);
            _livePlayers.TryGetValue(id, out var live);
            var account = Useful(record.AccountName) ? record.AccountName : Useful(record.UserId) ? record.UserId : "-";
            var lastOnline = online ? "在线" : record.LastSeenUtc?.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) ?? "未知";
            var duration = online && _sessionStarts.TryGetValue(id, out var started) ? FormatOnlineDuration(now - started) : "-";
            var playerMeta = hasSave ? "" : "尚未写入存档";
            return new PlayerInfo(
                status,
                Useful(record.DisplayName) ? record.DisplayName : "未知玩家",
                playerMeta,
                record.LastKnownLevel is { } level ? level.ToString(CultureInfo.InvariantCulture) : "-",
                online && live?.Ping is { } ping ? $"{ping:N0} ms" : "-",
                duration,
                lastOnline,
                account,
                record.PlayerId);
        })
        .OrderByDescending(row => row.Status == "在线")
        .ThenByDescending(row => row.Status == "未知")
        .ThenByDescending(row => ParseLastOnline(row.LastOnline))
        .ThenBy(row => row.Name, StringComparer.CurrentCultureIgnoreCase)
        .ToArray();

        return new PlayerRosterSnapshot(rows, presenceKnown ? _onlineIds.Count : 0, _savedIds.Count, presenceKnown);
    }

    private PlayerWorldActivity EnsureWorld()
    {
        if (_document is null || string.IsNullOrWhiteSpace(_worldGuid)) throw new InvalidOperationException("玩家档案尚未初始化。");
        if (!_document.Worlds.TryGetValue(_worldGuid, out var world))
        {
            world = new PlayerWorldActivity();
            _document.Worlds[_worldGuid] = world;
            _dirty = true;
        }
        return world;
    }

    private PlayerActivityRecord EnsurePlayer(string id)
    {
        var world = EnsureWorld();
        if (!world.Players.TryGetValue(id, out var record))
        {
            record = new PlayerActivityRecord { PlayerId = id };
            world.Players[id] = record;
            _dirty = true;
        }
        return record;
    }

    private async Task SaveDocumentAsync(CancellationToken cancellationToken)
    {
        if (!_dirty || _document is null) return;
        // SafeFileService itself is async and may capture a UI SynchronizationContext.
        // Run the persistence pipeline on the thread pool so a synchronous shutdown
        // wait can never form a UI-context cycle with the refresh gate.
        await Task.Run(
            () => files.WriteJsonAsync(context.StatePaths.PlayerActivityPath, _document, JsonOptions, keepPrevious: true, cancellationToken: cancellationToken),
            cancellationToken).ConfigureAwait(false);
        _dirty = false;
    }

    private void SaveDocument()
    {
        if (!_dirty || _document is null) return;
        Task.Run(() => files.WriteJsonAsync(context.StatePaths.PlayerActivityPath, _document, JsonOptions, keepPrevious: true))
            .GetAwaiter().GetResult();
        _dirty = false;
    }

    private static string NormalizePlayerId(string? value)
    {
        var normalized = (value ?? "").Trim();
        return normalized is "" or "-" ? "" : normalized.ToUpperInvariant();
    }

    private static string NormalizeFallback(string userId, string name)
    {
        var value = Useful(userId) ? userId : Useful(name) ? name : Guid.NewGuid().ToString("N");
        return value.Trim().ToUpperInvariant();
    }

    private static bool Useful(string? value) => !string.IsNullOrWhiteSpace(value) && value != "-";
    private static string FormatOnlineDuration(TimeSpan duration)
    {
        var totalMinutes = Math.Max(0, (int)Math.Floor(duration.TotalMinutes));
        return totalMinutes >= 60 ? $"{totalMinutes / 60}小时{totalMinutes % 60:00}分" : $"{totalMinutes} 分钟";
    }

    private static DateTimeOffset ParseLastOnline(string value) =>
        DateTimeOffset.TryParseExact(value, "yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var parsed)
            ? parsed
            : DateTimeOffset.MinValue;

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _gate.Wait();
        try
        {
            EndAllSessions(DateTimeOffset.Now);
            try { SaveDocument(); } catch { }
        }
        finally
        {
            _gate.Release();
        }
    }

    private sealed record RosterReadState(
        PlayerRosterSnapshot Snapshot,
        IReadOnlyDictionary<string, DateTimeOffset> SessionStarts)
    {
        public static RosterReadState Empty { get; } = new(
            new PlayerRosterSnapshot([], 0, 0, false),
            new Dictionary<string, DateTimeOffset>(StringComparer.OrdinalIgnoreCase));
    }
}

using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HaoHaoTianTian.PalHR.Models;
using HaoHaoTianTian.PalHR.Services;

namespace HaoHaoTianTian.PalHR.ViewModels;

public partial class MainViewModel : ObservableObject, IDisposable
{
    private readonly PalContext _context;
    private readonly ServerProcessService _processes;
    private readonly PalRestApiService _rest;
    private readonly SaveSlotService _slots;
    private readonly BackupService _backups;
    private readonly PlayerRosterService _roster;
    private readonly ServerManagerService _manager;
    private readonly SettingsService _settings;
    private readonly WorldSettingsService _worldSettings;
    private readonly KeepAwakeService _keepAwake;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly HashSet<int> _scheduleAnnouncements = [];
    private SaveSlotRegistry? _registry;
    private ServerProcessSnapshot? _previousProcess;
    private DateTimeOffset? _previousProcessAt;
    private DateTimeOffset _nextProcess;
    private DateTimeOffset _nextInfo;
    private DateTimeOffset _nextMetrics;
    private DateTimeOffset _nextPlayers;
    private DateTimeOffset _nextDisk;
    private DateTimeOffset _nextMonitorCycle;
    private DateTimeOffset _nextPlayerDurationUpdate;
    private DateTimeOffset? _scheduledAt;
    private double? _lastScheduleRemainingSeconds;
    private DateTimeOffset? _emptySince;
    private int _lastPlayerCount;
    private int _infoFailures;
    private int _metricsFailures;
    private int _playersFailures;
    private bool _hasRestSuccess;
    private int _automaticActionStarted;
    private bool _hasPlayerSnapshot;
    private bool _initializingSettings;
    private long _settingsWriteRevision;

    public MainViewModel(
        PalContext context,
        ServerProcessService processes,
        PalRestApiService rest,
        SaveSlotService slots,
        BackupService backups,
        PlayerRosterService roster,
        ServerManagerService manager,
        SettingsService settings,
        WorldSettingsService worldSettings,
        KeepAwakeService keepAwake)
    {
        _context = context;
        _processes = processes;
        _rest = rest;
        _slots = slots;
        _backups = backups;
        _roster = roster;
        _manager = manager;
        _settings = settings;
        _worldSettings = worldSettings;
        _keepAwake = keepAwake;
    }

    public Func<string, string, Task<bool>>? ConfirmAsync { get; set; }
    public Func<IReadOnlyList<BackupEntry>, bool, Task<BackupEntry?>>? ChooseBackupAsync { get; set; }
    public Func<WorldSettingsEditorSession, bool, Task<WorldSettingsEditorResult?>>? ShowWorldSettingsEditorAsync { get; set; }
    public event EventHandler? RequestClose;

    [ObservableProperty] public partial string ServerIdentity { get; set; } = "正在读取 ServerName…";
    [ObservableProperty] public partial string ServerState { get; set; } = "检测中";
    [ObservableProperty] public partial string PowerState { get; set; } = "";
    [ObservableProperty] public partial string ActiveSave { get; set; } = "-";
    [ObservableProperty] public partial string WorldUid { get; set; } = "-";
    [ObservableProperty] public partial string PlayerCount { get; set; } = "-";
    [ObservableProperty] public partial string PlayerRosterSummary { get; set; } = "正在读取存档…";
    [ObservableProperty] public partial string RestState { get; set; } = "检测中";
    [ObservableProperty] public partial string Fps { get; set; } = "-";
    [ObservableProperty] public partial string FrameTime { get; set; } = "-";
    [ObservableProperty] public partial string Cpu { get; set; } = "-";
    [ObservableProperty] public partial string Memory { get; set; } = "-";
    [ObservableProperty] public partial string Uptime { get; set; } = "-";
    [ObservableProperty] public partial string FreeDisk { get; set; } = "-";
    [ObservableProperty] public partial string StatusMessage { get; set; } = "正在连接服务器…";
    [ObservableProperty] public partial string UpdatedAt { get; set; } = "尚未刷新";
    [ObservableProperty] public partial DateTimeOffset ScheduleDate { get; set; } = DateTimeOffset.Now.Date;
    [ObservableProperty] public partial TimeSpan ScheduleTime { get; set; } = new(23, 30, 0);
    [ObservableProperty] public partial double ScheduleHour { get; set; } = 23;
    [ObservableProperty] public partial double ScheduleMinute { get; set; } = 30;
    [ObservableProperty] public partial bool SchedulePowerOff { get; set; } = true;
    [ObservableProperty] public partial bool IdleShutdownEnabled { get; set; } = true;
    [ObservableProperty] public partial double IdleShutdownMinutes { get; set; } = 15;
    [ObservableProperty] public partial bool IdleShutdownPowerOff { get; set; }
    [ObservableProperty] public partial bool IsBusy { get; set; }
    [ObservableProperty] public partial bool IsServerRunning { get; set; }
    [ObservableProperty] public partial bool HasSchedule { get; set; }

    public ObservableCollection<PlayerInfo> Players { get; } = [];

    public async Task InitializeAsync()
    {
        try
        {
            _initializingSettings = true;
            var saved = await _settings.LoadAsync(_lifetime.Token);
            IdleShutdownEnabled = saved.IdleShutdownEnabled;
            IdleShutdownMinutes = saved.IdleShutdownMinutes;
            IdleShutdownPowerOff = false;
            _initializingSettings = false;
            _registry = await _slots.LoadAsync(_lifetime.Token);
            UpdateSlotIdentity();
            ServerIdentity = $"{await _rest.GetConfiguredServerNameAsync(_lifetime.Token)} · 服务器版本待连接";
            await RefreshAllAsync(_lifetime.Token);
            StatusMessage = IsServerRunning ? "服务器状态已连接。" : "服务器当前未运行。";
            _ = RunMonitorAsync(_lifetime.Token);
            _ = RunAutomationClockAsync(_lifetime.Token);
        }
        catch (Exception exception)
        {
            _initializingSettings = false;
            StatusMessage = $"初始化失败：{exception.Message}";
            RestState = "不可用";
        }
    }

    public async Task ExecuteStartupChoiceAsync(StartupChoice choice)
    {
        switch (choice.Mode)
        {
            case StartupMode.Existing when choice.SlotId is { } slotId:
                await RunOperationAsync($"启动存档 {slotId}", token => _manager.SwitchAndStartAsync(slotId, token));
                break;
            case StartupMode.Create when choice.Tag is { } tag:
                await RunOperationAsync("创建并启动新存档", token => _manager.CreateAndStartAsync(
                    tag,
                    choice.WorldSettings ?? new NewWorldSettingsChoice(NewWorldSettingsMode.CopyCurrent),
                    token));
                break;
            case StartupMode.Rollback when choice.Backup is { } backup:
                await RunOperationAsync("回档并启动服务器", token => _manager.RestoreBackupAsync(_backups, backup, true, token));
                break;
            default:
                StatusMessage = "启动选择无效，未对存档执行操作。";
                break;
        }
        _registry = await _slots.LoadAsync(_lifetime.Token);
        UpdateSlotIdentity();
        ForceRefreshDue();
        await RefreshAllAsync(_lifetime.Token);
    }

    [RelayCommand]
    private async Task RefreshAsync()
    {
        if (IsBusy) return;
        StatusMessage = "正在刷新状态…";
        try
        {
            _registry = await _slots.LoadAsync(_lifetime.Token);
            UpdateSlotIdentity();
            await RefreshAllAsync(_lifetime.Token);
            StatusMessage = "状态已刷新。";
        }
        catch (Exception exception) { StatusMessage = $"刷新失败：{exception.Message}"; }
    }

    [RelayCommand]
    private async Task SaveAsync() => await RunOperationAsync("保存世界", token => _manager.SaveAsync(token));

    [RelayCommand]
    private async Task RestartAsync()
    {
        if (IsBusy || !IsServerRunning) return;
        if (!await Confirm("保存并重启", $"将保存当前存档 {ActiveSave}，全服广播后等待 10 秒正常关服，再以同一存档启动。\n\n当前在线玩家：{PlayerCount}\n\n继续吗？")) return;
        CancelScheduleCore("预约已因手动重启而取消。", false);
        await RunOperationAsync("保存并重启服务器", token => _manager.RestartAsync(token));
    }

    [RelayCommand]
    private async Task SaveStopAsync()
    {
        if (IsBusy || !IsServerRunning) return;
        if (!await Confirm("保存并关服", "将立即保存世界，全服广播后等待 10 秒正常关闭服务器。成功后管理器也会退出。\n\n继续吗？")) return;
        CancelScheduleCore("预约已因手动关服而取消。", false);
        var succeeded = await RunOperationAsync("保存并关闭服务器", token => _manager.SaveAndStopAsync(false, token), refreshAfterOperation: false);
        if (succeeded) RequestClose?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand]
    private async Task ForceStopAsync()
    {
        if (IsBusy || !IsServerRunning) return;
        if (!await Confirm("不保存强制关服", "确定立即强制关闭服务器吗？\n\n不会发送保存命令，上次自动保存之后的进度可能永久丢失。成功后管理器也会退出。")) return;
        CancelScheduleCore("预约已因强制关服而取消。", false);
        var succeeded = await RunOperationAsync("不保存强制关闭服务器", token => _manager.ForceStopAsync(token), refreshAfterOperation: false);
        if (succeeded) RequestClose?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand]
    private async Task RollbackAsync()
    {
        if (IsBusy || _registry is null || ChooseBackupAsync is null) return;
        try
        {
            var available = await _backups.ListAsync(_registry, _lifetime.Token);
            var wasRunning = IsServerRunning;
            var choice = await ChooseBackupAsync(available, wasRunning);
            if (choice is null) return;
            CancelScheduleCore("预约已因回档而取消。", false);
            await RunOperationAsync(wasRunning ? "保存、回档并重启服务器" : "恢复当前存档备份",
                token => _manager.RestoreBackupAsync(_backups, choice, wasRunning, token));
        }
        catch (Exception exception) { StatusMessage = $"无法开始回档：{exception.Message}"; }
    }

    [RelayCommand]
    private async Task EditWorldSettingsAsync()
    {
        if (IsBusy || _registry is null || ShowWorldSettingsEditorAsync is null) return;
        try
        {
            var slot = _slots.GetSlot(_registry, _registry.ActiveSlotId);
            var session = await _worldSettings.LoadEditorSessionAsync(slot, _lifetime.Token);
            var result = await ShowWorldSettingsEditorAsync(session, IsServerRunning);
            if (result is null) return;
            if (result.RestartRequested && IsServerRunning &&
                !await Confirm("保存世界设置并重启", $"将先保存当前存档 {ActiveSave}，全服广播后正常关服，再应用新设置并启动。\n\n当前在线玩家：{PlayerCount}\n\n继续吗？")) return;
            await _worldSettings.SaveEditorResultAsync(slot, result, _lifetime.Token);
            if (result.RestartRequested && IsServerRunning)
            {
                CancelScheduleCore("预约已因世界设置重启而取消。", false);
                await RunOperationAsync("应用世界设置并重启服务器", token => _manager.RestartAsync(token));
            }
            else
            {
                StatusMessage = IsServerRunning
                    ? "世界设置已保存；修改将在服务器下次启动后生效。"
                    : "世界设置已保存；下次启动此存档时生效。";
            }
        }
        catch (Exception exception) { StatusMessage = $"世界设置操作失败：{exception.Message}"; }
    }

    [RelayCommand]
    private async Task SetScheduleAsync()
    {
        if (IsBusy || !IsServerRunning) return;
        if (double.IsNaN(ScheduleHour) || double.IsNaN(ScheduleMinute))
        {
            StatusMessage = "请输入完整的预约小时和分钟。";
            return;
        }
        var hour = (int)Math.Clamp(Math.Truncate(ScheduleHour), 0, 23);
        var minute = (int)Math.Clamp(Math.Truncate(ScheduleMinute), 0, 59);
        ScheduleHour = hour;
        ScheduleMinute = minute;
        ScheduleTime = new TimeSpan(hour, minute, 0);
        var localDateTime = DateTime.SpecifyKind(ScheduleDate.Date.Add(ScheduleTime), DateTimeKind.Unspecified);
        var target = new DateTimeOffset(localDateTime, TimeZoneInfo.Local.GetUtcOffset(localDateTime));
        if (target <= DateTimeOffset.Now)
        {
            StatusMessage = "预约时间必须晚于现在。";
            return;
        }
        _scheduledAt = target;
        _lastScheduleRemainingSeconds = (target - DateTimeOffset.Now).TotalSeconds;
        HasSchedule = true;
        _scheduleAnnouncements.Clear();
        _emptySince = null;
        Interlocked.Exchange(ref _automaticActionStarted, 0);
        var action = SchedulePowerOff ? "保存、关服并关闭电脑" : "保存并关服，电脑保持开启";
        StatusMessage = $"已预约 {target:yyyy-MM-dd HH:mm}：{action}。";
        await AnnounceSafeAsync($"Server shutdown scheduled for {target:yyyy-MM-dd HH:mm} (server local time). The world will be saved first.");
    }

    [RelayCommand]
    private void CancelSchedule() => CancelScheduleCore("预约已取消。", true);

    partial void OnIdleShutdownEnabledChanged(bool value)
    {
        _emptySince = null;
        if (!_initializingSettings) _ = PersistSettingsAsync();
        StatusMessage = value ? "空服自动处理已开启；将在成功读取玩家状态后计时。" : "空服自动处理已关闭。";
    }

    partial void OnIdleShutdownMinutesChanged(double value)
    {
        if (_initializingSettings || !double.IsFinite(value) || value < 1 || value > 1440) return;

        if (IdleShutdownEnabled && IsServerRunning && _hasPlayerSnapshot && _lastPlayerCount == 0 && _scheduledAt is null)
        {
            _emptySince = DateTimeOffset.Now;
            Interlocked.Exchange(ref _automaticActionStarted, 0);
            var duration = TimeSpan.FromMinutes(Math.Round(value));
            StatusMessage = $"当前无人在线，{FormatCountdown(duration)} 后自动保存并关服{(IdleShutdownPowerOff ? "、关闭电脑" : "")}；有玩家加入会取消计时。";
        }
        else
        {
            _emptySince = null;
        }

        var revision = Interlocked.Increment(ref _settingsWriteRevision);
        _ = PersistSettingsAfterDebounceAsync(revision);
    }

    partial void OnIdleShutdownPowerOffChanged(bool value)
    {
        if (_initializingSettings) return;
        StatusMessage = value ? "本次已开启：空服保存关服后关闭电脑；重新打开管理器后恢复为关闭。" : "空服处理将只保存并关服，电脑保持开启。";
    }

    private async Task<bool> RunOperationAsync(string description, Func<CancellationToken, Task> operation, bool refreshAfterOperation = true)
    {
        if (IsBusy) return false;
        IsBusy = true;
        StatusMessage = $"{description}，请稍候…";
        var succeeded = false;
        try
        {
            await operation(_lifetime.Token);
            succeeded = true;
            StatusMessage = $"{description}已完成。";
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception exception) { StatusMessage = $"{description}失败：{exception.Message}"; }
        finally
        {
            IsBusy = false;
            ForceRefreshDue();
            try
            {
                if (refreshAfterOperation)
                {
                    _registry = await _slots.LoadAsync(_lifetime.Token);
                    UpdateSlotIdentity();
                    await RefreshAllAsync(_lifetime.Token);
                }
            }
            catch { }
        }
        return succeeded;
    }

    private async Task RefreshAllAsync(CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.Now;
        await RefreshProcessAsync(now, cancellationToken);
        await RefreshDiskAsync(cancellationToken);
        if (IsServerRunning)
        {
            await RefreshInfoAsync(cancellationToken);
            await RefreshMetricsAsync(cancellationToken);
            await RefreshPlayersAsync(cancellationToken);
        }
        else
        {
            ResetStoppedDisplay();
            await RefreshRosterAsync(null, PlayerPresenceMode.ServerStopped, now, cancellationToken);
        }
        SetRefreshDeadlines(now);
        UpdatedAt = $"更新于 {DateTime.Now:HH:mm:ss}";
    }

    private async Task RunMonitorAsync(CancellationToken cancellationToken)
    {
        var recoveringFromFailure = false;
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var now = DateTimeOffset.Now;
                if (now >= _nextMonitorCycle && !IsBusy)
                {
                    _nextMonitorCycle = now.AddSeconds(10);
                    if (now >= _nextProcess) await RefreshProcessAsync(now, cancellationToken);
                    if (now >= _nextDisk) await RefreshDiskAsync(cancellationToken);
                    if (IsServerRunning)
                    {
                        if (now >= _nextPlayers) await RefreshPlayersAsync(cancellationToken);
                        if (now >= _nextMetrics) await RefreshMetricsAsync(cancellationToken);
                        else if (now >= _nextInfo) await RefreshInfoAsync(cancellationToken);
                    }
                    else ResetStoppedDisplay();
                    UpdatedAt = $"更新于 {DateTime.Now:HH:mm:ss}";
                    if (recoveringFromFailure)
                    {
                        StatusMessage = IsServerRunning ? "服务器状态监测已恢复。" : "服务器状态监测已恢复；服务器当前未运行。";
                        recoveringFromFailure = false;
                    }
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { break; }
            catch (Exception exception)
            {
                recoveringFromFailure = true;
                StatusMessage = $"状态刷新暂时失败，将自动重试：{exception.Message}";
            }

            try { await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { break; }
        }
    }

    private async Task RunAutomationClockAsync(CancellationToken cancellationToken)
    {
        var recoveringFromFailure = false;
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var now = DateTimeOffset.Now;
                if (_scheduledAt is { } scheduled)
                {
                    var remaining = scheduled - now;
                    if (!IsBusy && remaining > TimeSpan.Zero)
                        StatusMessage = $"已预约 {scheduled:yyyy-MM-dd HH:mm}，剩余 {FormatCountdown(remaining)}；空服计时暂不生效。";
                    var previousRemaining = _lastScheduleRemainingSeconds;
                    foreach (var threshold in new[] { 600, 300, 60, 10 })
                    {
                        if (previousRemaining is { } previous && previous > threshold &&
                            remaining.TotalSeconds <= threshold && remaining.TotalSeconds > 0 &&
                            _scheduleAnnouncements.Add(threshold))
                            _ = AnnounceSafeAsync($"Server shutdown in {FormatAnnouncement(threshold)}. The world will be saved first.");
                    }
                    _lastScheduleRemainingSeconds = remaining.TotalSeconds;
                    if (remaining <= TimeSpan.Zero && Interlocked.CompareExchange(ref _automaticActionStarted, 1, 0) == 0)
                    {
                        var powerOff = SchedulePowerOff;
                        _scheduledAt = null;
                        _lastScheduleRemainingSeconds = null;
                        HasSchedule = false;
                        await RunOperationAsync(powerOff ? "预约保存关服并关闭电脑" : "预约保存并关服",
                            token => _manager.SaveAndStopAsync(powerOff, token));
                    }
                }
                else if (IdleShutdownEnabled && IsServerRunning && _hasPlayerSnapshot && _lastPlayerCount == 0 && _emptySince is { } emptySince)
                {
                    var duration = TimeSpan.FromMinutes(Math.Clamp(Math.Round(IdleShutdownMinutes), 1, 1440));
                    var remaining = duration - (now - emptySince);
                    if (!IsBusy && remaining > TimeSpan.Zero)
                        StatusMessage = $"当前无人在线，{FormatCountdown(remaining)} 后自动保存并关服{(IdleShutdownPowerOff ? "、关闭电脑" : "")}；有玩家加入会取消计时。";
                    if (remaining <= TimeSpan.Zero && Interlocked.CompareExchange(ref _automaticActionStarted, 1, 0) == 0)
                    {
                        if (await ConfirmEmptyServerAsync(cancellationToken))
                        {
                            var powerOff = IdleShutdownPowerOff;
                            await RunOperationAsync(powerOff ? "空服保存关服并关闭电脑" : "空服保存并关服",
                                token => _manager.SaveAndStopAsync(powerOff, token));
                        }
                        else
                        {
                            _emptySince = null;
                            Interlocked.Exchange(ref _automaticActionStarted, 0);
                        }
                    }
                }
                if (now >= _nextPlayerDurationUpdate && Players.Any(player => player.Status == "在线"))
                {
                    ApplyPlayerSnapshot(_roster.GetCurrentSnapshot(now));
                    _nextPlayerDurationUpdate = now.AddMinutes(1);
                }
                if (recoveringFromFailure)
                {
                    StatusMessage = "自动操作计时已恢复。";
                    recoveringFromFailure = false;
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { break; }
            catch (Exception exception)
            {
                recoveringFromFailure = true;
                StatusMessage = $"自动操作计时暂时失败，将自动重试：{exception.Message}";
            }

            try { await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { break; }
        }
    }

    private async Task RefreshProcessAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        var snapshot = await _processes.GetSnapshotAsync(cancellationToken);
        var wasRunning = IsServerRunning;
        IsServerRunning = snapshot.IsRunning;
        ServerState = snapshot.IsRunning ? "运行中" : "已关闭";
        PowerState = snapshot.IsRunning ? "允许息屏 · 阻止睡眠" : "";
        Memory = snapshot.IsRunning ? $"{snapshot.MemoryMb:N0} MB" : "-";
        if (_previousProcess is { } previous && _previousProcessAt is { } previousAt && snapshot.IsRunning)
        {
            var elapsed = (now - previousAt).TotalSeconds;
            var cpuDelta = snapshot.TotalCpuSeconds - previous.TotalCpuSeconds;
            Cpu = elapsed > 0 && cpuDelta >= 0 ? $"{Math.Clamp(cpuDelta / elapsed * 100 / Environment.ProcessorCount, 0, 100):N1}%" : "-";
        }
        else Cpu = snapshot.IsRunning ? "采样中" : "-";
        if (snapshot.StartedAt is { } started) Uptime = FormatDuration(now - started);
        _previousProcess = snapshot;
        _previousProcessAt = now;
        _nextProcess = now.AddSeconds(10);

        if (snapshot.IsRunning && !wasRunning)
        {
            await _keepAwake.EnsureRunningAsync(cancellationToken);
            _nextInfo = _nextMetrics = _nextPlayers = DateTimeOffset.MinValue;
            _hasPlayerSnapshot = false;
            _emptySince = null;
            Interlocked.Exchange(ref _automaticActionStarted, 0);
        }
        else if (!snapshot.IsRunning && wasRunning)
        {
            _emptySince = null;
            _hasPlayerSnapshot = false;
            Interlocked.Exchange(ref _automaticActionStarted, 0);
            await RefreshRosterAsync(null, PlayerPresenceMode.ServerStopped, now, cancellationToken);
        }
    }

    private async Task RefreshInfoAsync(CancellationToken cancellationToken)
    {
        try
        {
            var info = await _rest.GetInfoAsync(cancellationToken);
            ServerIdentity = $"{info.ServerName} · {info.Version}";
            if (!string.IsNullOrWhiteSpace(info.WorldGuid)) WorldUid = info.WorldGuid;
            MarkRestSuccess(RestEndpoint.Info);
        }
        catch (Exception exception) when (exception is not OperationCanceledException) { MarkRestFailure(RestEndpoint.Info, exception); }
        finally { _nextInfo = DateTimeOffset.Now.AddSeconds(60); }
    }

    private async Task RefreshMetricsAsync(CancellationToken cancellationToken)
    {
        try
        {
            var metrics = await _rest.GetMetricsAsync(cancellationToken);
            Fps = metrics.ServerFps is { } fps ? $"{fps:N1}" : "-";
            FrameTime = metrics.ServerFrameTime is { } frame ? $"{frame:N2} ms" : "-";
            if (metrics.CurrentPlayers is { } current)
            {
                _lastPlayerCount = current;
                PlayerCount = metrics.MaxPlayers is { } max ? $"{current} / {max}" : current.ToString();
            }
            if (metrics.UptimeSeconds is { } seconds) Uptime = FormatDuration(TimeSpan.FromSeconds(seconds));
            MarkRestSuccess(RestEndpoint.Metrics);
        }
        catch (Exception exception) when (exception is not OperationCanceledException) { MarkRestFailure(RestEndpoint.Metrics, exception); }
        finally { _nextMetrics = DateTimeOffset.Now.AddSeconds(20); }
    }

    private async Task RefreshPlayersAsync(CancellationToken cancellationToken)
    {
        try
        {
            var players = await _rest.GetPlayersAsync(cancellationToken);
            await RefreshRosterAsync(players, PlayerPresenceMode.OnlineSnapshot, DateTimeOffset.Now, cancellationToken);
            _lastPlayerCount = players.Count;
            _hasPlayerSnapshot = true;
            if (_lastPlayerCount > 0)
            {
                _emptySince = null;
                Interlocked.Exchange(ref _automaticActionStarted, 0);
            }
            else if (IdleShutdownEnabled && _scheduledAt is null)
            {
                _emptySince ??= DateTimeOffset.Now;
            }
            MarkRestSuccess(RestEndpoint.Players);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            MarkRestFailure(RestEndpoint.Players, exception);
            await RefreshRosterAsync(null, PlayerPresenceMode.Unknown, DateTimeOffset.Now, cancellationToken);
        }
        finally { _nextPlayers = DateTimeOffset.Now.AddSeconds(10); }
    }

    private Task RefreshDiskAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var root = Path.GetPathRoot(_context.ServerPaths.ServerRoot) ?? _context.ServerPaths.ServerRoot;
        var drive = new DriveInfo(root);
        FreeDisk = drive.IsReady ? $"{drive.AvailableFreeSpace / (1024d * 1024d * 1024d):N1} GB" : "-";
        _nextDisk = DateTimeOffset.Now.AddSeconds(60);
        return Task.CompletedTask;
    }

    private async Task<bool> ConfirmEmptyServerAsync(CancellationToken cancellationToken)
    {
        try
        {
            var players = await _rest.GetPlayersAsync(cancellationToken);
            _lastPlayerCount = players.Count;
            _hasPlayerSnapshot = true;
            return players.Count == 0;
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            MarkRestFailure(RestEndpoint.Players, exception);
            StatusMessage = "空服确认失败，自动关服已推迟；将在重新确认空服后重新计时。";
            return false;
        }
    }

    private void MarkRestSuccess(RestEndpoint endpoint)
    {
        SetFailures(endpoint, 0);
        _hasRestSuccess = true;
        UpdateRestState();
    }

    private void MarkRestFailure(RestEndpoint endpoint, Exception exception)
    {
        var failures = GetFailures(endpoint) + 1;
        SetFailures(endpoint, failures);
        UpdateRestState();
        if (failures >= 2) StatusMessage = $"REST {endpoint} 连续失败：{exception.Message}";
    }

    private void ResetStoppedDisplay()
    {
        RestState = "已关闭";
        Fps = FrameTime = Cpu = Memory = Uptime = "-";
        PlayerCount = "-";
        _infoFailures = _metricsFailures = _playersFailures = 0;
        _hasRestSuccess = false;
    }

    private async Task RefreshRosterAsync(
        IReadOnlyList<RestPlayer>? onlinePlayers,
        PlayerPresenceMode mode,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        if (_registry is null) return;
        var slot = _slots.GetSlot(_registry, _registry.ActiveSlotId);
        var playersDirectory = Path.Combine(_slots.GetSlotPath(_registry, slot.Id), slot.WorldGuid, "Players");
        var snapshot = await _roster.RefreshAsync(slot.WorldGuid, playersDirectory, onlinePlayers, mode, now, cancellationToken);
        ApplyPlayerSnapshot(snapshot);
        _nextPlayerDurationUpdate = now.AddMinutes(1);
    }

    private void ApplyPlayerSnapshot(PlayerRosterSnapshot snapshot)
    {
        if (!Players.SequenceEqual(snapshot.Players))
        {
            Players.Clear();
            foreach (var player in snapshot.Players) Players.Add(player);
        }
        PlayerRosterSummary = snapshot.PresenceKnown
            ? $"在线 {snapshot.OnlineCount} · 存档 {snapshot.SavedCount}"
            : $"在线状态未知 · 存档 {snapshot.SavedCount}";
    }

    private int GetFailures(RestEndpoint endpoint) => endpoint switch
    {
        RestEndpoint.Info => _infoFailures,
        RestEndpoint.Metrics => _metricsFailures,
        _ => _playersFailures
    };

    private void SetFailures(RestEndpoint endpoint, int value)
    {
        if (endpoint == RestEndpoint.Info) _infoFailures = value;
        else if (endpoint == RestEndpoint.Metrics) _metricsFailures = value;
        else _playersFailures = value;
    }

    private void UpdateRestState()
    {
        RestState = _infoFailures >= 2 || _metricsFailures >= 2 || _playersFailures >= 2
            ? (_hasRestSuccess ? "部分异常" : "异常")
            : (_hasRestSuccess ? "正常" : "重试中");
    }

    private void UpdateSlotIdentity()
    {
        if (_registry is null) return;
        var slot = _slots.GetSlot(_registry, _registry.ActiveSlotId);
        ActiveSave = $"{slot.Id} · {slot.Tag}";
        WorldUid = slot.WorldGuid;
    }

    private void SetRefreshDeadlines(DateTimeOffset now)
    {
        _nextProcess = now.AddSeconds(10);
        _nextMetrics = now.AddSeconds(20);
        _nextPlayers = now.AddSeconds(10);
        _nextInfo = now.AddSeconds(60);
        _nextDisk = now.AddSeconds(60);
        _nextMonitorCycle = now.AddSeconds(10);
    }

    private void ForceRefreshDue() =>
        _nextProcess = _nextInfo = _nextMetrics = _nextPlayers = _nextDisk = _nextMonitorCycle = DateTimeOffset.MinValue;

    private void CancelScheduleCore(string message, bool updateMessage)
    {
        _scheduledAt = null;
        _lastScheduleRemainingSeconds = null;
        HasSchedule = false;
        _scheduleAnnouncements.Clear();
        Interlocked.Exchange(ref _automaticActionStarted, 0);
        if (updateMessage) StatusMessage = message;
    }

    private async Task PersistSettingsAsync()
    {
        try
        {
            await _settings.SaveAsync(new ManagerSettings
            {
                IdleShutdownEnabled = IdleShutdownEnabled,
                IdleShutdownMinutes = (int)Math.Clamp(Math.Round(IdleShutdownMinutes), 1, 1440)
            }, _lifetime.Token);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception exception) { StatusMessage = $"保存空服设置失败：{exception.Message}"; }
    }

    private async Task PersistSettingsAfterDebounceAsync(long revision)
    {
        try
        {
            await Task.Delay(TimeSpan.FromMilliseconds(400), _lifetime.Token);
            if (revision == Interlocked.Read(ref _settingsWriteRevision)) await PersistSettingsAsync();
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
    }

    private async Task AnnounceSafeAsync(string message)
    {
        if (!IsServerRunning) return;
        try { await _rest.AnnounceAsync(message, _lifetime.Token); }
        catch (Exception exception) when (exception is not OperationCanceledException) { StatusMessage = $"预约已设置，但广播失败：{exception.Message}"; }
    }

    private async Task<bool> Confirm(string title, string message) =>
        ConfirmAsync is not null && await ConfirmAsync(title, message);

    private static string FormatAnnouncement(int seconds) => seconds >= 60 ? $"{seconds / 60} minute{(seconds == 60 ? "" : "s")}" : $"{seconds} seconds";
    private static string FormatCountdown(TimeSpan value) => value.TotalHours >= 1 ? $"{(int)value.TotalHours:00}:{value.Minutes:00}:{value.Seconds:00}" : $"{Math.Max(0, value.Minutes):00}:{Math.Max(0, value.Seconds):00}";
    private static string FormatDuration(TimeSpan value) => value.TotalDays >= 1 ? $"{(int)value.TotalDays}天 {value:hh\\:mm\\:ss}" : value.ToString(@"hh\:mm\:ss");

    public void Dispose()
    {
        _lifetime.Cancel();
        _roster.Dispose();
        _lifetime.Dispose();
    }

    private enum RestEndpoint { Info, Metrics, Players }
}

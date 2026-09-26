using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using HaoHaoTianTian.PalHR.Models;

namespace HaoHaoTianTian.PalHR.Services;

public sealed partial class PalServerBuildService(PalContext context, SafeFileService files)
{
    public async Task<string> GetCurrentBuildAsync(CancellationToken cancellationToken = default)
    {
        var steamApps = Directory.GetParent(Directory.GetParent(context.ServerPaths.ServerRoot)?.FullName ?? "")?.FullName;
        var manifest = steamApps is null ? null : Path.Combine(steamApps, "appmanifest_2394010.acf");
        if (manifest is not null && File.Exists(manifest))
        {
            var content = await File.ReadAllTextAsync(manifest, cancellationToken);
            var match = BuildIdRegex().Match(content);
            if (match.Success) return "steam:" + match.Groups[1].Value;
        }
        if (File.Exists(context.ServerPaths.ServerExe))
        {
            var version = FileVersionInfo.GetVersionInfo(context.ServerPaths.ServerExe).FileVersion;
            if (!string.IsNullOrWhiteSpace(version)) return "file:" + version;
            await using var stream = new FileStream(context.ServerPaths.ServerExe, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, true);
            return "sha256:" + Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken));
        }
        throw new FileNotFoundException("找不到 PalServer.exe，无法识别服务器版本。", context.ServerPaths.ServerExe);
    }

    public async Task<BuildStateDocument> LoadAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(context.StatePaths.BuildStatePath)) return new BuildStateDocument();
        try
        {
            await using var stream = new FileStream(context.StatePaths.BuildStatePath, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, true);
            return await JsonSerializer.DeserializeAsync<BuildStateDocument>(stream, SafeFileService.DefaultJsonOptions, cancellationToken)
                   ?? throw new InvalidOperationException("版本状态文件为空。");
        }
        catch (JsonException exception) { throw new InvalidOperationException($"WorldBuildState.json 已损坏：{exception.Message}", exception); }
    }

    public async Task<bool> NeedsProtectionAsync(string worldUid, string build, CancellationToken cancellationToken = default)
    {
        var state = await LoadAsync(cancellationToken);
        return !state.Worlds.TryGetValue(worldUid, out var item) || !string.Equals(item.LastSuccessfulBuild, build, StringComparison.Ordinal);
    }

    public async Task MarkSuccessfulAsync(SaveSlot slot, string build, CancellationToken cancellationToken = default)
    {
        var state = await LoadAsync(cancellationToken);
        state.Worlds[slot.WorldGuid] = new WorldBuildState { SlotId = slot.Id, WorldUid = slot.WorldGuid, LastSuccessfulBuild = build, VerifiedUtc = DateTimeOffset.UtcNow };
        await files.WriteJsonAsync(context.StatePaths.BuildStatePath, state, keepPrevious: true, cancellationToken: cancellationToken);
    }

    [GeneratedRegex("\"buildid\"\\s+\"(\\d+)\"", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)] private static partial Regex BuildIdRegex();
}

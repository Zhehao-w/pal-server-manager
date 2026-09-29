using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using HaoHaoTianTian.PalHR.Models;

namespace HaoHaoTianTian.PalHR.Services;

public sealed partial class SaveSlotService(PalContext context, ServerProcessService processes, LoggingService log, SafeFileService files)
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public async Task<SaveSlotRegistry> LoadAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(context.StatePaths.RegistryPath))
            throw new InvalidOperationException("管理状态缺少 SaveSlots.json；请先从服务器设置中接入现有世界，不能自动重建登记。");
        try
        {
            await using var stream = new FileStream(context.StatePaths.RegistryPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 4096, true);
            var registry = await JsonSerializer.DeserializeAsync<SaveSlotRegistry>(stream, JsonOptions, cancellationToken) ?? throw new InvalidOperationException("SaveSlots.json 为空。 ");
            ValidateRegistry(registry);
            return registry;
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException)
        {
            throw new InvalidOperationException($"SaveSlots.json 无效：{exception.Message}", exception);
        }
    }

    public async Task SaveAsync(SaveSlotRegistry registry, CancellationToken cancellationToken = default)
    {
        ValidateRegistry(registry);
        await files.WriteJsonAsync(context.StatePaths.RegistryPath, registry, JsonOptions, keepPrevious: true, cancellationToken);
    }

    public async Task<IReadOnlyList<SaveSlotDisplay>> GetDisplaysAsync(SaveSlotRegistry registry, CancellationToken cancellationToken = default)
    {
        return await Task.Run(() => registry.Slots.OrderBy(slot => slot.Id).Select(slot =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var path = GetSlotPath(registry, slot.Id);
            var levelPath = Path.Combine(path, slot.WorldGuid, "Level.sav");
            if (!File.Exists(levelPath)) return new SaveSlotDisplay(slot.Id, slot.Tag, slot.Id == registry.ActiveSlotId ? "当前" : "待选", slot.WorldGuid, "-", "-", "尚未检测到存档");
            var level = new FileInfo(levelPath);
            var playersPath = Path.Combine(path, slot.WorldGuid, "Players");
            var playerCount = Directory.Exists(playersPath) ? Directory.EnumerateFiles(playersPath, "*.sav").Count(file => !Path.GetFileName(file).EndsWith("_dps.sav", StringComparison.OrdinalIgnoreCase)) : 0;
            return new SaveSlotDisplay(slot.Id, slot.Tag, slot.Id == registry.ActiveSlotId ? "当前" : "待选", slot.WorldGuid, $"{level.Length / (1024d * 1024d):N2} MB", playerCount.ToString(CultureInfo.InvariantCulture), level.LastWriteTime.ToString("yyyy-MM-dd HH:mm"));
        }).ToArray(), cancellationToken);
    }

    public SaveSlot GetSlot(SaveSlotRegistry registry, int slotId) =>
        registry.Slots.FirstOrDefault(slot => slot.Id == slotId) ?? throw new InvalidOperationException($"存档 {slotId} 未登记。 ");

    public string GetSlotPath(SaveSlotRegistry registry, int slotId)
    {
        var slot = GetSlot(registry, slotId);
        return slotId == registry.ActiveSlotId ? context.ServerPaths.ActivePath : Path.Combine(context.ServerPaths.SaveRoot, slot.ParkedFolder);
    }

    public async Task RenameTagAsync(int slotId, string tag, CancellationToken cancellationToken = default)
    {
        if (processes.GetSnapshot().IsRunning) throw new InvalidOperationException("修改标签前必须先关闭服务器。 ");
        var newTag = ValidateTag(tag);
        var registry = await LoadAsync(cancellationToken);
        var slot = GetSlot(registry, slotId);
        var oldTag = slot.Tag;
        var oldParked = slot.ParkedFolder;
        var newParked = NewParkedFolderName(slotId, newTag);
        var oldPath = PathSafety.RequireInside(context.ServerPaths.SaveRoot, Path.Combine(context.ServerPaths.SaveRoot, oldParked));
        var newPath = PathSafety.RequireInside(context.ServerPaths.SaveRoot, Path.Combine(context.ServerPaths.SaveRoot, newParked));
        var parkedNameChanged = !string.Equals(oldPath, newPath, StringComparison.OrdinalIgnoreCase);
        var moved = false;

        if (parkedNameChanged && (Directory.Exists(newPath) || File.Exists(newPath)))
            throw new IOException($"新标签与已有文件夹冲突：{newPath}");

        if (slotId != registry.ActiveSlotId && parkedNameChanged)
        {
            if (!Directory.Exists(oldPath)) throw new DirectoryNotFoundException($"找不到停放存档：{oldPath}");
            await Task.Run(() => Directory.Move(oldPath, newPath), cancellationToken);
            moved = true;
        }

        try
        {
            slot.Tag = newTag;
            slot.ParkedFolder = newParked;
            await SaveAsync(registry, cancellationToken);
        }
        catch
        {
            slot.Tag = oldTag;
            slot.ParkedFolder = oldParked;
            if (moved && Directory.Exists(newPath) && !Directory.Exists(oldPath)) Directory.Move(newPath, oldPath);
            throw;
        }
        await log.WriteAsync($"Save {slotId} label changed from \"{oldTag}\" to \"{newTag}\".", cancellationToken);
    }

    public async Task<SaveSlotRegistry> SwitchAsync(SaveSlotRegistry registry, int targetSlotId, CancellationToken cancellationToken = default)
    {
        if (processes.GetSnapshot().IsRunning) throw new InvalidOperationException("切换存档前必须先关闭服务器。 ");
        var currentId = registry.ActiveSlotId;
        var target = GetSlot(registry, targetSlotId);
        ValidateSlot(registry, targetSlotId);
        if (currentId == targetSlotId)
        {
            await SetWorldGuidAsync(target.WorldGuid, cancellationToken);
            await log.WriteAsync($"Save {targetSlotId} is already active; DedicatedServerName was verified.", cancellationToken);
            return registry;
        }

        ValidateSlot(registry, currentId);
        var current = GetSlot(registry, currentId);
        var targetPath = PathSafety.RequireInside(context.ServerPaths.SaveRoot, GetSlotPath(registry, targetSlotId));
        var currentParked = PathSafety.RequireInside(context.ServerPaths.SaveRoot, Path.Combine(context.ServerPaths.SaveRoot, current.ParkedFolder));
        var temporary = PathSafety.RequireInside(context.ServerPaths.SaveRoot, Path.Combine(context.ServerPaths.SaveRoot, $".pal-switch-{Guid.NewGuid():N}"));
        if (Directory.Exists(currentParked)) throw new IOException($"无法停放当前存档，目标已存在：{currentParked}");

        await log.WriteAsync($"Switching from save {currentId} to save {targetSlotId}...", cancellationToken);
        await Task.Run(() =>
        {
            Directory.Move(context.ServerPaths.ActivePath, temporary);
            try
            {
                Directory.Move(targetPath, context.ServerPaths.ActivePath);
                Directory.Move(temporary, currentParked);
            }
            catch
            {
                if (Directory.Exists(temporary) && !Directory.Exists(context.ServerPaths.ActivePath)) Directory.Move(temporary, context.ServerPaths.ActivePath);
                else if (Directory.Exists(temporary) && Directory.Exists(context.ServerPaths.ActivePath) && !Directory.Exists(targetPath))
                {
                    Directory.Move(context.ServerPaths.ActivePath, targetPath);
                    Directory.Move(temporary, context.ServerPaths.ActivePath);
                }
                throw;
            }
        }, cancellationToken);

        try
        {
            await SetWorldGuidAsync(target.WorldGuid, cancellationToken);
        }
        catch
        {
            await Task.Run(() =>
            {
                Directory.Move(context.ServerPaths.ActivePath, temporary);
                Directory.Move(currentParked, context.ServerPaths.ActivePath);
                Directory.Move(temporary, targetPath);
            }, CancellationToken.None);
            await SetWorldGuidAsync(current.WorldGuid, CancellationToken.None);
            throw;
        }

        registry.ActiveSlotId = targetSlotId;
        try { await SaveAsync(registry, cancellationToken); }
        catch
        {
            registry.ActiveSlotId = currentId;
            await Task.Run(() =>
            {
                Directory.Move(context.ServerPaths.ActivePath, temporary);
                Directory.Move(currentParked, context.ServerPaths.ActivePath);
                Directory.Move(temporary, targetPath);
            }, CancellationToken.None);
            await SetWorldGuidAsync(current.WorldGuid, CancellationToken.None);
            throw;
        }
        await log.WriteAsync($"Save {targetSlotId} is now active.", cancellationToken);
        return registry;
    }

    public async Task<string> GetWorldGuidAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(context.ServerPaths.UserSettingsPath)) throw new FileNotFoundException("找不到 GameUserSettings.ini。", context.ServerPaths.UserSettingsPath);
        var content = await File.ReadAllTextAsync(context.ServerPaths.UserSettingsPath, cancellationToken);
        var match = DedicatedServerNameRegex().Match(content);
        if (!match.Success) throw new InvalidOperationException("GameUserSettings.ini 中缺少 DedicatedServerName。 ");
        return match.Groups[1].Value.Trim().ToUpperInvariant();
    }

    public async Task SetWorldGuidAsync(string worldGuid, CancellationToken cancellationToken = default)
    {
        var normalized = ValidateWorldGuid(worldGuid);
        var content = await File.ReadAllTextAsync(context.ServerPaths.UserSettingsPath, cancellationToken);
        if (!DedicatedServerNameRegex().IsMatch(content)) throw new InvalidOperationException("GameUserSettings.ini 中缺少 DedicatedServerName。 ");
        var updated = DedicatedServerNameRegex().Replace(content, $"DedicatedServerName={normalized}", 1);
        await files.WriteTextAsync(context.ServerPaths.UserSettingsPath, updated, new UTF8Encoding(false), keepPrevious: true, cancellationToken);
    }

    public async Task ClearWorldGuidForGenerationAsync(CancellationToken cancellationToken = default)
    {
        var content = await File.ReadAllTextAsync(context.ServerPaths.UserSettingsPath, cancellationToken);
        if (!DedicatedServerNameRegex().IsMatch(content)) throw new InvalidOperationException("GameUserSettings.ini 中缺少 DedicatedServerName。");
        var updated = DedicatedServerNameRegex().Replace(content, "DedicatedServerName=", 1);
        await files.WriteTextAsync(context.ServerPaths.UserSettingsPath, updated, new UTF8Encoding(false), keepPrevious: true, cancellationToken: cancellationToken);
    }

    public string DiscoverActiveWorldGuid()
    {
        if (!Directory.Exists(context.ServerPaths.ActivePath)) throw new DirectoryNotFoundException($"找不到 active 存档目录：{context.ServerPaths.ActivePath}");
        var worlds = Directory.EnumerateDirectories(context.ServerPaths.ActivePath).Select(Path.GetFileName)
            .Where(name => name is not null && WorldGuidRegex().IsMatch(name)).Cast<string>().ToArray();
        if (worlds.Length != 1) throw new InvalidOperationException($"新世界生成后应只有一个世界 UID 目录，实际为 {worlds.Length} 个。");
        return ValidateWorldGuid(worlds[0]);
    }

    public async Task WaitForActiveWorldSaveAsync(string expectedWorldGuid, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        var expected = ValidateWorldGuid(expectedWorldGuid);
        var deadline = DateTimeOffset.UtcNow.Add(timeout);
        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Directory.Exists(context.ServerPaths.ActivePath))
            {
                var worlds = Directory.EnumerateDirectories(context.ServerPaths.ActivePath).Select(Path.GetFileName)
                    .Where(name => name is not null && WorldGuidRegex().IsMatch(name)).Cast<string>().ToArray();
                if (worlds.Length > 1 || worlds.Length == 1 && !string.Equals(worlds[0], expected, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("新世界目录与 REST UID 不一致，已停止注册以防切错存档。");
                var world = Path.Combine(context.ServerPaths.ActivePath, expected);
                if (File.Exists(Path.Combine(world, "Level.sav")) &&
                    File.Exists(Path.Combine(world, "LevelMeta.sav"))) return;
            }
            await Task.Delay(500, cancellationToken);
        }
        throw new TimeoutException($"REST 已就绪，但 {timeout.TotalSeconds:0} 秒内未看到新世界的 Level.sav 和 LevelMeta.sav；已停止注册。");
    }

    public void ValidateSlot(SaveSlotRegistry registry, int slotId)
    {
        var slot = GetSlot(registry, slotId);
        var path = PathSafety.RequireInside(context.ServerPaths.SaveRoot, GetSlotPath(registry, slotId));
        if (!Directory.Exists(path)) throw new DirectoryNotFoundException($"找不到存档 {slotId}：{path}");
        var level = Path.Combine(path, slot.WorldGuid, "Level.sav");
        if (!File.Exists(level)) throw new FileNotFoundException($"存档 {slotId} 缺少预期的 Level.sav。", level);
    }

    public static string ValidateTag(string tag)
    {
        var value = tag.Trim();
        if (value.Length is < 1 or > 40) throw new InvalidOperationException("存档标签必须为 1 到 40 个字符。 ");
        return value;
    }

    public static string NewParkedFolderName(int slotId, string tag)
    {
        var label = tag.Trim();
        foreach (var invalid in Path.GetInvalidFileNameChars()) label = label.Replace(invalid, '_');
        label = WhitespaceRegex().Replace(label, " ").Trim(' ', '.');
        if (string.IsNullOrWhiteSpace(label)) label = "未命名";
        if (label.Length > 32) label = label[..32].TrimEnd(' ', '.');
        return PathSafety.RequireChildName($"0 - Slot {slotId:D3} - {label}");
    }

    private static void ValidateRegistry(SaveSlotRegistry registry)
    {
        if (registry.Slots.Count == 0) throw new InvalidOperationException("没有登记任何存档。 ");
        if (registry.Slots.Select(slot => slot.Id).Distinct().Count() != registry.Slots.Count) throw new InvalidOperationException("存在重复存档编号。 ");
        if (!registry.Slots.Any(slot => slot.Id == registry.ActiveSlotId)) throw new InvalidOperationException("当前存档编号未登记。 ");
        var duplicate = registry.Slots.GroupBy(slot => ValidateWorldGuid(slot.WorldGuid), StringComparer.OrdinalIgnoreCase).FirstOrDefault(group => group.Count() > 1);
        if (duplicate is not null)
            throw new InvalidOperationException($"重复世界 UID {duplicate.Key}：{string.Join("；", duplicate.Select(slot => $"{slot.Id} · {slot.Tag}"))}。两个世界会共用客户端 LocalData，可能混合地图和本地世界状态。 ");
        if (registry.Slots.Select(slot => slot.ParkedFolder).Distinct(StringComparer.OrdinalIgnoreCase).Count() != registry.Slots.Count)
            throw new InvalidOperationException("多个存档登记了同一个停放目录。 ");
        foreach (var slot in registry.Slots)
        {
            slot.WorldGuid = ValidateWorldGuid(slot.WorldGuid);
            PathSafety.RequireChildName(slot.ParkedFolder);
        }
    }

    private static string ValidateWorldGuid(string value)
    {
        var normalized = value.Trim().ToUpperInvariant();
        if (!WorldGuidRegex().IsMatch(normalized)) throw new InvalidOperationException($"世界 UID 无效：{value}");
        return normalized;
    }

    [GeneratedRegex("^[0-9A-Fa-f]{32}$", RegexOptions.CultureInvariant)] private static partial Regex WorldGuidRegex();
    [GeneratedRegex("(?m)^DedicatedServerName=([^\\r\\n]+)", RegexOptions.CultureInvariant)] private static partial Regex DedicatedServerNameRegex();
    [GeneratedRegex("\\s+", RegexOptions.CultureInvariant)] private static partial Regex WhitespaceRegex();
}

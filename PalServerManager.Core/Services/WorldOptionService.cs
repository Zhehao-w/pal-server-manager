using HaoHaoTianTian.PalHR.Models;

namespace HaoHaoTianTian.PalHR.Services;

public sealed class WorldOptionService(SaveSlotService slots, LoggingService log)
{
    private readonly HashSet<string> _acknowledgedStates = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<WorldOptionConflict> Detect(SaveSlotRegistry registry, int slotId)
    {
        var slot = slots.GetSlot(registry, slotId);
        var world = Path.Combine(slots.GetSlotPath(registry, slotId), slot.WorldGuid);
        var result = new List<WorldOptionConflict>();
        foreach (var name in new[] { "WorldOption.sav", "WorldOptions.sav" })
        {
            var path = PathSafety.RequireInside(world, Path.Combine(world, name));
            if (!File.Exists(path)) continue;
            var info = new FileInfo(path);
            var conflict = new WorldOptionConflict(slot.WorldGuid, path, info.Length, info.LastWriteTimeUtc);
            if (!_acknowledgedStates.Contains(GetStateKey(conflict))) result.Add(conflict);
        }
        return result;
    }

    public void Acknowledge(WorldOptionConflict conflict) => _acknowledgedStates.Add(GetStateKey(conflict));

    public async Task<string> BackupAndDisableAsync(WorldOptionConflict conflict, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(conflict.FilePath)) throw new FileNotFoundException("WorldOption 文件已经不存在。", conflict.FilePath);
        var disabled = conflict.FilePath + $".disabled-{DateTime.Now:yyyyMMdd-HHmmss}";
        File.Move(conflict.FilePath, disabled);
        await log.WriteAsync($"World-local options file was backed up and disabled: {conflict.FilePath} -> {disabled}", cancellationToken);
        return disabled;
    }

    private static string GetStateKey(WorldOptionConflict value) => $"{value.FilePath}|{value.Length}|{value.LastWriteTimeUtc.Ticks}";
}

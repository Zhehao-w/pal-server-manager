using System.Text.Json;
using HaoHaoTianTian.PalHR.Models;

namespace HaoHaoTianTian.PalHR.Services;

public sealed class SettingsService(PalContext context, SafeFileService files)
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public async Task<ManagerSettings> LoadAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(context.StatePaths.ManagerSettingsPath)) return new ManagerSettings();
        try
        {
            await using var stream = File.OpenRead(context.StatePaths.ManagerSettingsPath);
            var settings = await JsonSerializer.DeserializeAsync<ManagerSettings>(stream, JsonOptions, cancellationToken)
                ?? throw new InvalidOperationException("ManagerSettings.json 为空。");
            settings.IdleShutdownMinutes = Math.Clamp(settings.IdleShutdownMinutes, 1, 1440);
            return settings;
        }
        catch (JsonException exception) { throw new InvalidOperationException($"ManagerSettings.json 已损坏：{exception.Message}", exception); }
    }

    public async Task SaveAsync(ManagerSettings settings, CancellationToken cancellationToken = default)
    {
        settings.Version = 2;
        settings.UpdatedAtUtc = DateTimeOffset.UtcNow.ToString("O");
        await files.WriteJsonAsync(context.StatePaths.ManagerSettingsPath, settings, JsonOptions, keepPrevious: true, cancellationToken: cancellationToken);
    }
}

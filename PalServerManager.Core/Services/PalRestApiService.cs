using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using HaoHaoTianTian.PalHR.Models;

namespace HaoHaoTianTian.PalHR.Services;

public sealed partial class PalRestApiService(PalContext context, HttpClient httpClient)
{
    private DateTime _configurationWriteTimeUtc;
    private RestConfiguration? _configuration;

    public async Task<string> GetConfiguredServerNameAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(context.ServerPaths.SettingsPath)) return "ServerName 未知";
        var settings = await File.ReadAllTextAsync(context.ServerPaths.SettingsPath, cancellationToken);
        var match = ServerNameRegex().Match(settings);
        return match.Success && !string.IsNullOrWhiteSpace(match.Groups[1].Value) ? match.Groups[1].Value : "ServerName 未知";
    }

    public async Task<ServerInfo> GetInfoAsync(CancellationToken cancellationToken = default)
    {
        using var document = await SendAsync(HttpMethod.Get, "info", null, TimeSpan.FromSeconds(4), cancellationToken);
        var root = document.RootElement;
        return new ServerInfo(
            ReadString(root, "servername", "serverName") ?? "未知服务器",
            ReadString(root, "version") ?? "-",
            (ReadString(root, "worldguid", "worldGuid") ?? "").ToUpperInvariant());
    }

    public async Task<ServerMetrics> GetMetricsAsync(CancellationToken cancellationToken = default)
    {
        using var document = await SendAsync(HttpMethod.Get, "metrics", null, TimeSpan.FromSeconds(4), cancellationToken);
        var root = document.RootElement;
        return new ServerMetrics(
            ReadDouble(root, "serverfps", "serverFps"),
            ReadDouble(root, "serverframetime", "serverFrameTime"),
            ReadInt(root, "currentplayernum", "currentPlayerNum"),
            ReadInt(root, "maxplayernum", "maxPlayerNum"),
            ReadDouble(root, "uptime"));
    }

    public async Task<IReadOnlyList<RestPlayer>> GetPlayersAsync(CancellationToken cancellationToken = default)
    {
        using var document = await SendAsync(HttpMethod.Get, "players", null, TimeSpan.FromSeconds(4), cancellationToken);
        if (!TryGet(document.RootElement, out var players, "players") || players.ValueKind != JsonValueKind.Array) return [];
        var result = new List<RestPlayer>();
        foreach (var player in players.EnumerateArray())
        {
            result.Add(new RestPlayer(
                ReadString(player, "name") ?? "-",
                ReadInt(player, "level") ?? 0,
                ReadDouble(player, "ping"),
                ReadString(player, "playerId", "playerid") ?? "-",
                ReadString(player, "userId", "userid") ?? "-",
                ReadString(player, "accountName", "account") ?? "-"));
        }
        return result;
    }

    public async Task SaveAsync(CancellationToken cancellationToken = default)
    {
        using var _ = await SendAsync(HttpMethod.Post, "save", null, TimeSpan.FromSeconds(30), cancellationToken, allowEmptyResponse: true);
    }

    public async Task ShutdownAsync(int waitSeconds, string message, CancellationToken cancellationToken = default)
    {
        using var _ = await SendAsync(HttpMethod.Post, "shutdown", new { waittime = waitSeconds, message }, TimeSpan.FromSeconds(30), cancellationToken, allowEmptyResponse: true);
    }

    public async Task AnnounceAsync(string message, CancellationToken cancellationToken = default)
    {
        using var _ = await SendAsync(HttpMethod.Post, "announce", new { message }, TimeSpan.FromSeconds(5), cancellationToken, allowEmptyResponse: true);
    }

    private async Task<JsonDocument> SendAsync(HttpMethod method, string endpoint, object? body, TimeSpan timeout, CancellationToken cancellationToken, bool allowEmptyResponse = false)
    {
        var configuration = await GetConfigurationAsync(cancellationToken);
        using var request = new HttpRequestMessage(method, $"http://127.0.0.1:{configuration.Port}/v1/api/{endpoint.TrimStart('/')}");
        var token = Convert.ToBase64String(Encoding.UTF8.GetBytes($"admin:{configuration.Password}"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic", token);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        if (body is not null)
        {
            request.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
        }

        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);
        using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeoutSource.Token);
        response.EnsureSuccessStatusCode();
        var payload = await response.Content.ReadAsByteArrayAsync(timeoutSource.Token);
        var isEmpty = payload.Length == 0 || payload.All(static value => value is 0x20 or 0x09 or 0x0D or 0x0A);
        if (isEmpty)
        {
            if (allowEmptyResponse) return JsonDocument.Parse("{}");
            throw new InvalidDataException($"REST {endpoint} 返回了空响应。");
        }
        return JsonDocument.Parse(payload);
    }

    private async Task<RestConfiguration> GetConfigurationAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(context.ServerPaths.SettingsPath)) throw new FileNotFoundException("找不到 PalWorldSettings.ini。", context.ServerPaths.SettingsPath);
        var writeTime = File.GetLastWriteTimeUtc(context.ServerPaths.SettingsPath);
        if (_configuration is not null && writeTime == _configurationWriteTimeUtc) return _configuration;

        var settings = await File.ReadAllTextAsync(context.ServerPaths.SettingsPath, cancellationToken);
        if (!RestEnabledRegex().IsMatch(settings)) throw new InvalidOperationException("REST API 未启用。请设置 RESTAPIEnabled=True 并重启服务器。 ");
        var passwordMatch = AdminPasswordRegex().Match(settings);
        if (!passwordMatch.Success || string.IsNullOrEmpty(passwordMatch.Groups[1].Value)) throw new InvalidOperationException("PalWorldSettings.ini 中缺少 AdminPassword。 ");
        var portMatch = RestPortRegex().Match(settings);
        _configuration = new RestConfiguration(passwordMatch.Groups[1].Value, portMatch.Success ? int.Parse(portMatch.Groups[1].Value, CultureInfo.InvariantCulture) : 8212);
        _configurationWriteTimeUtc = writeTime;
        return _configuration;
    }

    private static string? ReadString(JsonElement element, params string[] names)
    {
        if (!TryGet(element, out var value, names)) return null;
        return value.ValueKind == JsonValueKind.String ? value.GetString() : value.ToString();
    }

    private static double? ReadDouble(JsonElement element, params string[] names)
    {
        if (!TryGet(element, out var value, names)) return null;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number)) return number;
        return double.TryParse(value.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out number) ? number : null;
    }

    private static int? ReadInt(JsonElement element, params string[] names)
    {
        var number = ReadDouble(element, names);
        return number is null ? null : Convert.ToInt32(number.Value, CultureInfo.InvariantCulture);
    }

    private static bool TryGet(JsonElement element, out JsonElement value, params string[] names)
    {
        foreach (var property in element.EnumerateObject())
        {
            if (names.Any(name => string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase)))
            {
                value = property.Value;
                return true;
            }
        }
        value = default;
        return false;
    }

    [GeneratedRegex("AdminPassword=\"([^\"]+)\"", RegexOptions.CultureInvariant)]
    private static partial Regex AdminPasswordRegex();

    [GeneratedRegex("RESTAPIEnabled=True", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex RestEnabledRegex();

    [GeneratedRegex("RESTAPIPort=(\\d+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex RestPortRegex();

    [GeneratedRegex("(?:^|,)ServerName=\"([^\"]*)\"", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ServerNameRegex();
}

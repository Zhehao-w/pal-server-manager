using System.Text;

namespace HaoHaoTianTian.PalHR.Services;

public sealed class PalWorldIniDocument
{
    private readonly string _prefix;
    private readonly string _suffix;
    private readonly List<Entry> _entries;

    private PalWorldIniDocument(string prefix, string suffix, List<Entry> entries)
    {
        _prefix = prefix;
        _suffix = suffix;
        _entries = entries;
    }

    public static PalWorldIniDocument Parse(string content)
    {
        const string marker = "OptionSettings=(";
        var start = content.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (start < 0) throw new InvalidOperationException("PalWorldSettings.ini 中缺少 OptionSettings。 ");
        var bodyStart = start + marker.Length;
        var depth = 1;
        var quoted = false;
        var escaped = false;
        var end = -1;
        for (var index = bodyStart; index < content.Length; index++)
        {
            var character = content[index];
            if (escaped) { escaped = false; continue; }
            if (character == '\\' && quoted) { escaped = true; continue; }
            if (character == '"') { quoted = !quoted; continue; }
            if (quoted) continue;
            if (character == '(') depth++;
            else if (character == ')' && --depth == 0) { end = index; break; }
        }
        if (end < 0) throw new InvalidOperationException("PalWorldSettings.ini 的 OptionSettings 括号不完整。 ");
        return new PalWorldIniDocument(
            content[..bodyStart],
            content[end..],
            SplitEntries(content[bodyStart..end]));
    }

    public static async Task<PalWorldIniDocument> LoadAsync(string path, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(path)) throw new FileNotFoundException("找不到 Palworld 配置文件。", path);
        return Parse(await File.ReadAllTextAsync(path, cancellationToken));
    }

    public bool TryGetValue(string key, out string value)
    {
        var entry = _entries.FirstOrDefault(item => string.Equals(item.Key, key, StringComparison.OrdinalIgnoreCase));
        value = entry?.Value ?? "";
        return entry is not null;
    }

    public string GetRequiredValue(string key) =>
        TryGetValue(key, out var value) ? value : throw new InvalidOperationException($"配置中缺少设置 {key}。 ");

    public void SetValue(string key, string value)
    {
        if (string.IsNullOrWhiteSpace(key) || key.Contains('=')) throw new ArgumentException("配置键无效。", nameof(key));
        WorldSettingsValueValidator.ValidateManagedValue(key, value);
        var entry = _entries.FirstOrDefault(item => string.Equals(item.Key, key, StringComparison.OrdinalIgnoreCase));
        if (entry is null) _entries.Add(new Entry(key, value));
        else entry.Value = value;
    }

    public string Render() => _prefix + string.Join(',', _entries.Select(entry => $"{entry.Key}={entry.Value}")) + _suffix;

    public async Task WriteAtomicAsync(string path, CancellationToken cancellationToken = default)
    {
        var parent = Path.GetDirectoryName(path) ?? throw new InvalidOperationException("配置文件路径无效。 ");
        Directory.CreateDirectory(parent);
        var temporary = Path.Combine(parent, $".{Path.GetFileName(path)}.tmp-{Environment.ProcessId}-{Guid.NewGuid():N}");
        try
        {
            var bytes = new UTF8Encoding(false).GetBytes(Render());
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096,
                             FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await stream.WriteAsync(bytes, cancellationToken);
                await stream.FlushAsync(cancellationToken);
                stream.Flush(true);
            }
            File.Move(temporary, path, true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    public static string Quote(string value) => $"\"{value.Replace("\\", "\\\\").Replace("\"", "\\\"")}\"";

    public static string Unquote(string value)
    {
        var trimmed = value.Trim();
        return trimmed.Length >= 2 && trimmed[0] == '"' && trimmed[^1] == '"'
            ? trimmed[1..^1].Replace("\\\"", "\"").Replace("\\\\", "\\")
            : trimmed;
    }

    private static List<Entry> SplitEntries(string body)
    {
        var result = new List<Entry>();
        var start = 0;
        var depth = 0;
        var quoted = false;
        var escaped = false;
        for (var index = 0; index <= body.Length; index++)
        {
            var atEnd = index == body.Length;
            var character = atEnd ? ',' : body[index];
            if (!atEnd)
            {
                if (escaped) { escaped = false; continue; }
                if (character == '\\' && quoted) { escaped = true; continue; }
                if (character == '"') { quoted = !quoted; continue; }
                if (!quoted)
                {
                    if (character == '(') depth++;
                    else if (character == ')') depth--;
                }
            }
            if (character != ',' || quoted || depth != 0) continue;
            var raw = body[start..index].Trim();
            start = index + 1;
            if (raw.Length == 0) continue;
            var equals = raw.IndexOf('=');
            if (equals <= 0) throw new InvalidOperationException($"无法解析 Palworld 设置项：{raw}");
            result.Add(new Entry(raw[..equals].Trim(), raw[(equals + 1)..].Trim()));
        }
        return result;
    }

    private sealed class Entry(string key, string value)
    {
        public string Key { get; } = key;
        public string Value { get; set; } = value;
    }
}

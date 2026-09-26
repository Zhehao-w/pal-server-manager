using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;

namespace HaoHaoTianTian.PalHR.Services;

public sealed class SafeFileService
{
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Gates = new(StringComparer.OrdinalIgnoreCase);
    public static readonly JsonSerializerOptions DefaultJsonOptions = new() { WriteIndented = true };

    public async Task WriteJsonAsync<T>(string path, T value, JsonSerializerOptions? options = null, bool keepPrevious = false, CancellationToken cancellationToken = default)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value, options ?? DefaultJsonOptions);
        await WriteBytesAsync(path, bytes, keepPrevious, cancellationToken);
    }

    public Task WriteTextAsync(string path, string value, Encoding? encoding = null, bool keepPrevious = false, CancellationToken cancellationToken = default) =>
        WriteBytesAsync(path, (encoding ?? new UTF8Encoding(false)).GetBytes(value), keepPrevious, cancellationToken);

    public async Task WriteBytesAsync(string path, ReadOnlyMemory<byte> value, bool keepPrevious = false, CancellationToken cancellationToken = default)
    {
        var fullPath = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(fullPath) ?? throw new InvalidOperationException($"无法确定文件目录：{fullPath}");
        Directory.CreateDirectory(directory);
        var gate = Gates.GetOrAdd(fullPath, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        string? temporary = null;
        try
        {
            CleanupStaleTemps(fullPath);
            temporary = Path.Combine(directory, $".{Path.GetFileName(fullPath)}.tmp-{Environment.ProcessId}-{Guid.NewGuid():N}");
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536,
                             FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await stream.WriteAsync(value, cancellationToken);
                await stream.FlushAsync(cancellationToken);
                stream.Flush(true);
            }

            if (File.Exists(fullPath))
            {
                var previous = keepPrevious ? fullPath + ".previous" : null;
                File.Replace(temporary, fullPath, previous, true);
            }
            else
            {
                File.Move(temporary, fullPath);
            }
            temporary = null;
        }
        finally
        {
            if (temporary is not null && File.Exists(temporary)) File.Delete(temporary);
            gate.Release();
        }
    }

    public static void CleanupStaleTemps(string destination)
    {
        var fullPath = Path.GetFullPath(destination);
        var directory = Path.GetDirectoryName(fullPath);
        if (directory is null || !Directory.Exists(directory)) return;
        var prefix = $".{Path.GetFileName(fullPath)}.tmp-";
        foreach (var candidate in Directory.EnumerateFiles(directory, prefix + "*", SearchOption.TopDirectoryOnly))
        {
            try { File.Delete(candidate); } catch { }
        }
        foreach (var candidate in Directory.EnumerateFiles(directory, Path.GetFileName(fullPath) + ".tmp-*", SearchOption.TopDirectoryOnly))
        {
            try { File.Delete(candidate); } catch { }
        }
    }
}

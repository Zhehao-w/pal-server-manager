namespace HaoHaoTianTian.PalHR.Services;

internal static class PathSafety
{
    public static string RequireInside(string root, string path, bool allowRoot = false)
    {
        var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var fullPath = Path.GetFullPath(path);
        if ((!allowRoot || !string.Equals(fullPath.TrimEnd(Path.DirectorySeparatorChar), fullRoot.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase)) &&
            !fullPath.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"拒绝不安全的路径：{fullPath}");
        }
        return fullPath;
    }

    public static string RequireChildName(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name == "0" || name != Path.GetFileName(name) || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            throw new InvalidOperationException($"不安全的停放存档文件夹名称：{name}");
        return name;
    }
}

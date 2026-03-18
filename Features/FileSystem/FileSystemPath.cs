namespace C2.Features.FileSystem;

/// <summary>Static path helper for FileSystem navigation.</summary>
public static class FileSystemPath
{
    /// <summary>Normalize a path for sending to the agent: trim trailing separators and append the correct one.</summary>
    public static string Normalize(string path, bool isUnix)
    {
        var trimmed = path.TrimEnd('\\', '/');
        if (isUnix && string.IsNullOrEmpty(trimmed)) return "/";
        if (!string.IsNullOrEmpty(trimmed) && trimmed != "/")
            trimmed += isUnix ? "/" : @"\";
        return trimmed;
    }

    /// <summary>Split a path into non-empty segments.</summary>
    public static string[] GetSegments(string path)
        => path.Split(new[] { '\\', '/' }, StringSplitOptions.RemoveEmptyEntries);

    /// <summary>Get the parent path from segments.</summary>
    public static string GetParent(string[] segments, string pathSep, bool isUnix, string rootPath)
    {
        if (segments.Length <= 1) return rootPath;
        var parent = string.Join(pathSep, segments.Take(segments.Length - 1));
        return isUnix ? "/" + parent : parent;
    }

    /// <summary>Build a breadcrumb path from segments up to (and including) the given index.</summary>
    public static string BuildBreadcrumbPath(string[] segments, int toIndex, string pathSep, bool isUnix)
    {
        var joined = string.Join(pathSep, segments.Take(toIndex + 1));
        return isUnix ? "/" + joined : joined;
    }

    /// <summary>Trim trailing path separators.</summary>
    public static string TrimTrailing(string path)
        => path.TrimEnd('\\', '/');
}

namespace WhipRadio.Core.Helpers;

/// <summary>
/// Resolves a stored media path against the data root. Generated and uploaded
/// media store data-root-relative paths; external-library tracks store the
/// absolute path of a file WhipRadio does not own (and must never delete).
/// </summary>
public static class MediaPaths
{
    public static string ResolveAbsolute(string dataRoot, string filePath)
        => Path.IsPathRooted(filePath) ? filePath : Path.Combine(dataRoot, filePath);

    /// <summary>True when the resolved file lives under the data root — i.e. WhipRadio owns it.</summary>
    public static bool IsUnderDataRoot(string dataRoot, string filePath)
    {
        var absolute = Path.GetFullPath(ResolveAbsolute(dataRoot, filePath));
        var root = Path.GetFullPath(dataRoot)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return absolute.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            || absolute.StartsWith(root + Path.AltDirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }
}

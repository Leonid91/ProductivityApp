namespace ProductivityApp.Host;

/// <summary>
/// The published app is a single file that unpacks itself (about 120 MB) into %TEMP%\.net\ProductivityApp\&lt;build&gt;\ the
/// first time each build runs, and .NET never removes the copies of earlier builds: every update would leave one more
/// on drive C:. At start, the copies of other builds that no program is using are deleted.
/// </summary>
internal static class UnpackedCopies
{
    public static void RemoveOld()
    {
        var current = Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory);
        var parent = Path.GetDirectoryName(current);
        // Only when this run is itself an unpacked copy (not a development build).
        if (parent is null || !string.Equals(Path.GetFileName(parent), "ProductivityApp", StringComparison.OrdinalIgnoreCase)
            || !string.Equals(Path.GetFileName(Path.GetDirectoryName(parent)), ".net", StringComparison.OrdinalIgnoreCase)) return;
        foreach (var dir in Directory.EnumerateDirectories(parent))
        {
            if (string.Equals(dir, current, StringComparison.OrdinalIgnoreCase)) continue;
            try
            {
                // A copy that is running cannot be renamed (its files are in use): it is left alone.
                var doomed = dir + ".old";
                Directory.Move(dir, doomed);
                Directory.Delete(doomed, true);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}

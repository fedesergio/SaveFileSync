namespace SaveFileSync.Core.IO;

/// <summary>
/// Write-to-temp + flush + rename. Temp names contain ".tmp-" so that readers
/// (including other PCs that receive the file half-way through OneDrive) ignore them.
/// </summary>
public static class AtomicFile
{
    public const string TempMarker = ".tmp-";

    public static bool IsTempName(string fileName) => fileName.Contains(TempMarker, StringComparison.OrdinalIgnoreCase);

    public static string TempPathFor(string finalPath, string tag) =>
        $"{finalPath}{TempMarker}{tag}-{Guid.NewGuid().ToString("N")[..8]}";

    public static void WriteAllBytes(string path, ReadOnlySpan<byte> bytes, string tag, bool overwrite = true)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tmp = TempPathFor(path, tag);
        try
        {
            using (var fs = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                fs.Write(bytes);
                fs.Flush(flushToDisk: true);
            }

            File.Move(tmp, path, overwrite);
        }
        catch
        {
            TryDelete(tmp);
            throw;
        }
    }

    public static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}

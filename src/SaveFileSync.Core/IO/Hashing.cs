using System.Security.Cryptography;

namespace SaveFileSync.Core.IO;

public static class Hashing
{
    private const int BufferSize = 81920;

    /// <summary>Copies <paramref name="source"/> to <paramref name="target"/> and returns the SHA-256 (lowercase hex).</summary>
    public static string CopyAndHash(Stream source, Stream target)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[BufferSize];
        int read;
        while ((read = source.Read(buffer, 0, buffer.Length)) > 0)
        {
            hash.AppendData(buffer, 0, read);
            target.Write(buffer, 0, read);
        }

        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    /// <summary>
    /// Hashes a file without locking it: the game may be writing it, and an exclusive
    /// open could make the game's own save fail (PLAN.md §6.3).
    /// </summary>
    public static string HashFile(string path)
    {
        using var stream = OpenShared(path);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }

    /// <summary>Opens a game file for reading with full sharing (read, write and delete).</summary>
    public static FileStream OpenShared(string path) =>
        new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, BufferSize, FileOptions.SequentialScan);
}

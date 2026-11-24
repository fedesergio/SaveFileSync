using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SaveFileSync.Core.Config;
using SaveFileSync.Core.IO;

namespace SaveFileSync.Windows;

/// <summary>
/// API keys in <c>secrets.dat</c>, encrypted with DPAPI for the current Windows account (PLAN.md §5, §11).
/// Another user, or the same file copied to another PC, cannot read it; the keys are entered on each PC.
/// </summary>
public sealed class DpapiSecretStore : ISecretStore
{
    // Not a secret: it only makes sure other programs' DPAPI blobs are not accepted as ours.
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("SaveFileSync/secrets/v1");

    private readonly string _path;
    private readonly Lock _lock = new();
    private Dictionary<string, string> _values;

    public DpapiSecretStore(string path)
    {
        _path = path;
        _values = Load();
    }

    public string? Get(string name)
    {
        lock (_lock)
        {
            return _values.GetValueOrDefault(name);
        }
    }

    public void Set(string name, string? value)
    {
        lock (_lock)
        {
            var copy = new Dictionary<string, string>(_values, StringComparer.Ordinal);
            if (string.IsNullOrEmpty(value))
            {
                copy.Remove(name);
            }
            else
            {
                copy[name] = value;
            }

            if (copy.Count == 0)
            {
                File.Delete(_path);
            }
            else
            {
                var plain = JsonSerializer.SerializeToUtf8Bytes(copy);
                AtomicFile.WriteAllBytes(_path, ProtectedData.Protect(plain, Entropy, DataProtectionScope.CurrentUser), "sec");
                CryptographicOperations.ZeroMemory(plain);
            }

            _values = copy;
        }
    }

    private Dictionary<string, string> Load()
    {
        if (!File.Exists(_path))
        {
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }

        try
        {
            var plain = ProtectedData.Unprotect(File.ReadAllBytes(_path), Entropy, DataProtectionScope.CurrentUser);
            try
            {
                return JsonSerializer.Deserialize<Dictionary<string, string>>(plain) ?? [];
            }
            finally
            {
                CryptographicOperations.ZeroMemory(plain);
            }
        }
        catch (Exception ex) when (ex is CryptographicException or JsonException or IOException)
        {
            // Another Windows account or a damaged file: the keys simply have to be entered again.
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }
    }
}

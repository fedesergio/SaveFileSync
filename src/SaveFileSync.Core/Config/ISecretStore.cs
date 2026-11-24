namespace SaveFileSync.Core.Config;

/// <summary>
/// Device-local storage for API keys (PLAN.md §5). On Windows it is encrypted with DPAPI;
/// keys never reach git, the shared repository, logs or diagnostics.
/// </summary>
public interface ISecretStore
{
    string? Get(string name);

    /// <summary>Stores the value; null or empty removes it.</summary>
    void Set(string name, string? value);
}

/// <summary>Names of the secrets the app knows.</summary>
public static class SecretNames
{
    public const string SteamGridDbKey = "steamgriddb.apikey";
    public const string IgdbClientId = "igdb.clientid";
    public const string IgdbClientSecret = "igdb.clientsecret";
}

/// <summary>For tests and for runs where nothing can be stored.</summary>
public sealed class MemorySecretStore : ISecretStore
{
    private readonly Dictionary<string, string> _values = new(StringComparer.Ordinal);
    private readonly Lock _lock = new();

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
            if (string.IsNullOrEmpty(value))
            {
                _values.Remove(name);
            }
            else
            {
                _values[name] = value;
            }
        }
    }
}

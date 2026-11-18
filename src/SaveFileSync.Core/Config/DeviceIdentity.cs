using SaveFileSync.Core.IO;
using SaveFileSync.Core.Model;

namespace SaveFileSync.Core.Config;

/// <summary>
/// This PC's identity (<c>device.json</c>): a random id generated on first run and a
/// fingerprint of the machine used to detect a cloned identity (PLAN.md §5, §9).
/// </summary>
public sealed class DeviceIdentity
{
    public required string DeviceId { get; init; }

    public required string ShortId { get; init; }

    public string? Fingerprint { get; init; }

    public static DeviceIdentity LoadOrCreate(string path, string? fingerprint, Func<string, bool>? shortIdTaken = null)
    {
        if (File.Exists(path))
        {
            var existing = Json.Deserialize<DeviceIdentity>(File.ReadAllBytes(path));
            if (existing is not null)
            {
                return existing;
            }
        }

        var created = Create(fingerprint, shortIdTaken);
        AtomicFile.WriteAllBytes(path, Json.Serialize(created), "dev");
        return created;
    }

    public static DeviceIdentity Create(string? fingerprint, Func<string, bool>? shortIdTaken = null)
    {
        while (true)
        {
            var id = Guid.NewGuid().ToString("N");
            var shortId = id[..8];
            if (shortIdTaken?.Invoke(shortId) == true)
            {
                continue;
            }

            return new DeviceIdentity { DeviceId = id, ShortId = shortId, Fingerprint = fingerprint };
        }
    }
}

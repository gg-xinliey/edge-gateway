using System.Collections.Concurrent;
using System.Security.Cryptography;

namespace EdgeGateway.Services;

// One writer at a time per config file, whoever writes it: the auto-mapper (AddPointMapEntry), a save
// on the config page, a tab getting another mapping (Reassign). Version: a hash of the file as it is,
// sent to the page with it (ETag); a save made from an older copy (If-Match) is refused, so it can't
// wipe out tags mapped automatically since the page loaded the file.
public static class ConfigFileLock
{
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Locks = new(StringComparer.OrdinalIgnoreCase);

    public static SemaphoreSlim For(string path) => Locks.GetOrAdd(Path.GetFullPath(path), _ => new SemaphoreSlim(1, 1));

    // An HTTP ETag: "<16 hex>", or "none" for a file that doesn't exist.
    public static string Version(byte[]? content) =>
        content is null ? "\"none\"" : $"\"{Convert.ToHexString(SHA256.HashData(content))[..16]}\"";

    public static string Version(string path) => Version(File.Exists(path) ? File.ReadAllBytes(path) : null);
}

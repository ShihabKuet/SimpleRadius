using System.Net;
using System.Text.Json;
using SimpleRadius.Core.Models;

namespace SimpleRadius.Core.Storage;

/// <summary>
/// Thread-safe store for NAS (Network Access Server) client entries.
/// Supports single-IP and CIDR subnet matching.
/// </summary>
public sealed class NasStore
{
    private readonly string              _filePath;
    private readonly ReaderWriterLockSlim _lock = new();
    private List<NasClient>              _clients = new();

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented               = true,
        PropertyNameCaseInsensitive = true,
    };

    public NasStore(string filePath)
    {
        _filePath = filePath;
        Load();
    }

    // ── Queries ───────────────────────────────────────────────────────────────
    public IReadOnlyList<NasClient> GetAll()
    {
        _lock.EnterReadLock();
        try { return _clients.AsReadOnly(); }
        finally { _lock.ExitReadLock(); }
    }

    /// <summary>
    /// Finds the first NAS entry whose IP/CIDR matches the given remote endpoint.
    /// Returns null if no match (request should be silently dropped).
    /// </summary>
    public NasClient? FindByIp(IPAddress remoteIp)
    {
        _lock.EnterReadLock();
        try
        {
            foreach (var nas in _clients)
            {
                if (IpMatches(remoteIp, nas.IpAddress))
                    return nas;
            }
            return null;
        }
        finally { _lock.ExitReadLock(); }
    }

    // ── Mutations ─────────────────────────────────────────────────────────────
    public bool Add(NasClient client)
    {
        _lock.EnterWriteLock();
        try
        {
            if (_clients.Any(c => c.Name.Equals(client.Name, StringComparison.OrdinalIgnoreCase)))
                return false;
            _clients.Add(client);
            Save();
            return true;
        }
        finally { _lock.ExitWriteLock(); }
    }

    public bool Remove(string name)
    {
        _lock.EnterWriteLock();
        try
        {
            int n = _clients.RemoveAll(c => c.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (n > 0) Save();
            return n > 0;
        }
        finally { _lock.ExitWriteLock(); }
    }

    public bool Update(NasClient updated)
    {
        _lock.EnterWriteLock();
        try
        {
            var idx = _clients.FindIndex(c =>
                c.Name.Equals(updated.Name, StringComparison.OrdinalIgnoreCase));
            if (idx < 0) return false;
            _clients[idx] = updated;
            Save();
            return true;
        }
        finally { _lock.ExitWriteLock(); }
    }

    // ── IP matching ───────────────────────────────────────────────────────────
    private static bool IpMatches(IPAddress address, string nasIpOrCidr)
    {
        if (nasIpOrCidr == "0.0.0.0" || nasIpOrCidr == "*")
            return true; // wildcard — match all (useful for dev/testing)

        if (nasIpOrCidr.Contains('/'))
        {
            // CIDR matching
            var parts = nasIpOrCidr.Split('/');
            if (parts.Length != 2) return false;
            if (!IPAddress.TryParse(parts[0], out var network)) return false;
            if (!int.TryParse(parts[1], out int prefixLen)) return false;

            var addrBytes    = address.GetAddressBytes();
            var networkBytes = network.GetAddressBytes();
            if (addrBytes.Length != networkBytes.Length) return false;

            int fullBytes = prefixLen / 8;
            int remainder = prefixLen % 8;

            for (int i = 0; i < fullBytes; i++)
                if (addrBytes[i] != networkBytes[i]) return false;

            if (remainder > 0)
            {
                byte mask = (byte)(0xFF << (8 - remainder));
                if ((addrBytes[fullBytes] & mask) != (networkBytes[fullBytes] & mask))
                    return false;
            }
            return true;
        }

        // Single IP
        return IPAddress.TryParse(nasIpOrCidr, out var single) && single.Equals(address);
    }

    // ── Persistence ───────────────────────────────────────────────────────────
    private void Load()
    {
        if (!File.Exists(_filePath))
        {
            // Seed a localhost NAS so the server works out-of-the-box for testing
            _clients = new List<NasClient>
            {
                new()
                {
                    Name         = "localhost-test",
                    IpAddress    = "127.0.0.1",
                    SharedSecret = "testing123",
                    Vendor       = "Generic",
                    Description  = "Loopback NAS for testing with radtest",
                },
                new()
                {
                    Name         = "all-private",
                    IpAddress    = "0.0.0.0",
                    SharedSecret = "testing123",
                    Vendor       = "Generic",
                    Description  = "Wildcard entry — matches any source (remove in production!)",
                },
            };
            Save();
            return;
        }

        try
        {
            var json = File.ReadAllText(_filePath);
            _clients = JsonSerializer.Deserialize<List<NasClient>>(json, JsonOpts) ?? new();
        }
        catch
        {
            _clients = new();
        }
    }

    private void Save()
    {
        var dir = Path.GetDirectoryName(_filePath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        File.WriteAllText(_filePath, JsonSerializer.Serialize(_clients, JsonOpts));
    }
}

using System.Text.Json;
using SimpleRadius.Core.Models;

namespace SimpleRadius.Core.Storage;

/// <summary>
/// Thread-safe user store backed by a JSON file on disk.
/// Phase 1 stores passwords in plain text. Phase 2 will add bcrypt hashing.
/// </summary>
public sealed class UserStore
{
    private readonly string            _filePath;
    private readonly ReaderWriterLockSlim _lock = new();
    private List<UserEntry>            _users = new();

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
    };

    public UserStore(string filePath)
    {
        _filePath = filePath;
        Load();
    }

    // ── Queries ───────────────────────────────────────────────────────────────
    public UserEntry? Find(string username)
    {
        _lock.EnterReadLock();
        try
        {
            return _users.FirstOrDefault(u =>
                u.Username.Equals(username, StringComparison.OrdinalIgnoreCase));
        }
        finally { _lock.ExitReadLock(); }
    }

    public IReadOnlyList<UserEntry> GetAll()
    {
        _lock.EnterReadLock();
        try { return _users.AsReadOnly(); }
        finally { _lock.ExitReadLock(); }
    }

    // ── Mutations ─────────────────────────────────────────────────────────────
    public bool Add(UserEntry user)
    {
        _lock.EnterWriteLock();
        try
        {
            if (_users.Any(u => u.Username.Equals(user.Username, StringComparison.OrdinalIgnoreCase)))
                return false;
            _users.Add(user);
            Save();
            return true;
        }
        finally { _lock.ExitWriteLock(); }
    }

    public bool Update(UserEntry updated)
    {
        _lock.EnterWriteLock();
        try
        {
            var idx = _users.FindIndex(u =>
                u.Username.Equals(updated.Username, StringComparison.OrdinalIgnoreCase));
            if (idx < 0) return false;
            _users[idx] = updated;
            Save();
            return true;
        }
        finally { _lock.ExitWriteLock(); }
    }

    public bool Remove(string username)
    {
        _lock.EnterWriteLock();
        try
        {
            int removed = _users.RemoveAll(u =>
                u.Username.Equals(username, StringComparison.OrdinalIgnoreCase));
            if (removed > 0) Save();
            return removed > 0;
        }
        finally { _lock.ExitWriteLock(); }
    }

    // ── Authentication check ─────────────────────────────────────────────────
    /// <summary>
    /// Returns the user if credentials are valid and account is enabled.
    /// Returns null on any failure (intentionally vague — no oracle for attackers).
    /// </summary>
    public UserEntry? Authenticate(string username, string password)
    {
        var user = Find(username);
        if (user == null || !user.IsEnabled) return null;

        // Phase 1: plain-text compare
        // Phase 2: replace with BCrypt.Verify(password, user.PasswordHash)
        return user.Password == password ? user : null;
    }

    // ── Persistence ───────────────────────────────────────────────────────────
    private void Load()
    {
        if (!File.Exists(_filePath))
        {
            // Seed a default test user so the server works out-of-the-box
            _users = new List<UserEntry>
            {
                new() { Username = "testuser",  Password = "testpass",  Group = "default", VlanId = 1, IsEnabled = true, Description = "Default test account" },
                new() { Username = "admin",     Password = "adminpass", Group = "admins",  VlanId = 10, IsEnabled = true, Description = "Admin account" },
                new() { Username = "disabled",  Password = "any",       Group = "default", VlanId = 1,  IsEnabled = false, Description = "Disabled account example" },
            };
            Save();
            return;
        }

        try
        {
            var json = File.ReadAllText(_filePath);
            _users = JsonSerializer.Deserialize<List<UserEntry>>(json, JsonOpts) ?? new();
        }
        catch
        {
            _users = new();
        }
    }

    private void Save()
    {
        var dir = Path.GetDirectoryName(_filePath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        File.WriteAllText(_filePath, JsonSerializer.Serialize(_users, JsonOpts));
    }
}

using System.Text.Json;
using SimpleRadius.Core.Crypto;
using SimpleRadius.Core.Models;

namespace SimpleRadius.Core.Storage;

/// <summary>
/// Thread-safe user store backed by a JSON file on disk.
///
/// Password storage:
///   • New users      → bcrypt hash in PasswordHash + NT Hash in NtHash. Password field = "".
///   • Legacy users   → Password field is plain-text (Phase 1).
///                      Auto-migrated to bcrypt+NT Hash on first successful auth.
///
/// This means existing users.json files from Phase 1 keep working transparently.
/// </summary>
public sealed class UserStore
{
    private readonly string               _filePath;
    private readonly ReaderWriterLockSlim _lock = new();
    private List<UserEntry>               _users = new();

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented               = true,
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
    /// <summary>
    /// Add a new user. Automatically hashes the password before saving.
    /// Pass the plain-text password in user.Password — it will be replaced.
    /// </summary>
    public bool Add(UserEntry user)
    {
        _lock.EnterWriteLock();
        try
        {
            if (_users.Any(u => u.Username.Equals(user.Username, StringComparison.OrdinalIgnoreCase)))
                return false;

            HashAndClearPassword(user);
            _users.Add(user);
            Save();
            return true;
        }
        finally { _lock.ExitWriteLock(); }
    }

    /// <summary>
    /// Update an existing user.
    /// If updated.Password is non-empty it is treated as a new plain-text
    /// password and re-hashed. If empty, existing hashes are preserved.
    /// </summary>
    public bool Update(UserEntry updated)
    {
        _lock.EnterWriteLock();
        try
        {
            var idx = _users.FindIndex(u =>
                u.Username.Equals(updated.Username, StringComparison.OrdinalIgnoreCase));
            if (idx < 0) return false;

            var existing = _users[idx];

            // If a new plain-text password was supplied, hash it
            if (!string.IsNullOrEmpty(updated.Password))
                HashAndClearPassword(updated);
            else
            {
                // Keep existing hashes
                updated.PasswordHash = existing.PasswordHash;
                updated.NtHash       = existing.NtHash;
                updated.Password     = "";
            }

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
            int n = _users.RemoveAll(u =>
                u.Username.Equals(username, StringComparison.OrdinalIgnoreCase));
            if (n > 0) Save();
            return n > 0;
        }
        finally { _lock.ExitWriteLock(); }
    }

    // ── Authentication ────────────────────────────────────────────────────────

    /// <summary>
    /// Verify a plain-text password for PAP.
    /// Handles both bcrypt-hashed (Phase 2) and legacy plain-text (Phase 1) entries,
    /// auto-migrating the latter on success.
    /// Returns the UserEntry on success, null on failure.
    /// </summary>
    public UserEntry? Authenticate(string username, string plainPassword)
    {
        var user = Find(username);
        if (user == null || !user.IsEnabled) return null;

        bool valid = false;

        if (!string.IsNullOrEmpty(user.PasswordHash) && PasswordHelper.IsHashed(user.PasswordHash))
        {
            // Phase 2 path: verify against PBKDF2 hash
            valid = PasswordHelper.VerifyPassword(plainPassword, user.PasswordHash);
        }
        else if (!string.IsNullOrEmpty(user.Password))
        {
            // Phase 1 legacy path: plain-text compare
            valid = user.Password == plainPassword;

            if (valid)
            {
                // Auto-migrate to bcrypt + NT Hash
                _lock.EnterWriteLock();
                try
                {
                    var idx = _users.FindIndex(u =>
                        u.Username.Equals(username, StringComparison.OrdinalIgnoreCase));
                    if (idx >= 0)
                    {
                        _users[idx].Password = plainPassword;   // temporarily set so HashAndClearPassword works
                        HashAndClearPassword(_users[idx]);
                        Save();
                    }
                }
                finally { _lock.ExitWriteLock(); }
            }
        }

        return valid ? user : null;
    }

    /// <summary>
    /// Retrieve the NT Hash for a user by username.
    /// Used by CHAP and MSCHAPv2 handlers.
    /// Returns null if user not found, disabled, or NT Hash not yet generated.
    /// </summary>
    public byte[]? GetNtHash(string username)
    {
        var user = Find(username);
        if (user == null || !user.IsEnabled) return null;
        if (string.IsNullOrEmpty(user.NtHash)) return null;
        return PasswordHelper.FromHex(user.NtHash);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Hash the plain-text in user.Password into PasswordHash + NtHash, then clear Password.
    /// Call this before saving any user with a plain-text password.
    /// </summary>
    private static void HashAndClearPassword(UserEntry user)
    {
        if (string.IsNullOrEmpty(user.Password)) return;
        user.PasswordHash = PasswordHelper.HashPassword(user.Password);
        user.NtHash       = PasswordHelper.ToHex(PasswordHelper.NtHash(user.Password));
        user.Password     = "";
    }

    // ── Persistence ───────────────────────────────────────────────────────────
    private void Load()
    {
        if (!File.Exists(_filePath))
        {
            // Seed default users — passwords are hashed immediately
            _users = new List<UserEntry>
            {
                new() { Username = "testuser", Password = "testpass",  Group = "default", VlanId = 1,  IsEnabled = true,  Description = "Default test account" },
                new() { Username = "admin",    Password = "adminpass", Group = "admins",  VlanId = 10, IsEnabled = true,  Description = "Admin account"         },
                new() { Username = "disabled", Password = "any",       Group = "default", VlanId = 1,  IsEnabled = false, Description = "Disabled account example" },
            };
            foreach (var u in _users) HashAndClearPassword(u);
            Save();
            return;
        }

        try
        {
            var json = File.ReadAllText(_filePath);
            _users = JsonSerializer.Deserialize<List<UserEntry>>(json, JsonOpts) ?? new();

            // Migrate any plain-text passwords found in the file (Phase 1 → Phase 2)
            bool migrated = false;
            foreach (var u in _users)
            {
                if (!string.IsNullOrEmpty(u.Password))
                {
                    HashAndClearPassword(u);
                    migrated = true;
                }
            }
            if (migrated) Save();
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

using System.Text.Json;

namespace SimpleRadius.Core.Policy;

/// <summary>
/// Evaluates policy rules against an incoming authentication request.
///
/// Rules are loaded from policies.json and evaluated in ascending Priority order.
/// First matching rule wins — subsequent rules are not evaluated.
///
/// If no rule matches, a default "pass-through" result is returned
/// (no VLAN override, no timeout — user's own settings apply).
/// </summary>
public sealed class PolicyEngine
{
    private readonly string               _filePath;
    private readonly ReaderWriterLockSlim _lock = new();
    private List<PolicyRule>              _rules = new();

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented               = true,
        PropertyNameCaseInsensitive = true,
    };

    public PolicyEngine(string dataDir)
    {
        _filePath = Path.Combine(dataDir, "policies.json");
        Load();
    }

    // ── Evaluation ────────────────────────────────────────────────────────────
    /// <summary>
    /// Evaluate rules for an incoming authentication request.
    /// Returns the first matching PolicyResult, or a default pass-through.
    /// </summary>
    public PolicyResult Evaluate(string username, string group, string nasIp)
    {
        _lock.EnterReadLock();
        try
        {
            foreach (var rule in _rules.Where(r => r.IsEnabled).OrderBy(r => r.Priority))
            {
                if (!Matches(rule, username, group, nasIp)) continue;

                return new PolicyResult
                {
                    Matched            = true,
                    MatchedRule        = rule,
                    Reject             = rule.Reject,
                    VlanId             = rule.VlanId,
                    SessionTimeoutSecs = rule.SessionTimeoutSecs,
                    IdleTimeoutSecs    = rule.IdleTimeoutSecs,
                    ReplyMessage       = rule.ReplyMessage ?? $"Welcome, {username}!",
                };
            }

            // No rule matched — pass-through (user's own VLAN/timeout settings apply)
            return new PolicyResult { Matched = false };
        }
        finally { _lock.ExitReadLock(); }
    }

    // ── CRUD ──────────────────────────────────────────────────────────────────
    public IReadOnlyList<PolicyRule> GetAll()
    {
        _lock.EnterReadLock();
        try { return _rules.AsReadOnly(); }
        finally { _lock.ExitReadLock(); }
    }

    public bool Add(PolicyRule rule)
    {
        _lock.EnterWriteLock();
        try
        {
            if (_rules.Any(r => r.Name.Equals(rule.Name, StringComparison.OrdinalIgnoreCase)))
                return false;
            _rules.Add(rule);
            _rules = _rules.OrderBy(r => r.Priority).ToList();
            Save();
            return true;
        }
        finally { _lock.ExitWriteLock(); }
    }

    public bool Update(PolicyRule updated)
    {
        _lock.EnterWriteLock();
        try
        {
            var idx = _rules.FindIndex(r => r.Name.Equals(updated.Name, StringComparison.OrdinalIgnoreCase));
            if (idx < 0) return false;
            _rules[idx] = updated;
            _rules = _rules.OrderBy(r => r.Priority).ToList();
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
            int n = _rules.RemoveAll(r => r.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (n > 0) Save();
            return n > 0;
        }
        finally { _lock.ExitWriteLock(); }
    }

    // ── Match logic ───────────────────────────────────────────────────────────
    private static bool Matches(PolicyRule rule, string username, string group, string nasIp)
    {
        // Each condition: null = wildcard (skip check), non-null = must match
        if (rule.MatchUser  != null &&
            !rule.MatchUser.Equals(username, StringComparison.OrdinalIgnoreCase))
            return false;

        if (rule.MatchGroup != null &&
            !rule.MatchGroup.Equals(group, StringComparison.OrdinalIgnoreCase))
            return false;

        if (rule.MatchNasIp != null &&
            !rule.MatchNasIp.Equals(nasIp, StringComparison.OrdinalIgnoreCase))
            return false;

        return true;
    }

    // ── Persistence ───────────────────────────────────────────────────────────
    private void Load()
    {
        if (!File.Exists(_filePath))
        {
            // Seed sensible default policies
            _rules = new List<PolicyRule>
            {
                new()
                {
                    Priority           = 10,
                    Name               = "admins-vlan20",
                    IsEnabled          = true,
                    MatchGroup         = "admins",
                    VlanId             = 20,
                    SessionTimeoutSecs = 28800,   // 8 hours
                    ReplyMessage       = "Welcome, Admin!",
                },
                new()
                {
                    Priority           = 20,
                    Name               = "guests-vlan99",
                    IsEnabled          = true,
                    MatchGroup         = "guests",
                    VlanId             = 99,
                    SessionTimeoutSecs = 3600,    // 1 hour
                    IdleTimeoutSecs    = 600,     // 10 min idle
                    ReplyMessage       = "Welcome, Guest!",
                },
                new()
                {
                    Priority           = 30,
                    Name               = "iot-vlan50",
                    IsEnabled          = true,
                    MatchGroup         = "iot",
                    VlanId             = 50,
                    SessionTimeoutSecs = 0,
                    ReplyMessage       = "IoT device accepted.",
                },
                new()
                {
                    Priority           = 100,
                    Name               = "default-allow",
                    IsEnabled          = true,
                    MatchGroup         = null,    // wildcard — matches all
                    VlanId             = 1,
                    SessionTimeoutSecs = 0,
                    ReplyMessage       = "Welcome!",
                },
            };
            Save();
            return;
        }

        try
        {
            var json = File.ReadAllText(_filePath);
            _rules = JsonSerializer.Deserialize<List<PolicyRule>>(json, JsonOpts) ?? new();
            _rules = _rules.OrderBy(r => r.Priority).ToList();
        }
        catch { _rules = new(); }
    }

    private void Save()
    {
        var dir = Path.GetDirectoryName(_filePath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        File.WriteAllText(_filePath, JsonSerializer.Serialize(_rules, JsonOpts));
    }
}

namespace SimpleRadius.Core.Policy;

/// <summary>
/// A single policy rule. Rules are evaluated top-down; first match wins.
/// Conditions are ANDed together — all must match for the rule to fire.
/// </summary>
public sealed class PolicyRule
{
    public int     Priority    { get; set; } = 0;
    public string  Name        { get; set; } = "";
    public bool    IsEnabled   { get; set; } = true;

    // ── Match conditions (null = wildcard, matches anything) ──────────────────
    public string? MatchGroup  { get; set; }   // e.g. "admins"
    public string? MatchNasIp  { get; set; }   // e.g. "192.168.0.1"
    public string? MatchUser   { get; set; }   // exact username match

    // ── Reply attributes to return on match ───────────────────────────────────
    public int     VlanId              { get; set; } = 0;     // 0 = no VLAN
    public int     SessionTimeoutSecs  { get; set; } = 0;     // 0 = no limit
    public int     IdleTimeoutSecs     { get; set; } = 0;     // 0 = no limit
    public bool    Reject              { get; set; } = false; // true = reject on match
    public string? ReplyMessage        { get; set; }          // custom reply message

    public override string ToString() =>
        $"[{Priority}] {Name} → VLAN={VlanId} Timeout={SessionTimeoutSecs}s Reject={Reject}";
}

/// <summary>Result returned by the policy engine after evaluating a request.</summary>
public sealed class PolicyResult
{
    public bool        Matched           { get; init; }
    public PolicyRule? MatchedRule       { get; init; }
    public bool        Reject            { get; init; }
    public int         VlanId            { get; init; }
    public int         SessionTimeoutSecs { get; init; }
    public int         IdleTimeoutSecs   { get; init; }
    public string      ReplyMessage      { get; init; } = "";
}

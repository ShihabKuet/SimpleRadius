namespace SimpleRadius.Core.Models;

// ── Local user account ────────────────────────────────────────────────────────
public sealed class UserEntry
{
    public string Username     { get; set; } = "";
    public string Password     { get; set; } = "";   // plain-text for Phase 1; hashed in Phase 2
    public string Group        { get; set; } = "default";
    public int    VlanId       { get; set; } = 0;    // 0 = no VLAN assignment
    public bool   IsEnabled    { get; set; } = true;
    public string? Description { get; set; }

    // Reply attributes to return on accept (e.g. bandwidth limits, session timeout)
    public int    SessionTimeoutSeconds { get; set; } = 0;  // 0 = no limit

    public override string ToString() => $"{Username} [{Group}] VLAN={VlanId} Enabled={IsEnabled}";
}

// ── NAS (Network Access Server) client ───────────────────────────────────────
public sealed class NasClient
{
    public string  Name         { get; set; } = "";
    public string  IpAddress    { get; set; } = "";   // single IP or CIDR, e.g. "10.0.0.0/24"
    public string  SharedSecret { get; set; } = "";
    public string  Vendor       { get; set; } = "Generic";
    public string? Description  { get; set; }

    public override string ToString() => $"{Name} ({IpAddress}) [{Vendor}]";
}

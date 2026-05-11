namespace SimpleRadius.Core.Models;

// ── Local user account ────────────────────────────────────────────────────────
public sealed class UserEntry
{
    public string  Username     { get; set; } = "";

    /// <summary>
    /// Phase 1 legacy plain-text field. Kept for JSON migration only.
    /// After first successful auth this is cleared and PasswordHash is populated.
    /// Never use this directly — call UserStore.Authenticate() instead.
    /// </summary>
    public string  Password     { get; set; } = "";

    /// <summary>bcrypt hash of the password. Used for PAP verification.</summary>
    public string  PasswordHash { get; set; } = "";

    /// <summary>
    /// Hex-encoded NT Hash (MD4 of UTF-16LE password).
    /// Used for CHAP and MSCHAPv2 verification.
    /// </summary>
    public string  NtHash       { get; set; } = "";

    public string  Group        { get; set; } = "default";
    public int     VlanId       { get; set; } = 0;
    public bool    IsEnabled    { get; set; } = true;
    public string? Description  { get; set; }
    public int     SessionTimeoutSeconds { get; set; } = 0;

    public override string ToString() => $"{Username} [{Group}] VLAN={VlanId} Enabled={IsEnabled}";
}

// ── NAS (Network Access Server) client ───────────────────────────────────────
public sealed class NasClient
{
    public string  Name         { get; set; } = "";
    public string  IpAddress    { get; set; } = "";
    public string  SharedSecret { get; set; } = "";
    public string  Vendor       { get; set; } = "Generic";
    public string? Description  { get; set; }

    public override string ToString() => $"{Name} ({IpAddress}) [{Vendor}]";
}


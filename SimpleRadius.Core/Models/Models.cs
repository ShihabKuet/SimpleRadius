namespace SimpleRadius.Core.Models;

public sealed class UserEntry
{
    public string  Username     { get; set; } = "";
    public string  Password     { get; set; } = "";
    public string  PasswordHash { get; set; } = "";
    public string  NtHash       { get; set; } = "";
    public string  Group        { get; set; } = "default";
    public int     VlanId       { get; set; } = 0;
    public bool    IsEnabled    { get; set; } = true;
    public string? Description  { get; set; }
    public int     SessionTimeoutSeconds { get; set; } = 0;

    // ── Billing / Quota ───────────────────────────────────────────────────────
    /// <summary>Monthly data download quota in MB. 0 = unlimited.</summary>
    public long DataQuotaMb      { get; set; } = 0;

    /// <summary>Monthly session time quota in hours. 0 = unlimited.</summary>
    public int  TimeQuotaHours   { get; set; } = 0;

    /// <summary>Day of month the billing cycle resets (1–28). Default = 1.</summary>
    public int  BillingCycleDay  { get; set; } = 1;

    /// <summary>Price per GB in local currency (for report display only).</summary>
    public decimal PricePerGb    { get; set; } = 0;

    /// <summary>Price per hour in local currency (for report display only).</summary>
    public decimal PricePerHour  { get; set; } = 0;

    public override string ToString() => $"{Username} [{Group}] VLAN={VlanId} Enabled={IsEnabled}";
}

public sealed class NasClient
{
    public string  Name         { get; set; } = "";
    public string  IpAddress    { get; set; } = "";
    public string  SharedSecret { get; set; } = "";
    public string  Vendor       { get; set; } = "Generic";
    public string? Description  { get; set; }
    public override string ToString() => $"{Name} ({IpAddress}) [{Vendor}]";
}


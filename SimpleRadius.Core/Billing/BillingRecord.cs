namespace SimpleRadius.Core.Billing;

/// <summary>
/// Per-user billing summary for a single billing cycle.
/// Computed on-demand from the accounting session log.
/// </summary>
public sealed class BillingRecord
{
    public string   Username          { get; set; } = "";
    public string   Group             { get; set; } = "";
    public DateTime PeriodStart       { get; set; }
    public DateTime PeriodEnd         { get; set; }

    // ── Usage ─────────────────────────────────────────────────────────────────
    public long     TotalSessions     { get; set; }
    public long     TotalSeconds      { get; set; }
    public long     TotalInputMb      { get; set; }
    public long     TotalOutputMb     { get; set; }
    public long     TotalDataMb       => TotalInputMb + TotalOutputMb;

    // ── Quota ─────────────────────────────────────────────────────────────────
    public long     DataQuotaMb       { get; set; }   // 0 = unlimited
    public int      TimeQuotaHours    { get; set; }   // 0 = unlimited

    // ── Cost ─────────────────────────────────────────────────────────────────
    public decimal  PricePerGb        { get; set; }
    public decimal  PricePerHour      { get; set; }

    // ── Computed helpers ──────────────────────────────────────────────────────
    public double   DataUsedPercent   => DataQuotaMb  > 0 ? Math.Min(100, TotalDataMb  * 100.0 / DataQuotaMb)  : 0;
    public double   TimeUsedPercent   => TimeQuotaHours > 0 ? Math.Min(100, TotalHours * 100.0 / TimeQuotaHours) : 0;
    public double   TotalHours        => TotalSeconds / 3600.0;
    public bool     DataQuotaExceeded => DataQuotaMb  > 0 && TotalDataMb  >= DataQuotaMb;
    public bool     TimeQuotaExceeded => TimeQuotaHours > 0 && TotalHours >= TimeQuotaHours;
    public bool     QuotaExceeded     => DataQuotaExceeded || TimeQuotaExceeded;

    public decimal  DataCost          => PricePerGb   > 0 ? Math.Round(TotalDataMb / 1024m * PricePerGb,  2) : 0;
    public decimal  TimeCost          => PricePerHour > 0 ? Math.Round((decimal)TotalHours * PricePerHour, 2) : 0;
    public decimal  TotalCost         => DataCost + TimeCost;

    public string   DataUsageSummary  => DataQuotaMb > 0
        ? $"{TotalDataMb:N0} / {DataQuotaMb:N0} MB ({DataUsedPercent:F1}%)"
        : $"{TotalDataMb:N0} MB (unlimited)";

    public string   TimeUsageSummary  => TimeQuotaHours > 0
        ? $"{TotalHours:F1} / {TimeQuotaHours} h ({TimeUsedPercent:F1}%)"
        : $"{TotalHours:F1} h (unlimited)";

    public string   StatusText        => QuotaExceeded ? "Quota Exceeded" : "Active";
}

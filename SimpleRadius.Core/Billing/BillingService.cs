using SimpleRadius.Core.Accounting;
using SimpleRadius.Core.Models;
using SimpleRadius.Core.Storage;

namespace SimpleRadius.Core.Billing;

/// <summary>
/// Computes per-user billing records from the accounting session log.
/// Also enforces data and time quotas — called by RadiusServer before
/// issuing an Access-Accept to check if the user is over their limit.
/// </summary>
public sealed class BillingService
{
    private readonly AccountingStore _accounting;
    private readonly UserStore       _users;

    public BillingService(AccountingStore accounting, UserStore users)
    {
        _accounting = accounting;
        _users      = users;
    }

    // ── Quota check (called during authentication) ────────────────────────────

    /// <summary>
    /// Returns true if the user has exceeded their data or time quota
    /// for the current billing cycle. Returns false if no quota is set.
    /// </summary>
    public bool IsQuotaExceeded(string username)
    {
        var user = _users.Find(username);
        if (user == null) return false;
        if (user.DataQuotaMb == 0 && user.TimeQuotaHours == 0) return false;

        var record = GetCurrentPeriodRecord(user);
        return record.QuotaExceeded;
    }

    // ── Billing record computation ────────────────────────────────────────────

    /// <summary>
    /// Compute the billing record for a user for their current billing cycle.
    /// </summary>
    public BillingRecord GetCurrentPeriodRecord(UserEntry user)
    {
        var (start, end) = GetCurrentBillingPeriod(user.BillingCycleDay);
        return ComputeRecord(user, start, end);
    }

    /// <summary>
    /// Compute the billing record for a specific period (for history view).
    /// </summary>
    public BillingRecord GetPeriodRecord(UserEntry user, DateTime start, DateTime end)
        => ComputeRecord(user, start, end);

    /// <summary>
    /// Get billing summaries for all users for the current billing cycle.
    /// </summary>
    public List<BillingRecord> GetAllCurrentRecords()
    {
        var records = new List<BillingRecord>();
        foreach (var user in _users.GetAll())
            records.Add(GetCurrentPeriodRecord(user));
        return records.OrderBy(r => r.Username).ToList();
    }

    /// <summary>
    /// Get billing history for a user — last N billing cycles.
    /// </summary>
    public List<BillingRecord> GetHistory(UserEntry user, int cycles = 6)
    {
        var records = new List<BillingRecord>();
        var (currentStart, _) = GetCurrentBillingPeriod(user.BillingCycleDay);

        for (int i = 0; i < cycles; i++)
        {
            var start = currentStart.AddMonths(-i);
            var end   = start.AddMonths(1).AddSeconds(-1);
            records.Add(ComputeRecord(user, start, end));
        }

        return records;
    }

    // ── CSV export ────────────────────────────────────────────────────────────

    public string ExportAllCsv()
    {
        var records = GetAllCurrentRecords();
        var sb      = new System.Text.StringBuilder();
        sb.AppendLine("Username,Group,Period,Sessions,DataMB,QuotaMB,UsedPct,Hours,QuotaHours,DataCost,TimeCost,TotalCost,Status");
        foreach (var r in records)
            sb.AppendLine(
                $"{r.Username},{r.Group}," +
                $"{r.PeriodStart:yyyy-MM-dd}~{r.PeriodEnd:yyyy-MM-dd}," +
                $"{r.TotalSessions},{r.TotalDataMb},{r.DataQuotaMb},{r.DataUsedPercent:F1}%," +
                $"{r.TotalHours:F1},{r.TimeQuotaHours}," +
                $"{r.DataCost},{r.TimeCost},{r.TotalCost},{r.StatusText}");
        return sb.ToString();
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private BillingRecord ComputeRecord(UserEntry user, DateTime start, DateTime end)
    {
        var sessions = _accounting.GetSessions(from: start, to: end, username: user.Username,
                                               limit: 100_000);
        return new BillingRecord
        {
            Username        = user.Username,
            Group           = user.Group,
            PeriodStart     = start,
            PeriodEnd       = end,
            TotalSessions   = sessions.Count,
            TotalSeconds    = sessions.Sum(s => (long)s.SessionSeconds),
            TotalInputMb    = sessions.Sum(s => s.InputOctets)  / 1_048_576,
            TotalOutputMb   = sessions.Sum(s => s.OutputOctets) / 1_048_576,
            DataQuotaMb     = user.DataQuotaMb,
            TimeQuotaHours  = user.TimeQuotaHours,
            PricePerGb      = user.PricePerGb,
            PricePerHour    = user.PricePerHour,
        };
    }

    /// <summary>
    /// Returns the start and end of the current billing cycle based on
    /// the user's configured billing cycle day (1–28).
    /// e.g. BillingCycleDay=5 → period runs from the 5th of this month
    /// to the 4th of next month.
    /// </summary>
    private static (DateTime Start, DateTime End) GetCurrentBillingPeriod(int cycleDay)
    {
        cycleDay = Math.Clamp(cycleDay, 1, 28);
        var now  = DateTime.UtcNow;

        DateTime start;
        if (now.Day >= cycleDay)
            start = new DateTime(now.Year, now.Month, cycleDay, 0, 0, 0, DateTimeKind.Utc);
        else
            start = new DateTime(now.Year, now.Month, cycleDay, 0, 0, 0, DateTimeKind.Utc)
                        .AddMonths(-1);

        var end = start.AddMonths(1).AddSeconds(-1);
        return (start, end);
    }
}

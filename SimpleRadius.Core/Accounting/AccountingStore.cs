using System.Text.Json;

namespace SimpleRadius.Core.Accounting;

/// <summary>
/// Thread-safe accounting store backed by a JSON file.
/// Handles the full session lifecycle: Start → Interim-Update → Stop.
///
/// Sessions are kept in memory for fast querying and flushed to
/// accounting.json on every write. The file is human-readable and
/// can be imported into Excel or any JSON viewer.
/// </summary>
public sealed class AccountingStore
{
    private readonly string               _filePath;
    private readonly ReaderWriterLockSlim _lock = new();
    private List<AccountingSession>       _sessions = new();
    private long                          _nextId   = 1;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented               = true,
        PropertyNameCaseInsensitive = true,
    };

    public AccountingStore(string dataDir)
    {
        Directory.CreateDirectory(dataDir);
        _filePath = Path.Combine(dataDir, "accounting.json");
        Load();
    }

    // ── Write operations ──────────────────────────────────────────────────────

    /// <summary>Called when Acct-Status-Type = Start.</summary>
    public void SessionStart(AccountingSession s)
    {
        _lock.EnterWriteLock();
        try
        {
            s.Id = _nextId++;
            _sessions.Insert(0, s);
            // Keep at most 10,000 sessions in memory / file
            if (_sessions.Count > 10_000)
                _sessions.RemoveAt(_sessions.Count - 1);
            Save();
        }
        finally { _lock.ExitWriteLock(); }
    }

    /// <summary>Called when Acct-Status-Type = Interim-Update.</summary>
    public void SessionInterim(string sessionId, int seconds, long inputOctets, long outputOctets)
    {
        _lock.EnterWriteLock();
        try
        {
            var s = _sessions.FirstOrDefault(x => x.SessionId == sessionId && x.StopTime == null);
            if (s != null)
            {
                s.SessionSeconds = seconds;
                s.InputOctets    = inputOctets;
                s.OutputOctets   = outputOctets;
                Save();
            }
        }
        finally { _lock.ExitWriteLock(); }
    }

    /// <summary>Called when Acct-Status-Type = Stop.</summary>
    public void SessionStop(string sessionId, int seconds, long inputOctets,
                            long outputOctets, TerminateCause cause)
    {
        _lock.EnterWriteLock();
        try
        {
            var s = _sessions.FirstOrDefault(x => x.SessionId == sessionId && x.StopTime == null);
            if (s != null)
            {
                s.StopTime       = DateTime.UtcNow;
                s.SessionSeconds = seconds;
                s.InputOctets    = inputOctets;
                s.OutputOctets   = outputOctets;
                s.TerminateCause = cause;
                Save();
            }
        }
        finally { _lock.ExitWriteLock(); }
    }

    // ── Query operations ──────────────────────────────────────────────────────

    /// <summary>All currently active sessions (no stop time).</summary>
    public List<AccountingSession> GetActiveSessions()
    {
        _lock.EnterReadLock();
        try { return _sessions.Where(s => s.IsActive).ToList(); }
        finally { _lock.ExitReadLock(); }
    }

    /// <summary>Filtered session query with optional date range and username.</summary>
    public List<AccountingSession> GetSessions(
        DateTime? from     = null,
        DateTime? to       = null,
        string?   username = null,
        int       limit    = 500)
    {
        _lock.EnterReadLock();
        try
        {
            var q = _sessions.AsEnumerable();
            if (from     != null) q = q.Where(s => s.StartTime >= from.Value);
            if (to       != null) q = q.Where(s => s.StartTime <= to.Value);
            if (username != null) q = q.Where(s =>
                s.Username.Equals(username, StringComparison.OrdinalIgnoreCase));
            return q.Take(limit).ToList();
        }
        finally { _lock.ExitReadLock(); }
    }

    /// <summary>Summary statistics for the accounting dashboard.</summary>
    public (long TotalSessions, long ActiveSessions, long TotalInputMb, long TotalOutputMb) GetStats()
    {
        _lock.EnterReadLock();
        try
        {
            return (
                _sessions.Count,
                _sessions.Count(s => s.IsActive),
                _sessions.Sum(s => s.InputOctets)  / 1_048_576,
                _sessions.Sum(s => s.OutputOctets) / 1_048_576
            );
        }
        finally { _lock.ExitReadLock(); }
    }

    /// <summary>Export filtered sessions to a CSV string.</summary>
    public string ExportCsv(DateTime? from = null, DateTime? to = null, string? username = null)
    {
        var sessions = GetSessions(from, to, username, limit: 100_000);
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("SessionId,Username,NasIp,StartTime,StopTime,Duration,InputMB,OutputMB,TerminateCause,AuthMethod");
        foreach (var s in sessions)
            sb.AppendLine(
                $"{s.SessionId},{s.Username},{s.NasIp}," +
                $"{s.StartTime:yyyy-MM-dd HH:mm:ss}," +
                $"{s.StopTime?.ToString("yyyy-MM-dd HH:mm:ss") ?? ""}," +
                $"{s.Duration},{s.InputMb},{s.OutputMb}," +
                $"{s.TerminateCause},{s.AuthMethod}");
        return sb.ToString();
    }

    // ── Persistence ───────────────────────────────────────────────────────────
    private void Load()
    {
        if (!File.Exists(_filePath)) return;
        try
        {
            var json = File.ReadAllText(_filePath);
            _sessions = JsonSerializer.Deserialize<List<AccountingSession>>(json, JsonOpts) ?? new();
            _nextId   = _sessions.Count > 0 ? _sessions.Max(s => s.Id) + 1 : 1;
        }
        catch { _sessions = new(); }
    }

    private void Save()
    {
        File.WriteAllText(_filePath, JsonSerializer.Serialize(_sessions, JsonOpts));
    }
}

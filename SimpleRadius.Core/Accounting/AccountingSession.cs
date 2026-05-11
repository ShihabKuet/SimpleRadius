namespace SimpleRadius.Core.Accounting;

// ── Accounting status types (RFC 2866 §5.41) ──────────────────────────────────
public enum AcctStatusType
{
    Start         = 1,
    Stop          = 2,
    InterimUpdate = 3,
    AccountingOn  = 7,
    AccountingOff = 8,
}

// ── Termination causes (RFC 2866 §5.48) ──────────────────────────────────────
public enum TerminateCause
{
    Unknown          = 0,
    UserRequest      = 1,
    LostCarrier      = 2,
    LostService      = 3,
    IdleTimeout      = 4,
    SessionTimeout   = 5,
    AdminReset       = 6,
    AdminReboot      = 7,
    PortError        = 8,
    NasError         = 9,
    NasRequest       = 10,
    NasReboot        = 11,
    PortUnneeded     = 12,
    PortPreempted    = 13,
    PortSuspended    = 14,
    ServiceUnavailable = 15,
    Callback         = 16,
    UserError        = 17,
    HostRequest      = 18,
}

// ── A single accounting session record ───────────────────────────────────────
public sealed class AccountingSession
{
    public long     Id               { get; set; }   // SQLite rowid
    public string   SessionId        { get; set; } = "";
    public string   Username         { get; set; } = "";
    public string   NasIp            { get; set; } = "";
    public string   NasIdentifier    { get; set; } = "";
    public string   CalledStationId  { get; set; } = "";
    public string   CallingStationId { get; set; } = "";
    public string   FramedIpAddress  { get; set; } = "";
    public DateTime StartTime        { get; set; }
    public DateTime? StopTime        { get; set; }
    public int      SessionSeconds   { get; set; }
    public long     InputOctets      { get; set; }
    public long     OutputOctets     { get; set; }
    public TerminateCause TerminateCause { get; set; } = TerminateCause.Unknown;
    public string   AuthMethod       { get; set; } = "";

    // ── Computed helpers ──────────────────────────────────────────────────────
    public bool    IsActive   => StopTime == null;
    public string  Duration   => TimeSpan.FromSeconds(SessionSeconds).ToString(@"hh\:mm\:ss");
    public string  InputMb    => $"{InputOctets  / 1_048_576.0:F2} MB";
    public string  OutputMb   => $"{OutputOctets / 1_048_576.0:F2} MB";
    public string  StatusText => IsActive ? "Active" : "Stopped";
}

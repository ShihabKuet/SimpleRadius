using System.Net;
using System.Net.Sockets;
using System.Text;
using SimpleRadius.Core.Accounting;
using SimpleRadius.Core.Auth;
using SimpleRadius.Core.Billing;
using SimpleRadius.Core.Models;
using SimpleRadius.Core.Policy;
using SimpleRadius.Core.Protocol;
using SimpleRadius.Core.Storage;

namespace SimpleRadius.Core.Server;

// ── Auth event ────────────────────────────────────────────────────────────────
public sealed class AuthEventArgs : EventArgs
{
    public string   Username  { get; init; } = "";
    public string   NasIp     { get; init; } = "";
    public string   Method    { get; init; } = "";
    public bool     Accepted  { get; init; }
    public string   Reason    { get; init; } = "";
    public DateTime Timestamp { get; init; } = DateTime.Now;
}

// ── Server config ─────────────────────────────────────────────────────────────
public sealed class RadiusServerConfig
{
    public int    AuthPort    { get; set; } = 1812;
    public int    AcctPort    { get; set; } = 1813;
    public string BindAddress { get; set; } = "0.0.0.0";
    public string DataDir     { get; set; } = "data";
}

// ── Main server ───────────────────────────────────────────────────────────────
public sealed class RadiusServer : IDisposable
{
    private readonly RadiusServerConfig _config;
    private readonly UserStore          _users;
    private readonly NasStore           _nas;
    private readonly AccountingStore    _accounting;
    private readonly PolicyEngine       _policy;
    private readonly BillingService     _billing;
    private readonly IRadiusLogger      _logger;

    private UdpClient?               _authSocket;
    private UdpClient?               _acctSocket;
    private CancellationTokenSource? _cts;
    private Task?                    _authTask;
    private Task?                    _acctTask;

    // ── Statistics ────────────────────────────────────────────────────────────
    private long _totalRequests;
    private long _totalAccepts;
    private long _totalRejects;
    private long _totalAccounting;
    private readonly DateTime _startTime = DateTime.Now;

    public long     TotalRequests   => Interlocked.Read(ref _totalRequests);
    public long     TotalAccepts    => Interlocked.Read(ref _totalAccepts);
    public long     TotalRejects    => Interlocked.Read(ref _totalRejects);
    public long     TotalAccounting => Interlocked.Read(ref _totalAccounting);
    public TimeSpan Uptime          => IsRunning ? DateTime.Now - _startTime : TimeSpan.Zero;
    public bool     IsRunning       { get; private set; }

    // ── Events ────────────────────────────────────────────────────────────────
    public event EventHandler<AuthEventArgs>? OnAuthEvent;
    public event EventHandler<string>?        OnLog;

    // ── Public store accessors ────────────────────────────────────────────────
    public UserStore       Users      => _users;
    public NasStore        Nas        => _nas;
    public AccountingStore Accounting => _accounting;
    public PolicyEngine    Policy     => _policy;
    public BillingService  Billing    => _billing;

    // ── Constructor ───────────────────────────────────────────────────────────
    public RadiusServer(RadiusServerConfig config, IRadiusLogger? logger = null)
    {
        _config     = config;
        _logger     = logger ?? new ConsoleRadiusLogger();
        Directory.CreateDirectory(config.DataDir);
        _users      = new UserStore(Path.Combine(config.DataDir, "users.json"));
        _nas        = new NasStore(Path.Combine(config.DataDir,  "nas.json"));
        _accounting = new AccountingStore(config.DataDir);
        _policy     = new PolicyEngine(config.DataDir);
        _billing    = new BillingService(_accounting, _users);
    }

    // ── Start / Stop ──────────────────────────────────────────────────────────
    public void Start()
    {
        if (IsRunning) return;
        var bindIp  = IPAddress.Parse(_config.BindAddress);
        _authSocket = new UdpClient(new IPEndPoint(bindIp, _config.AuthPort));
        _acctSocket = new UdpClient(new IPEndPoint(bindIp, _config.AcctPort));
        _cts        = new CancellationTokenSource();
        _authTask   = Task.Run(() => ListenLoop(_authSocket, "AUTH", _cts.Token));
        _acctTask   = Task.Run(() => ListenLoop(_acctSocket, "ACCT", _cts.Token));
        IsRunning   = true;
        Log($"Simple Radius started — Auth:{_config.AuthPort}  Acct:{_config.AcctPort}");
    }

    public void Stop()
    {
        if (!IsRunning) return;
        _cts?.Cancel();
        _authSocket?.Close();
        _acctSocket?.Close();
        Task.WhenAll(_authTask ?? Task.CompletedTask, _acctTask ?? Task.CompletedTask)
            .Wait(TimeSpan.FromSeconds(3));
        IsRunning = false;
        Log("Simple Radius stopped.");
    }

    // ── UDP listener loop ─────────────────────────────────────────────────────
    private async Task ListenLoop(UdpClient socket, string label, CancellationToken ct)
    {
        Log($"[{label}] Listening on port {((IPEndPoint)socket.Client.LocalEndPoint!).Port}");
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var result = await socket.ReceiveAsync(ct);
                _ = Task.Run(() => HandlePacket(result.Buffer, result.RemoteEndPoint, socket), ct);
            }
            catch (OperationCanceledException) { break; }
            catch (SocketException) when (ct.IsCancellationRequested) { break; }
            catch (Exception ex) { Log($"[{label}] Receive error: {ex.Message}"); }
        }
        Log($"[{label}] Listener stopped.");
    }

    // ── Packet dispatcher ─────────────────────────────────────────────────────
    private async Task HandlePacket(byte[] data, IPEndPoint remote, UdpClient socket)
    {
        Interlocked.Increment(ref _totalRequests);
        var pkt = RadiusPacket.Parse(data);
        if (pkt == null) { Log($"[WARN] Malformed packet from {remote.Address} — discarded"); return; }

        var nas = _nas.FindByIp(remote.Address);
        if (nas == null) { Log($"[WARN] Unknown NAS {remote.Address} — discarded"); return; }

        Log($"[RECV] {pkt.Code} from {remote.Address} ({nas.Name})  Id={pkt.Identifier}  User={pkt.UserName ?? "(none)"}");

        RadiusPacket? response = pkt.Code switch
        {
            RadiusCode.AccessRequest     => HandleAccessRequest(pkt, nas, remote),
            RadiusCode.AccountingRequest => HandleAccountingRequest(pkt, nas, remote),
            _                            => null,
        };

        if (response != null)
        {
            var bytes = response.Encode();
            await socket.SendAsync(bytes, bytes.Length, remote);
            Log($"[SEND] {response.Code} → {remote.Address}  Id={response.Identifier}");
        }
    }

    // ── Access-Request ────────────────────────────────────────────────────────
    private RadiusPacket HandleAccessRequest(RadiusPacket req, NasClient nas, IPEndPoint remote)
    {
        string username = req.UserName ?? "";
        if (string.IsNullOrEmpty(username))
            return Reject(req, nas.SharedSecret, "No username");

        bool hasPap     = req.GetAttribute(RadiusAttributeType.UserPassword)  != null;
        bool hasChap    = req.GetAttribute(RadiusAttributeType.ChapPassword)  != null;
        bool hasMsChap2 = HasMsChapV2Response(req);

        if (hasMsChap2) return HandleMsChapV2(req, nas, remote, username);
        if (hasChap)    return HandleChap(req, nas, remote, username);
        if (hasPap)     return HandlePap(req, nas, remote, username);

        Log($"[AUTH] {username} — unsupported auth method");
        return Reject(req, nas.SharedSecret, "Unsupported auth method");
    }

    // ── PAP ───────────────────────────────────────────────────────────────────
    private RadiusPacket HandlePap(RadiusPacket req, NasClient nas, IPEndPoint remote, string username)
    {
        string? password = req.DecryptPapPassword(nas.SharedSecret, req.Authenticator);
        if (password == null)
            return Reject(req, nas.SharedSecret, "Password decryption failed");

        var user = _users.Authenticate(username, password);
        if (user == null)
            return RejectAndLog(req, nas, remote, username, "PAP", "Invalid credentials");

        return AcceptWithPolicy(req, nas, remote, user, "PAP");
    }

    // ── CHAP ──────────────────────────────────────────────────────────────────
    private RadiusPacket HandleChap(RadiusPacket req, NasClient nas, IPEndPoint remote, string username)
    {
        bool ok = ChapAuthHandler.Authenticate(req, _users, out _, out string reason);
        if (!ok) return RejectAndLog(req, nas, remote, username, "CHAP", reason);

        var user = _users.Find(username);
        if (user == null || !user.IsEnabled)
            return RejectAndLog(req, nas, remote, username, "CHAP", "User not found or disabled");

        return AcceptWithPolicy(req, nas, remote, user, "CHAP");
    }

    // ── MSCHAPv2 ──────────────────────────────────────────────────────────────
    private RadiusPacket HandleMsChapV2(RadiusPacket req, NasClient nas, IPEndPoint remote, string username)
    {
        var authChallenge   = GetMsChapChallenge(req);
        var msChap2Response = GetMsChap2Response(req);

        if (authChallenge == null || authChallenge.Length < 16)
            return RejectAndLog(req, nas, remote, username, "MSCHAPv2", "Missing MS-CHAP-Challenge");
        if (msChap2Response == null || msChap2Response.Length < 50)
            return RejectAndLog(req, nas, remote, username, "MSCHAPv2", "Missing MS-CHAP2-Response");

        byte[] peerChallenge = msChap2Response[2..18];
        byte[] ntResponse    = msChap2Response[24..48];

        bool ok = MsChapV2Handler.Authenticate(username, authChallenge, peerChallenge,
                      ntResponse, _users, out string authResponse, out string reason);

        if (!ok) return RejectAndLog(req, nas, remote, username, "MSCHAPv2", reason);

        var user = _users.Find(username);
        if (user == null || !user.IsEnabled)
            return RejectAndLog(req, nas, remote, username, "MSCHAPv2", "User disabled");

        var accept = AcceptWithPolicy(req, nas, remote, user, "MSCHAPv2");

        // Attach MS-CHAP2-Success for mutual auth
        byte ident        = msChap2Response[0];
        var successMsg    = Encoding.ASCII.GetBytes($"{ident} {authResponse}");
        accept.Attributes.Add(BuildMicrosoftVsa(26, successMsg));
        accept.SetResponseAuthenticator(req.Authenticator, nas.SharedSecret);
        return accept;
    }

    // ── Accounting-Request ────────────────────────────────────────────────────
    private RadiusPacket HandleAccountingRequest(RadiusPacket req, NasClient nas, IPEndPoint remote)
    {
        Interlocked.Increment(ref _totalAccounting);

        var statusAttr = req.GetAttribute(RadiusAttributeType.AcctStatusType);
        int statusVal  = (int)(statusAttr?.AsUInt32() ?? 0);
        var status     = (AcctStatusType)statusVal;

        string sessionId = req.GetAttribute(RadiusAttributeType.AcctSessionId)?.AsString()   ?? "";
        string username  = req.UserName ?? "";
        int    seconds   = (int)(req.GetAttribute(RadiusAttributeType.AcctSessionTime)?.AsUInt32() ?? 0);
        long   inputOct  = (long)(req.GetAttribute(RadiusAttributeType.AcctInputOctets)?.AsUInt32()  ?? 0);
        long   outputOct = (long)(req.GetAttribute(RadiusAttributeType.AcctOutputOctets)?.AsUInt32() ?? 0);

        try
        {
            switch (status)
            {
                case AcctStatusType.Start:
                    _accounting.SessionStart(new AccountingSession
                    {
                        SessionId        = sessionId,
                        Username         = username,
                        NasIp            = remote.Address.ToString(),
                        NasIdentifier    = req.NasIdentifier ?? "",
                        CalledStationId  = req.GetAttribute(RadiusAttributeType.CalledStationId)?.AsString()  ?? "",
                        CallingStationId = req.GetAttribute(RadiusAttributeType.CallingStationId)?.AsString() ?? "",
                        FramedIpAddress  = req.GetAttribute(RadiusAttributeType.FramedIpAddress)?.AsIpString()  ?? "",
                        StartTime        = DateTime.UtcNow,
                    });
                    Log($"[ACCT] Start — User={username}  Session={sessionId}");
                    break;

                case AcctStatusType.InterimUpdate:
                    _accounting.SessionInterim(sessionId, seconds, inputOct, outputOct);
                    Log($"[ACCT] Interim — User={username}  Session={sessionId}  {seconds}s  In={inputOct}  Out={outputOct}");
                    break;

                case AcctStatusType.Stop:
                    var causeAttr = req.GetAttribute(RadiusAttributeType.AcctTerminateCause);
                    var cause     = causeAttr != null ? (TerminateCause)causeAttr.AsUInt32() : TerminateCause.Unknown;
                    _accounting.SessionStop(sessionId, seconds, inputOct, outputOct, cause);
                    Log($"[ACCT] Stop — User={username}  Session={sessionId}  {seconds}s  In={inputOct}  Out={outputOct}  Cause={cause}");
                    break;

                default:
                    Log($"[ACCT] {status} — User={username}");
                    break;
            }
        }
        catch (Exception ex)
        {
            Log($"[ACCT] DB error: {ex.Message}");
        }

        var response = new RadiusPacket { Code = RadiusCode.AccountingResponse, Identifier = req.Identifier };
        response.SetResponseAuthenticator(req.Authenticator, nas.SharedSecret);
        return response;
    }

    // ── Accept with policy override ───────────────────────────────────────────
    private RadiusPacket AcceptWithPolicy(RadiusPacket req, NasClient nas,
                                          IPEndPoint remote, UserEntry user, string method)
    {
        // Run policy engine — may override user's VLAN/timeout
        var policy = _policy.Evaluate(user.Username, user.Group, remote.Address.ToString());

        // Policy can force a reject
        if (policy.Matched && policy.Reject)
            return RejectAndLog(req, nas, remote, user.Username, method,
                                $"Rejected by policy: {policy.MatchedRule?.Name}");

        // Quota enforcement — reject if user exceeded data or time limit
        if (_billing.IsQuotaExceeded(user.Username))
            return RejectAndLog(req, nas, remote, user.Username, method,
                                "Quota exceeded — billing limit reached");

        int    vlan    = policy.Matched && policy.VlanId > 0 ? policy.VlanId : user.VlanId;
        int    timeout = policy.Matched && policy.SessionTimeoutSecs > 0
                             ? policy.SessionTimeoutSecs : user.SessionTimeoutSeconds;
        int    idle    = policy.Matched ? policy.IdleTimeoutSecs : 0;
        string reply   = policy.Matched && !string.IsNullOrEmpty(policy.ReplyMessage)
                             ? policy.ReplyMessage : $"Welcome, {user.Username}!";

        string policyLabel = policy.Matched ? $" Policy={policy.MatchedRule?.Name}" : "";
        Interlocked.Increment(ref _totalAccepts);
        Log($"[AUTH] ACCEPT ({method}) — {user.Username} from {remote.Address}  Group={user.Group}  VLAN={vlan}{policyLabel}");
        FireAuthEvent(user.Username, remote, method, true, $"Group={user.Group}");

        var accept = new RadiusPacket { Code = RadiusCode.AccessAccept, Identifier = req.Identifier };
        accept.Attributes.Add(new(RadiusAttributeType.ReplyMessage, reply));

        if (vlan > 0)
        {
            accept.Attributes.Add(new(RadiusAttributeType.TunnelType,           (uint)13));
            accept.Attributes.Add(new(RadiusAttributeType.TunnelMediumType,     (uint)6));
            accept.Attributes.Add(new(RadiusAttributeType.TunnelPrivateGroupId, vlan.ToString()));
        }
        if (timeout > 0)
            accept.Attributes.Add(new(RadiusAttributeType.SessionTimeout, (uint)timeout));
        if (idle > 0)
            accept.Attributes.Add(new(RadiusAttributeType.IdleTimeout, (uint)idle));

        accept.SetResponseAuthenticator(req.Authenticator, nas.SharedSecret);
        return accept;
    }

    // ── Reject helpers ────────────────────────────────────────────────────────
    private RadiusPacket RejectAndLog(RadiusPacket req, NasClient nas, IPEndPoint remote,
                                      string username, string method, string reason)
    {
        Interlocked.Increment(ref _totalRejects);
        Log($"[AUTH] REJECT ({method}) — {username} from {remote.Address} — {reason}");
        FireAuthEvent(username, remote, method, false, reason);
        return Reject(req, nas.SharedSecret, reason);
    }

    private RadiusPacket Reject(RadiusPacket req, string secret, string reason)
    {
        var reject = new RadiusPacket { Code = RadiusCode.AccessReject, Identifier = req.Identifier };
        reject.Attributes.Add(new(RadiusAttributeType.ReplyMessage, reason));
        reject.SetResponseAuthenticator(req.Authenticator, secret);
        return reject;
    }

    private void FireAuthEvent(string username, IPEndPoint remote, string method, bool accepted, string reason)
        => OnAuthEvent?.Invoke(this, new AuthEventArgs
        {
            Username = username, NasIp = remote.Address.ToString(),
            Method = method, Accepted = accepted, Reason = reason,
        });

    // ── VSA helpers ───────────────────────────────────────────────────────────
    private static bool HasMsChapV2Response(RadiusPacket pkt) => GetMsChap2Response(pkt) != null;
    private static byte[]? GetMsChapChallenge(RadiusPacket pkt) => GetMicrosoftVsa(pkt, 11);
    private static byte[]? GetMsChap2Response(RadiusPacket pkt) => GetMicrosoftVsa(pkt, 25);

    private static byte[]? GetMicrosoftVsa(RadiusPacket pkt, byte vsaType)
    {
        foreach (var attr in pkt.GetAttributes(RadiusAttributeType.VendorSpecific))
        {
            var v = attr.Value;
            if (v.Length < 6) continue;
            uint vendorId = (uint)((v[0] << 24) | (v[1] << 16) | (v[2] << 8) | v[3]);
            if (vendorId != 311 || v[4] != vsaType) continue;
            int vsaLen = v[5] - 2;
            if (vsaLen <= 0 || 6 + vsaLen > v.Length) continue;
            return v[6..(6 + vsaLen)];
        }
        return null;
    }

    private static RadiusAttribute BuildMicrosoftVsa(byte vsaType, byte[] value)
    {
        var data = new byte[4 + 1 + 1 + value.Length];
        data[0] = 0; data[1] = 0; data[2] = 0x01; data[3] = 0x37;
        data[4] = vsaType;
        data[5] = (byte)(2 + value.Length);
        Buffer.BlockCopy(value, 0, data, 6, value.Length);
        return new RadiusAttribute(RadiusAttributeType.VendorSpecific, data);
    }

    // ── Logging ───────────────────────────────────────────────────────────────
    private void Log(string message)
    {
        _logger.Info(message);
        OnLog?.Invoke(this, $"[{DateTime.Now:HH:mm:ss.fff}] {message}");
    }

    public void Dispose()
    {
        Stop();
        _cts?.Dispose();
        _authSocket?.Dispose();
        _acctSocket?.Dispose();
    }
}

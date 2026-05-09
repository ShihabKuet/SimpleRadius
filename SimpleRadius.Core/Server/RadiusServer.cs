using System.Net;
using System.Net.Sockets;
using SimpleRadius.Core.Models;
using SimpleRadius.Core.Protocol;
using SimpleRadius.Core.Storage;

namespace SimpleRadius.Core.Server;

// ── Events ────────────────────────────────────────────────────────────────────
public sealed class AuthEventArgs : EventArgs
{
    public string     Username   { get; init; } = "";
    public string     NasIp      { get; init; } = "";
    public string     Method     { get; init; } = "";
    public bool       Accepted   { get; init; }
    public string     Reason     { get; init; } = "";
    public DateTime   Timestamp  { get; init; } = DateTime.Now;
}

// ── Server configuration ──────────────────────────────────────────────────────
public sealed class RadiusServerConfig
{
    public int    AuthPort    { get; set; } = 1812;
    public int    AcctPort    { get; set; } = 1813;
    public string BindAddress { get; set; } = "0.0.0.0";
    public string DataDir     { get; set; } = "data";
}

// ── Main RADIUS server ────────────────────────────────────────────────────────
/// <summary>
/// UDP-based RADIUS authentication and accounting server.
/// Phase 1: PAP authentication against a local user store.
/// </summary>
public sealed class RadiusServer : IDisposable
{
    private readonly RadiusServerConfig _config;
    private readonly UserStore          _users;
    private readonly NasStore           _nas;
    private readonly IRadiusLogger      _logger;

    private UdpClient?        _authSocket;
    private UdpClient?        _acctSocket;
    private CancellationTokenSource? _cts;
    private Task?             _authTask;
    private Task?             _acctTask;

    // ── Statistics (thread-safe via Interlocked) ──────────────────────────────
    private long _totalRequests;
    private long _totalAccepts;
    private long _totalRejects;
    private long _totalAccounting;
    private readonly DateTime _startTime = DateTime.Now;

    public long TotalRequests   => Interlocked.Read(ref _totalRequests);
    public long TotalAccepts    => Interlocked.Read(ref _totalAccepts);
    public long TotalRejects    => Interlocked.Read(ref _totalRejects);
    public long TotalAccounting => Interlocked.Read(ref _totalAccounting);
    public TimeSpan Uptime      => IsRunning ? DateTime.Now - _startTime : TimeSpan.Zero;
    public bool     IsRunning   { get; private set; }

    // ── Events (the GUI subscribes to these) ──────────────────────────────────
    public event EventHandler<AuthEventArgs>? OnAuthEvent;
    public event EventHandler<string>?        OnLog;

    // ── Constructor ───────────────────────────────────────────────────────────
    public RadiusServer(RadiusServerConfig config, IRadiusLogger? logger = null)
    {
        _config = config;
        _logger = logger ?? new ConsoleRadiusLogger();

        Directory.CreateDirectory(config.DataDir);
        _users = new UserStore(Path.Combine(config.DataDir, "users.json"));
        _nas   = new NasStore(Path.Combine(config.DataDir,  "nas.json"));
    }

    // ── Public store accessors ────────────────────────────────────────────────
    public UserStore Users => _users;
    public NasStore  Nas   => _nas;

    // ── Start / Stop ──────────────────────────────────────────────────────────
    public void Start()
    {
        if (IsRunning) return;

        var bindIp = IPAddress.Parse(_config.BindAddress == "0.0.0.0"
            ? "0.0.0.0" : _config.BindAddress);

        _authSocket = new UdpClient(new IPEndPoint(bindIp, _config.AuthPort));
        _acctSocket = new UdpClient(new IPEndPoint(bindIp, _config.AcctPort));
        _cts        = new CancellationTokenSource();

        _authTask = Task.Run(() => ListenLoop(_authSocket, "AUTH", _cts.Token));
        _acctTask = Task.Run(() => ListenLoop(_acctSocket, "ACCT", _cts.Token));

        IsRunning = true;
        Log($"Simple Radius started — Auth:{_config.AuthPort} Acct:{_config.AcctPort}");
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

    // ── UDP receive loop ──────────────────────────────────────────────────────
    private async Task ListenLoop(UdpClient socket, string label, CancellationToken ct)
    {
        Log($"[{label}] Listener started on port {((IPEndPoint)socket.Client.LocalEndPoint!).Port}");

        while (!ct.IsCancellationRequested)
        {
            try
            {
                var result = await socket.ReceiveAsync(ct);
                // Handle each packet on the thread pool (don't await)
                _ = Task.Run(() => HandlePacket(result.Buffer, result.RemoteEndPoint, socket), ct);
            }
            catch (OperationCanceledException) { break; }
            catch (SocketException) when (ct.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                Log($"[{label}] Receive error: {ex.Message}");
            }
        }

        Log($"[{label}] Listener stopped.");
    }

    // ── Packet dispatcher ─────────────────────────────────────────────────────
    private async Task HandlePacket(byte[] data, IPEndPoint remote, UdpClient socket)
    {
        Interlocked.Increment(ref _totalRequests);

        var pkt = RadiusPacket.Parse(data);
        if (pkt == null)
        {
            Log($"[WARN] Malformed packet from {remote.Address} — discarded");
            return;
        }

        // Look up NAS entry by source IP
        var nas = _nas.FindByIp(remote.Address);
        if (nas == null)
        {
            Log($"[WARN] Packet from unknown NAS {remote.Address} — discarded");
            return;
        }

        Log($"[RECV] {pkt.Code} from {remote.Address} ({nas.Name}) Id={pkt.Identifier} User={pkt.UserName ?? "(none)"}");

        RadiusPacket? response = pkt.Code switch
        {
            RadiusCode.AccessRequest     => HandleAccessRequest(pkt, nas, remote),
            RadiusCode.AccountingRequest => HandleAccountingRequest(pkt, nas),
            _ => null
        };

        if (response != null)
        {
            var bytes = response.Encode();
            await socket.SendAsync(bytes, bytes.Length, remote);
            Log($"[SEND] {response.Code} to {remote.Address} Id={response.Identifier}");
        }
    }

    // ── Access-Request handler ────────────────────────────────────────────────
    private RadiusPacket HandleAccessRequest(RadiusPacket req, NasClient nas, IPEndPoint remote)
    {
        string sharedSecret = nas.SharedSecret;
        string? username    = req.UserName;

        if (string.IsNullOrEmpty(username))
            return Reject(req, sharedSecret, "No username provided");

        // Phase 1: PAP only
        var hasPassword = req.GetAttribute(RadiusAttributeType.UserPassword) != null;
        if (!hasPassword)
        {
            Log($"[AUTH] {username} — no PAP password attribute (EAP not yet supported in Phase 1)");
            return Reject(req, sharedSecret, "Auth method not supported");
        }

        string? password = req.DecryptPapPassword(sharedSecret, req.Authenticator);
        if (password == null)
            return Reject(req, sharedSecret, "Password decryption failed");

        var user = _users.Authenticate(username, password);
        if (user == null)
        {
            Interlocked.Increment(ref _totalRejects);
            Log($"[AUTH] REJECT — {username} from {remote.Address} — bad credentials or disabled");

            OnAuthEvent?.Invoke(this, new AuthEventArgs
            {
                Username  = username,
                NasIp     = remote.Address.ToString(),
                Method    = "PAP",
                Accepted  = false,
                Reason    = "Invalid credentials",
            });

            return Reject(req, sharedSecret, "Invalid credentials");
        }

        // ── Build Access-Accept ───────────────────────────────────────────────
        Interlocked.Increment(ref _totalAccepts);
        Log($"[AUTH] ACCEPT — {username} from {remote.Address} Group={user.Group} VLAN={user.VlanId}");

        OnAuthEvent?.Invoke(this, new AuthEventArgs
        {
            Username = username,
            NasIp    = remote.Address.ToString(),
            Method   = "PAP",
            Accepted = true,
            Reason   = $"Group={user.Group}",
        });

        var accept = new RadiusPacket
        {
            Code       = RadiusCode.AccessAccept,
            Identifier = req.Identifier,
        };

        accept.Attributes.Add(new(RadiusAttributeType.ReplyMessage, $"Welcome, {username}!"));

        // Attach VLAN assignment attributes if configured
        if (user.VlanId > 0)
        {
            // Tunnel-Type = VLAN (13)
            accept.Attributes.Add(new(RadiusAttributeType.TunnelType,         (uint)13));
            // Tunnel-Medium-Type = IEEE-802 (6)
            accept.Attributes.Add(new(RadiusAttributeType.TunnelMediumType,   (uint)6));
            // Tunnel-Private-Group-Id = VLAN ID as string
            accept.Attributes.Add(new(RadiusAttributeType.TunnelPrivateGroupId, user.VlanId.ToString()));
        }

        // Session timeout
        if (user.SessionTimeoutSeconds > 0)
            accept.Attributes.Add(new(RadiusAttributeType.SessionTimeout, (uint)user.SessionTimeoutSeconds));

        accept.SetResponseAuthenticator(req.Authenticator, sharedSecret);
        return accept;
    }

    // ── Accounting-Request handler ────────────────────────────────────────────
    private RadiusPacket HandleAccountingRequest(RadiusPacket req, NasClient nas)
    {
        Interlocked.Increment(ref _totalAccounting);

        var statusAttr = req.GetAttribute(RadiusAttributeType.AcctStatusType);
        string status  = statusAttr != null
            ? statusAttr.AsUInt32() switch { 1 => "Start", 2 => "Stop", 3 => "Interim", _ => "?" }
            : "Unknown";

        Log($"[ACCT] {status} — User={req.UserName ?? "(none)"} Session={req.GetAttribute(RadiusAttributeType.AcctSessionId)?.AsString() ?? "-"}");

        var response = new RadiusPacket
        {
            Code       = RadiusCode.AccountingResponse,
            Identifier = req.Identifier,
        };
        response.SetResponseAuthenticator(req.Authenticator, nas.SharedSecret);
        return response;
    }

    // ── Helpers ───────────────────────────────────────────────────────────────
    private RadiusPacket Reject(RadiusPacket req, string secret, string reason)
    {
        var reject = new RadiusPacket
        {
            Code       = RadiusCode.AccessReject,
            Identifier = req.Identifier,
        };
        reject.Attributes.Add(new(RadiusAttributeType.ReplyMessage, reason));
        reject.SetResponseAuthenticator(req.Authenticator, secret);
        return reject;
    }

    private void Log(string message)
    {
        _logger.Info(message);
        OnLog?.Invoke(this, $"[{DateTime.Now:HH:mm:ss.fff}] {message}");
    }

    // ── IDisposable ───────────────────────────────────────────────────────────
    public void Dispose()
    {
        Stop();
        _cts?.Dispose();
        _authSocket?.Dispose();
        _acctSocket?.Dispose();
    }
}

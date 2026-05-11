using SimpleRadius.Core;
using SimpleRadius.Core.Server;

namespace SimpleRadius.Service;

/// <summary>
/// IHostedService wrapper around RadiusServer.
/// Starts the RADIUS engine when the Windows Service starts,
/// stops it cleanly when the service is stopped.
///
/// Configuration is read from appsettings.json (see below).
/// Logs go to the Windows Event Log and optionally to a rolling file.
/// </summary>
public sealed class RadiusWorker : BackgroundService
{
    private readonly ILogger<RadiusWorker> _hostLogger;
    private readonly IConfiguration        _config;
    private RadiusServer?                  _server;

    public RadiusWorker(ILogger<RadiusWorker> logger, IConfiguration config)
    {
        _hostLogger = logger;
        _config     = config;
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var serverConfig = new RadiusServerConfig
        {
            AuthPort    = _config.GetValue<int>("SimpleRadius:AuthPort",    1812),
            AcctPort    = _config.GetValue<int>("SimpleRadius:AcctPort",    1813),
            BindAddress = _config.GetValue<string>("SimpleRadius:BindAddress", "0.0.0.0")!,
            DataDir     = _config.GetValue<string>("SimpleRadius:DataDir",
                              Path.Combine(AppContext.BaseDirectory, "data"))!,
        };

        // Bridge Core logs into the Windows Event Log / console
        var logger = new ServiceRadiusLogger(_hostLogger);

        _server = new RadiusServer(serverConfig, logger);
        _server.Start();

        _hostLogger.LogInformation(
            "Simple Radius service started — Auth:{Auth}  Acct:{Acct}  Data:{Dir}",
            serverConfig.AuthPort, serverConfig.AcctPort, serverConfig.DataDir);

        // Block until the host requests cancellation (service stop)
        stoppingToken.WaitHandle.WaitOne();
        return Task.CompletedTask;
    }

    public override Task StopAsync(CancellationToken cancellationToken)
    {
        _hostLogger.LogInformation("Simple Radius service stopping...");
        _server?.Stop();
        _server?.Dispose();
        return base.StopAsync(cancellationToken);
    }
}

/// <summary>
/// Bridges IRadiusLogger calls into Microsoft.Extensions.Logging
/// so they appear in the Windows Event Log.
/// </summary>
internal sealed class ServiceRadiusLogger : IRadiusLogger
{
    private readonly ILogger _logger;
    public ServiceRadiusLogger(ILogger logger) => _logger = logger;

    public void Info(string message)  => _logger.LogInformation("{Msg}", message);
    public void Warn(string message)  => _logger.LogWarning("{Msg}", message);
    public void Error(string message, Exception? ex = null)
        => _logger.LogError(ex, "{Msg}", message);
}

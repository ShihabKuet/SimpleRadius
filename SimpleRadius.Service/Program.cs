using SimpleRadius.Service;

// Build and run as a Windows Service (or console app when debugging)
var host = Host.CreateDefaultBuilder(args)
    .UseWindowsService(opts =>
    {
        opts.ServiceName = "SimpleRadius";
    })
    .ConfigureServices((ctx, services) =>
    {
        services.AddHostedService<RadiusWorker>();
    })
    .ConfigureLogging(logging =>
    {
        logging.ClearProviders();
        logging.AddEventLog(settings =>
        {
            settings.SourceName = "SimpleRadius";
        });
        logging.AddConsole();
    })
    .Build();

await host.RunAsync();

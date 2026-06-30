using redb.Tsak.Core.Extensions;
using redb.Tsak.Core.Pro.Extensions;
using redb.Tsak.Worker.Utils;
using redb.Tsak.Core.Monitoring;
using Serilog;

StartupBanner.Print();

var builder = Host.CreateDefaultBuilder(args)
    .UseSerilog((context, services, configuration) => configuration
        .ReadFrom.Configuration(context.Configuration)
        .Enrich.With(new redb.Tsak.Worker.Utils.MemoryUsageEnricher())
        .WriteTo.Sink(services.GetRequiredService<RingBufferSink>()))
    .ConfigureServices((context, services) =>
    {
        services.AddTsak(context.Configuration);
        services.AddTsakCluster(context.Configuration);
    });

await builder.Build().RunAsync();

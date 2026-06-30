using Serilog;
using redb.Tsak.Core.Extensions;
//#if (pro)
using redb.Tsak.Core.Pro.Extensions;
//#endif

var builder = Host.CreateDefaultBuilder(args)
    .UseSerilog((context, services, configuration) => configuration
        .ReadFrom.Configuration(context.Configuration))
    .ConfigureServices((context, services) =>
    {
        // Registers everything required by the Tsak runtime:
        //   storage, hot-reload, REST management API,
        //   scheduler, monitoring, security.
        services.AddTsak(context.Configuration);
//#if (pro)
        // Pro: cluster bootstrap (no-op when Tsak:Cluster:Enabled = false).
        services.AddTsakCluster(context.Configuration);
//#endif
    });

await builder.Build().RunAsync();

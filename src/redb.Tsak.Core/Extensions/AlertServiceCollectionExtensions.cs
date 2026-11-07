using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using redb.Tsak.Core.Monitoring.Alerts;

namespace redb.Tsak.Core.Extensions;

/// <summary>
/// Registers the watchdog alert-delivery pipeline: the config-bound options, the natively
/// supported channels (webhook, Telegram, email — all zero extra dependencies), and the
/// dispatcher. The generic broker <c>endpoint</c> channel is attached later by
/// <c>SystemContextBuilder</c>, once the _system route context exists.
/// </summary>
public static class AlertServiceCollectionExtensions
{
    public static IServiceCollection AddTsakAlerts(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);

        var options = new WatchdogAlertOptions();
        configuration.GetSection("Tsak:Watchdog:Alerts").Bind(options);
        services.TryAddSingleton(options);

        // One shared HttpClient for the webhook + Telegram channels — a short timeout so a slow
        // collector cannot stall the alert pump.
        services.TryAddSingleton<AlertHttpClient>(_ => new AlertHttpClient(
            new HttpClient { Timeout = TimeSpan.FromSeconds(10) }));

        services.TryAddSingleton<AlertDispatcher>(sp =>
        {
            var http = sp.GetRequiredService<AlertHttpClient>().Client;
            var channels = new IAlertChannel[]
            {
                new WebhookAlertChannel(options.Webhook, http),
                new TelegramAlertChannel(options.Telegram, http),
                new EmailAlertChannel(options.Email)
                // The endpoint (broker) channel is attached in SystemContextBuilder — it needs the
                // route context, and its target component is provided by the host, not Core.
            };
            return new AlertDispatcher(options, channels, sp.GetRequiredService<ILogger<AlertDispatcher>>());
        });

        return services;
    }
}

/// <summary>Wrapper so a single long-lived <see cref="HttpClient"/> can be a DI singleton.</summary>
public sealed class AlertHttpClient(HttpClient client)
{
    public HttpClient Client { get; } = client;
}

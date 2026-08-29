using Jellyfin.Plugin.Watchoffit.Commands;
using Jellyfin.Plugin.Watchoffit.Commands.Handlers;
using Jellyfin.Plugin.Watchoffit.Configuration;
using Jellyfin.Plugin.Watchoffit.Events;
using Jellyfin.Plugin.Watchoffit.Pairing;
using Jellyfin.Plugin.Watchoffit.Protocol.V1;

using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Watchoffit;

/// <summary>
/// Hooks the pairing service and the dashboard controller into
/// Jellyfin's DI container. Jellyfin calls
/// <see cref="RegisterServices"/> when the plugin is loaded; the
/// controller is then discovered via the standard ASP.NET Core
/// application-part mechanism.
/// </summary>
public sealed class WatchoffitPluginServiceRegistrator : IPluginServiceRegistrator
{
    /// <inheritdoc />
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        ArgumentNullException.ThrowIfNull(serviceCollection);
        ArgumentNullException.ThrowIfNull(applicationHost);

        // The connection store is rooted in the plugin's data folder
        // (BasePlugin<T>.DataFolderPath, set in the plugin's ctor).
        // We use a placeholder here; the real path is injected when
        // the WatchoffitPlugin instance is constructed. The service
        // collection registration below resolves it lazily so the
        // store is created on first use.
        serviceCollection.AddSingleton<ICredentialProtector>(_ => CredentialProtectorFactory.CreateForCurrentPlatform());
        serviceCollection.AddSingleton<V1EnvelopeBuilder>();

        // SystemInfo needs the IApplicationHost; register a factory
        // that captures it from the host parameter.
        serviceCollection.AddSingleton<IJellyfinSystemInfoProvider>(_ =>
            new LiveJellyfinSystemInfoProvider(applicationHost));

        // The connection store is constructed lazily so the plugin
        // data folder is known by then. The plugin instance sets the
        // folder path on WatchoffitConnectionStore via a setter after
        // Jellyfin has computed it.
        serviceCollection.AddSingleton<WatchoffitConnectionStore>(sp =>
        {
            var plugin = WatchoffitPlugin.Instance;
            if (plugin is null)
            {
                throw new InvalidOperationException("Watchoffit plugin instance is not yet available");
            }

            var dataFolder = plugin.DataFolderPath;
            return new WatchoffitConnectionStore(
                dataFolder,
                sp.GetRequiredService<ICredentialProtector>(),
                sp.GetRequiredService<ILogger<WatchoffitConnectionStore>>());
        });

        serviceCollection.AddSingleton<WatchoffitClient>(sp =>
        {
            // Pairing HTTP traffic uses a per-instance HttpClient so
            // connection pooling is scoped to the plugin's lifetime
            // and not the DI container's. The 10-second timeout mirrors
            // the design's §3.7 wire trace.
            var http = new HttpClient
            {
                BaseAddress = new Uri("http://localhost/"),
                Timeout = TimeSpan.FromSeconds(10),
            };
            return new WatchoffitClient(
                http,
                sp.GetRequiredService<V1EnvelopeBuilder>(),
                sp.GetRequiredService<IJellyfinSystemInfoProvider>(),
                sp.GetRequiredService<ILogger<WatchoffitClient>>());
        });

        serviceCollection.AddSingleton<PairingService>();
        // Hosted services start in registration order. Rehydrate the durable
        // pairing before any queue or event subscription can observe it.
        serviceCollection.AddHostedService<PairingStartupService>();

        // Phase 5 — durable event outbox. The forwarder only persists event
        // envelopes; the background worker is the sole owner of network I/O.
        serviceCollection.AddSingleton<DurableEventOutbox>(sp =>
        {
            var plugin = WatchoffitPlugin.Instance
                ?? throw new InvalidOperationException("Watchoffit plugin instance is not yet available");
            return new DurableEventOutbox(
                plugin.DataFolderPath,
                sp.GetRequiredService<ILogger<DurableEventOutbox>>())
            {
            };
        });
        serviceCollection.AddSingleton<IEventOutboxSender, WatchoffitEventOutboxSender>();
        serviceCollection.AddHostedService<EventOutboxWorker>();
        serviceCollection.AddHostedService<InventoryPublisher>();

        // Subscribes to Jellyfin's event bus after the worker has been
        // registered, and tears subscriptions down at application stop.
        serviceCollection.AddSingleton<EventForwarder>();
        serviceCollection.AddHostedService<EventForwarderHostedService>();

        // Phase 6 — outbound command channel. The plugin long-polls
        // Watchoffit for leased commands and acks the result. The
        // `PairingStartupService` runs first, so the polling service
        // sees the rehydrated credential from the very first tick.
        serviceCollection.AddSingleton<ICommandCausationContext, CommandCausationContext>();
        serviceCollection.AddSingleton<ICommandHandler, PingCommandHandler>();
        serviceCollection.AddSingleton<ICommandHandler, MarkPlayedCommandHandler>();
        serviceCollection.AddSingleton<ICommandHandler, MarkUnplayedCommandHandler>();
        serviceCollection.AddSingleton<ICommandHandler, ReconcileRequestCommandHandler>();
        serviceCollection.AddSingleton<ICommandHandler, BackfillRequestCommandHandler>();
        serviceCollection.AddSingleton<ICommandHandlerRegistry, CommandHandlerRegistry>();
        serviceCollection.AddHostedService<CommandPollingService>();

        // Make the PairingController discoverable as an MVC controller.
        serviceCollection.AddControllers().AddApplicationPart(typeof(PairingController).Assembly);
    }
}

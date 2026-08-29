using System;
using System.Collections.Generic;
using System.Globalization;
using Jellyfin.Plugin.Watchoffit.Configuration;
using Jellyfin.Plugin.Watchoffit.Pairing;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Watchoffit;

/// <summary>
/// The main Watchoffit plugin entry point.
/// </summary>
public class WatchoffitPlugin : BasePlugin<WatchoffitPluginConfiguration>, IHasWebPages
{
    /// <summary>
    /// Initializes a new instance of the <see cref="WatchoffitPlugin"/> class.
    /// </summary>
    /// <param name="applicationPaths">Instance of the <see cref="IApplicationPaths"/> interface.</param>
    /// <param name="xmlSerializer">Instance of the <see cref="IXmlSerializer"/> interface.</param>
    /// <param name="loggerFactory">Jellyfin's logger factory; passed through to the pairing service so plugin-scoped logs have a stable category.</param>
    /// <param name="serviceProvider">
    /// Jellyfin's DI container, used to resolve <see cref="Pairing.PairingService"/>
    /// for the best-effort rehydration below. The service is resolved
    /// lazily because <see cref="WatchoffitConnectionStore"/> depends on
    /// <see cref="Instance"/> (this plugin's <c>DataFolderPath</c>), and
    /// the factory that creates the store requires <see cref="Instance"/>
    /// to be set first.
    /// </param>
    public WatchoffitPlugin(
        IApplicationPaths applicationPaths,
        IXmlSerializer xmlSerializer,
        ILoggerFactory loggerFactory,
        IServiceProvider serviceProvider)
        : base(applicationPaths, xmlSerializer)
    {
        Instance = this;
        LoggerFactory = loggerFactory;

        // Best-effort rehydration: if a connection.json from a previous
        // run is present in the plugin data folder, transition the
        // PairingService to its persisted state BEFORE the event
        // forwarder / outbox worker hosted services start. The
        // EventForwarderHostedService and EventOutboxWorker run as
        // IHostedService; they read PairingService.CurrentConnection
        // to decide whether they have a credential to send with, so
        // any startup work that touches the network must observe the
        // rehydrated state.
        //
        // We swallow exceptions: a rehydration failure means the
        // operator must re-pair, which is recoverable. Throwing here
        // would prevent the plugin from loading and turn a recoverable
        // situation into a hard crash.
        try
        {
            var pairingService = serviceProvider.GetService(typeof(PairingService)) as PairingService;
            pairingService?.LoadFromStore();
        }
        catch (Exception ex)
        {
            loggerFactory.CreateLogger<WatchoffitPlugin>()
                .LogWarning(ex, "Rehydration from connection.json failed; plugin will start in None state");
        }
    }

    /// <inheritdoc />
    public override string Name => "Watchoffit";

    /// <inheritdoc />
    public override Guid Id => Guid.Parse("ed8e9c41-2e0f-5872-93f2-06feb1bc37d1");

    /// <summary>Jellyfin's logger factory, captured at construction so the pairing service can resolve category loggers.</summary>
    public ILoggerFactory LoggerFactory { get; }

    /// <summary>
    /// Gets the current plugin instance. Used by
    /// <see cref="WatchoffitPluginServiceRegistrator"/> to resolve the
    /// plugin's data folder lazily when constructing the connection
    /// store.
    /// </summary>
    public static WatchoffitPlugin? Instance { get; private set; }

    /// <inheritdoc />
    public IEnumerable<PluginPageInfo> GetPages()
    {
        return
        [
            new PluginPageInfo
            {
                Name = Name,
                EmbeddedResourcePath = string.Format(CultureInfo.InvariantCulture, "{0}.Configuration.watchoffitConfigPage.html", GetType().Namespace)
            }
        ];
    }
}

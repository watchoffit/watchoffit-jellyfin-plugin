using System.Net.Mime;
using System.Text;
using System.Text.Json;

using Jellyfin.Plugin.Watchoffit.Pairing;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Watchoffit.Configuration;

/// <summary>
/// HTTP controller that handles the pairing form post from the
/// dashboard page. Two endpoints:
///   - <c>POST /Plugins/Watchoffit/Pairing/Connect</c> — accepts one
///     short-lived Watchoffit connection string, exchanges it for a
///     durable credential, and returns a JSON status payload
///     the dashboard can render in place.
///   - <c>POST /Plugins/Watchoffit/Pairing/Disconnect</c> — drops the
///     local state and best-effort revokes the remote credential.
/// </summary>
/// <remarks>
/// The controller is intentionally minimal: it owns no state, holds
/// no connection cache, and reads <see cref="PairingService"/> from
/// the Jellyfin DI container. The endpoints never throw — transport
/// and protocol failures are returned as a 200 with an
/// <c>errorCode</c> field so the dashboard can render the inline
/// error without a page reload.
/// </remarks>
[ApiController]
[Authorize(Policy = "RequiresElevation")]
[Route("Plugins/Watchoffit/Pairing")]
public sealed class PairingController : ControllerBase
{
    private readonly PairingService _pairingService;
    private readonly ILogger<PairingController> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="PairingController"/> class.
    /// </summary>
    /// <param name="pairingService">Pairing service injected by the DI container; holds the state machine.</param>
    /// <param name="logger">Plugin logger.</param>
    public PairingController(PairingService pairingService, ILogger<PairingController> logger)
    {
        _pairingService = pairingService;
        _logger = logger;
    }

    /// <summary>Returns the current pairing state as JSON. Used by the dashboard page to refresh the view without reloading.</summary>
    /// <returns>State, connection summary, and the last error code (or null).</returns>
    [HttpGet("Status")]
    public IActionResult Status()
    {
        var connection = _pairingService.CurrentConnection;
        return Ok(new
        {
            state = _pairingService.CurrentState.ToString().ToLowerInvariant(),
            baseUrl = connection?.BaseUrl,
            serverConnectionId = connection?.ServerConnectionId,
            watchoffitServerName = connection?.WatchoffitServerName,
            credentialMasked = connection?.DisplayCredentialMasked(),
            lastPingAt = string.IsNullOrEmpty(connection?.LastPingAt) ? null : connection.LastPingAt,
            createdAt = string.IsNullOrEmpty(connection?.CreatedAt) ? null : connection.CreatedAt,
        });
    }

    /// <summary>Exchanges one connection string for a persisted paired connection.</summary>
    /// <param name="connectionString">Short-lived pairing bundle copied from Watchoffit.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>200 with the new state on success, 200 with an error code on failure.</returns>
    [HttpPost("Connect")]
    public async Task<IActionResult> Connect([FromForm] string connectionString, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            return Ok(new { errorCode = "INVALID_INPUT", errorMessage = "connection string is required" });
        }

        var result = await _pairingService.ConnectAsync(connectionString, cancellationToken)
            .ConfigureAwait(false);
        if (result.NewState != PairingState.Paired)
        {
            return Ok(new { errorCode = result.ErrorCode, errorMessage = result.ErrorMessage });
        }

        return Ok(new
        {
            state = "paired",
            serverConnectionId = result.Connection?.ServerConnectionId,
            watchoffitServerName = result.Connection?.WatchoffitServerName,
        });
    }

    /// <summary>Drops the local state and best-effort revokes the remote credential.</summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>200 with the new state.</returns>
    [HttpPost("Disconnect")]
    public async Task<IActionResult> Disconnect(CancellationToken cancellationToken)
    {
        var result = await _pairingService.DisconnectAsync(cancellationToken).ConfigureAwait(false);
        return Ok(new { state = result.NewState.ToString().ToLowerInvariant() });
    }
}

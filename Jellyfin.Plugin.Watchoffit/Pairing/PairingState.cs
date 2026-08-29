namespace Jellyfin.Plugin.Watchoffit.Pairing;

/// <summary>
/// State machine for the v1 pairing flow. Mirrors the diagram in
/// <c>docs/pairing-design.md</c> §8.
/// </summary>
/// <remarks>
/// <see cref="Revoked"/> is terminal for the credential on the Watchoffit
/// side. The plugin can only move from <see cref="Revoked"/> back to
/// <see cref="None"/> by forgetting local state and pairing again. The
/// plugin is the single source of truth for the local state; the
/// <c>state</c> field in <c>connection.json</c> mirrors this enum so
/// an operator inspecting the file sees the same value the runtime
/// logic uses.
/// </remarks>
public enum PairingState
{
    /// <summary>No <c>connection.json</c> exists, or it carries an unknown <c>version</c>.</summary>
    None,

    /// <summary>Sent <c>challenge_request</c>; waiting on the Watchoffit challenge ack.</summary>
    Challenge,

    /// <summary>Sent <c>redeem_request</c>; waiting on the credential from Watchoffit.</summary>
    Handshake,

    /// <summary>Credential persisted. <c>ping</c> / outbound commands are allowed.</summary>
    Paired,

    /// <summary>Sent <c>rotate_credential</c>; waiting on the new credential from Watchoffit.</summary>
    Rotating,

    /// <summary>Remote credential was revoked or rejected. Plugin stops sending authenticated traffic.</summary>
    Revoked,
}

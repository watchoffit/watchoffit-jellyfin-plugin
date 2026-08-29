using System.Text.Json;
using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.Watchoffit.Protocol.V1.Payloads.Acks;

/// <summary>
/// One leased command inside a <see cref="V1CommandPollAckPayload.Commands"/>
/// array. Mirrors the per-command entry in
/// <c>v1CommandPollAckPayloadSchema</c> in
/// <c>packages/core/src/integrations/watchoffit-plugin-protocol/v1.ts</c>.
/// </summary>
/// <remarks>
/// <see cref="Payload"/> is held as a <see cref="JsonElement"/> because the
/// command body is opaque at the wire level — the plugin narrows it to a
/// specific C# shape inside the per-<c>commandKind</c> handler. Keeping the
/// raw element here avoids one round-trip through the C# type system for
/// command kinds the plugin does not yet handle.
/// </remarks>
public sealed record V1LeasedCommand
{
    /// <summary>Server-issued id of the leased row (<c>cmd_&lt;uuidv7&gt;</c>). The plugin echoes this back in the ack.</summary>
    [JsonPropertyName("commandId")]
    public required string CommandId { get; init; }

    /// <summary>One of <c>mark_played</c>, <c>mark_unplayed</c>, <c>ping</c>, <c>reconcile_request</c>, <c>rotate_credential</c>.</summary>
    [JsonPropertyName("commandKind")]
    public required string CommandKind { get; init; }

    /// <summary>Opaque per-kind body. Preserved as a JSON value; the handler narrows it.</summary>
    [JsonPropertyName("payload")]
    public required JsonElement Payload { get; init; }

    /// <summary>Unix seconds when this lease expires. The plugin MUST re-poll before this timestamp or the server reclaims the row.</summary>
    [JsonPropertyName("leaseUntil")]
    public required long LeaseUntil { get; init; }

    /// <summary>Opaque per-lease token (<c>att_&lt;uuidv7&gt;</c>) the plugin echoes in the ack envelope's <c>header.id</c>.</summary>
    [JsonPropertyName("attemptToken")]
    public required string AttemptToken { get; init; }
}

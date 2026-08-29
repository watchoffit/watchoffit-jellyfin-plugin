using System.Text.Json;
using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.Watchoffit.Protocol.V1.Payloads;

/// <summary>
/// Error payload for an <c>error</c> envelope. <see cref="Code"/> is an
/// application-level code (see <see cref="ApplicationErrorCode"/>) capped
/// at 64 chars; <see cref="CommandId"/> is optional because some errors are
/// unsolicited.
/// </summary>
public sealed record V1ErrorPayload
{
    /// <summary>
    /// Application-level error code. Parser and transport errors use
    /// <see cref="SafeErrorCode"/> and are returned by
    /// <see cref="V1EnvelopeParser"/> only — they never appear here.
    /// </summary>
    [JsonPropertyName("code")]
    public required string Code { get; init; }

    /// <summary>For logs and diagnostics only. Never surfaced to end users.</summary>
    [JsonPropertyName("message")]
    public required string Message { get; init; }

    /// <summary>Optional id of the command being answered; absent for unsolicited errors.</summary>
    [JsonPropertyName("commandId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? CommandId { get; init; }
}

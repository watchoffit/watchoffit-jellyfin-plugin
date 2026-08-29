# Watchoffit ↔ Jellyfin Plugin — Protocol v1

> **Status:** frozen for v1, implementation in progress.
> **Audience:** the public Jellyfin plugin repository (C#) and the Watchoffit core (TypeScript).
> **Goal:** replace the legacy Webhook plugin / Generic Destination / Quick Connect
> bridge with a single pairing flow, a bidirectional durable channel, and a
> shared wire contract.

This document is the contract. The C# plugin and the Watchoffit core MUST validate
every inbound and outbound message against the schemas in section
[Command and event schemas](#4-command-and-event-schemas). The same JSON
acceptance fixtures in section
[JSON acceptance fixtures](#7-json-acceptance-fixtures) are checked into both
codebases and matched byte-for-byte in CI.

> [!NOTE]
> Versioning rule. The wire format described here is `v1`. The envelope
> carries a `version` integer. Bumping it is a breaking change that requires
> a new RFC document and a new major in the plugin manifest. Backwards-
> compatible additions (new optional fields, new `kind` values inside an
> existing discriminated union) require only a minor bump in the protocol
> version constants and an addition to this document.

## 1. Protocol version 1 message envelopes

### 1.1 Envelope shape

Every message exchanged between the Watchoffit core and the Jellyfin plugin is a
single JSON object with a fixed top-level shape:

```jsonc
{
  "kind": "command | event | ack | error",
  "header": { /* see 1.2 */ },
  "payload": { /* see section 4 — discriminated by header.kind */ }
}
```

The envelope root is **always** a JSON object (never an array, never a
primitive). The `kind` field on the envelope is the router discriminator and
MUST match the `kind` on `header` — `envelope.kind === header.kind` is a
hard invariant. The TypeScript skeleton enforces it by giving each envelope
kind its own header schema with `kind` pinned to a literal, plus a
`superRefine` check on the discriminated union (see
[v1.ts](../../packages/core/src/integrations/watchoffit-plugin-protocol/v1.ts)).
Receivers that observe a mismatch MUST respond with
`SafeErrorCode.INVALID_ENVELOPE` and MUST NOT apply the payload.

### 1.2 Header fields

| Field | Type | Required | Description |
| --- | --- | --- | --- |
| `version` | integer literal `1` | yes | Wire-format version. Receivers MUST refuse any other value with `PROTOCOL_VERSION_UNSUPPORTED`. |
| `kind` | `"command" \| "event" \| "ack" \| "error"` | yes | High-level envelope kind. Determines which `payload` schema applies. |
| `id` | string (1–128 chars) | yes | Unique per logical message. Reused across retries of the same logical message. Used for at-least-once deduplication. |
| `correlationId` | string (1–128 chars) | conditional | For a `command`, the id of the envelope that triggered the work. For an `ack` or `error`, the id of the command being answered. Absent for unsolicited `event` envelopes. |
| `sequence` | non-negative integer | yes | Monotonic per-sender counter. Used to detect lost messages (gaps larger than 1 between consecutive envelopes from the same sender are dropped or surface as a gap-detected error). |
| `timestamp` | ISO 8601 UTC string ending in `Z` | yes | Time the envelope was assembled. Used for clock-drift detection and for ordering when `sequence` is not yet meaningful. **MUST end in `Z`**; offsets such as `+00:00` or `+0100` are rejected. |
| `serverConnectionId` | string (1–128 chars) | yes | The Watchoffit-side identifier of the connection this envelope belongs to. Used to refuse envelopes from a previous pair after credential rotation. |
| `capabilities` | object (see 1.3) | optional | Sender's declared capabilities. Commands always include it; events may omit it after the first successful pairing. |

### 1.3 Capabilities

```jsonc
{
  "minProtocolVersion": 1,
  "maxProtocolVersion": 1,
  "maxPayloadBytes": 65536,
  "maxBatchSize": 50
}
```

| Field | Type | Range | Description |
| --- | --- | --- | --- |
| `minProtocolVersion` | integer | `1..version` | Lowest protocol version this side still understands. |
| `maxProtocolVersion` | integer | `1..version` | Highest protocol version this side speaks. Used during negotiation. |
| `maxPayloadBytes` | integer | `1024..65536` | Max bytes the sender will put into a single payload. Defaults to `65536`. |
| `maxBatchSize` | integer | `1..50` | Max events batched in one envelope. Defaults to `50`. |

### 1.4 Signatures, acks, ids

- **Signature.** v1 does not sign individual envelopes. Authenticity is
  enforced at the transport layer (WebSocket `Authorization` header and the
  `Sec-WebSocket-Protocol` channel id; HTTPS long-poll via a session token).
  The envelope does not carry a signature because replay is handled by the
  `id` and by transport-level encryption (TLS).
- **Ack id.** Every `command` MUST eventually be answered with exactly one
  `ack` OR one `error` envelope whose `payload.commandId` equals the
  `command.id` and whose `header.correlationId` is also set to that value.
  The ack/error MUST be sent before the plugin reports the command as
  delivered in its durable queue; see
  [ACK / replay / deduplication rules](#3-ack--replay--deduplication-rules).
- **Timestamp.** ISO 8601 UTC with millisecond precision and a trailing `Z`.
  Example: `2026-08-26T20:34:41.000Z`. **MUST end in `Z`**; local times
  and offsets (`+00:00`, `+0100`, …) are rejected with
  `INVALID_ENVELOPE`. See §8.4 for the C# / TypeScript serialization
  contract.

### 1.5 Example skeleton (fields only)

```jsonc
{
  "kind": "command",
  "header": {
    "version": 1,
    "kind": "command",
    "id": "cmd_01HZ",
    "sequence": 1,
    "timestamp": "2026-08-26T20:34:41.000Z",
    "serverConnectionId": "scn_01HZ",
    "capabilities": {
      "minProtocolVersion": 1,
      "maxProtocolVersion": 1,
      "maxPayloadBytes": 65536,
      "maxBatchSize": 50
    }
  },
  "payload": {
    /* command-specific body, see section 4 */
  }
}
```

## 2. Pairing and credential lifecycle

### 2.1 States

A `serverConnectionId` is the unit of state for the channel between Watchoffit
and one Jellyfin server. The full state machine:

```
                    ┌──────────────┐
                    │   (none)     │
                    └──────┬───────┘
                           │ admin clicks "Connect Jellyfin" in Watchoffit
                           ▼
                    ┌──────────────┐
                    │  challenge   │  Watchoffit mints a one-time pairing code
                    │  issued      │  (10 min TTL, single use)
                    └──────┬───────┘
                           │ admin enters code + Watchoffit URL on the plugin page
                           ▼
                    ┌──────────────┐
                    │  handshake   │  plugin POSTs code to /pair/redeem
                    │  in_flight   │  Watchoffit returns credential + serverInfo
                    └──────┬───────┘
                           │ ack received
                           ▼
                    ┌──────────────┐
                    │  paired      │  bidirectional channel is open
                    └──────┬───────┘
                           │ admin initiates rotate OR revoke
                           ▼
                    ┌──────────────┐
                    │  rotating    │  new credential issued, old one valid
                    │              │  for a grace period (24h)
                    └──────┬───────┘
                           │ grace period elapsed
                           ▼
                    ┌──────────────┐
                    │  paired      │  (with new credential)
                    └──────┬───────┘
                           │ admin clicks "Disconnect"
                           ▼
                    ┌──────────────┐
                    │  revoked     │  old credential rejected
                    └──────────────┘
```

### 2.2 Handshake sequence

The handshake is initiated by the Watchoffit admin UI and completed by the
plugin. The wire format below lives **outside** the v1 envelope; it uses the
same JSON conventions but travels over HTTPS POST so it can traverse captive
portals and NAT without an open outbound WebSocket. The envelope format
takes over only after the credential is returned.

1. **Challenge issued** — Watchoffit mints a pairing code.
   ```jsonc
   // Watchoffit admin UI displays:
   { "pairingCode": "ABCD-EFGH-JKLM-NPQR", "expiresAt": "2026-08-26T20:44:41.000Z" }
   ```
2. **Plugin redeems** — plugin POSTs to `https://<watchoffit>/v1/plugin/pair/redeem`.
   ```jsonc
   // request
   { "pairingCode": "ABCD-EFGH-JKLM-NPQR" }
   ```
3. **Watchoffit acknowledges** — Watchoffit returns the credential and a
   `serverInfo` block. The plugin stores both, scoped to its Jellyfin
   server id.
   ```jsonc
   // response
   {
     "credential": "cred_01HZ...",
     "serverConnectionId": "scn_01HZ...",
     "serverInfo": {
       "watchoffitBaseUrl": "https://watchoffit.example.com",
       "minProtocolVersion": 1,
       "maxProtocolVersion": 1
     }
   }
   ```
4. **Channel opens** — the plugin opens the WebSocket channel using the
   `Authorization: Bearer <credential>` header. The first envelope on the
   channel is an `event` of `kind: "heartbeat"` so Watchoffit can measure RTT
   and finalize the pair.

### 2.3 Credential lifecycle

- **Storage.** The credential is encrypted by Watchoffit before being written
  to the plugin's private configuration. The plugin never writes it to
  logs. The Watchoffit side stores only a salted hash.
- **Scope.** A credential is bound to one `serverConnectionId` and one
  Jellyfin `serverId`. It cannot be replayed against a different Watchoffit
  installation.
- **Rotation.** The admin triggers rotation from the Watchoffit UI. Watchoffit sends
  a `command` of `kind: "rotate_credential"` (see §4.1.5) on the existing
  channel. The new credential is delivered as the `ack` payload of that
  command — `status: "ok"`, `commandId` echoes the rotate command, plus
  `newCredential` and `rotatedAt`. The old credential remains valid for a
  24-hour grace period; both are accepted during that window.
- **Revocation.** Immediate. After revocation, the next envelope from the
  old credential receives an `error` of `code: "AUTH_REQUIRED"` and the
  channel is closed. The plugin's UI flips to "Disconnected" and surfaces a
  re-pair action.
- **Uninstall.** Disabling or uninstalling the plugin MUST scrub the
  stored credential. Disabling MUST also revoke the active credential on
  the Watchoffit side. No valid credential is left behind.

## 3. ACK / replay / deduplication rules

### 3.1 Delivery semantics

- **At-least-once.** Every command and every event is delivered until the
  receiver acknowledges it. The sender keeps the message in a bounded
  durable queue until the ack arrives.
- **Exactly-once application.** The receiver deduplicates by `header.id`.
  A retried envelope with the same `id` and the same `header.sequence` is
  treated as a duplicate and answered with the cached `ack` or `error` if
  the receiver still remembers it; otherwise the receiver re-processes the
  message idempotently.

### 3.2 Deduplication

- **Window.** The receiver keeps an LRU of recent `id`s for at least
  `24 hours`. Anything older is treated as a new message and may be
  re-applied; the receiving side MUST therefore be idempotent.
- **Key.** `(serverConnectionId, header.id)` is the dedup key. Two
  different connections may legitimately reuse the same id.
- **Sequence gap.** When the receiver observes a gap in `header.sequence`
  (i.e. the next envelope arrives with `sequence > previous.sequence + 1`),
  it MUST emit a `diagnostic` event in its logs and either request a
  `reconcile_request` command or wait for a `heartbeat` that confirms the
  gap is real. It MUST NOT silently drop the missing envelopes.

### 3.3 What counts as "delivered"

A command is **delivered** to the plugin when:

1. The plugin has persisted the envelope to its durable queue, AND
2. The plugin has sent an `ack` of `status: "ok"` OR `status: "noop"`,
   AND
3. The plugin has applied the side effect (or decided that no side effect
   was required, hence `noop`).

A command is **delivered** to Watchoffit when:

1. Watchoffit has applied the inbound event to its tracking state, AND
2. Watchoffit has written the dedup record.

### 3.4 Retry, backoff, and the dead-letter state

- **Retry.** The sender retries on transport errors and on
  `error` responses whose `code` is not terminal (see
  [Safe error codes](#6-safe-error-codes)).
- **Backoff.** Exponential with jitter, capped at 5 minutes between
  attempts. `Retry-After` (when present on the underlying transport) wins.
- **Dead-letter.** After 10 failed attempts within a 1-hour window the
  sender moves the message to its dead-letter store. The dead-letter state
  is visible in the diagnostics UI and never produces silent failure.
- **Newer state wins.** If a newer command for the same
  `(serverConnectionId, jellyfinItemId, kind)` arrives while an older one
  is in flight, the sender MAY coalesce: the older message is dropped, the
  newer one is queued, and the dedup record is updated.

### 3.5 Network failure recovery

- The plugin and Watchoffit both keep a bounded durable queue on their side of
  the channel. A restart on either side does not lose messages.
- After reconnect, the sender flushes its queue in `header.sequence` order
  and the receiver applies the same dedup rules described in 3.2.
- Heartbeats (`heartbeat` events) are sent every 30 seconds. Three missed
  heartbeats trigger a transport-level reconnect with exponential backoff
  and jitter.

## 4. Command and event schemas

The `payload` field is a discriminated union keyed on `kind`. Both
implementations MUST treat the `kind` field as the routing key. C# code
SHOULD use a `switch` with a default arm that returns an `error` envelope
with `code: "INVALID_ENVELOPE"`.

### 4.1 Commands (Watchoffit → Jellyfin)

| `kind` | Purpose |
| --- | --- |
| `mark_played` | Mark the item as played. |
| `mark_unplayed` | Mark the item as not played. |
| `ping` | Health probe; reply is an `ack` with the same `nonce`. |
| `reconcile_request` | Ask Jellyfin to re-emit a recent `user_data` snapshot. |
| `rotate_credential` | Internal command that delivers a fresh credential to the plugin (see §4.1.5). |

#### 4.1.1 `mark_played`

```jsonc
{
  "kind": "mark_played",
  "jellyfinItemId": "jf-movie-1",
  "watchoffitUserId": "u_01HZ",
  "mediaKind": "movie",          // or "episode"
  "providerIds": {                // optional
    "tmdb": "603",
    "imdb": "tt0133093",
    "tvdb": "..."                 // optional
  },
  "watchedAt": "2026-08-26T20:34:00.000Z"   // optional; ISO 8601 UTC
}
```

| Field | Type | Required | Description |
| --- | --- | --- | --- |
| `kind` | `"mark_played"` | yes | Discriminator. |
| `jellyfinItemId` | string (1–128) | yes | Jellyfin item id. |
| `watchoffitUserId` | string (1–128) | yes | The Watchoffit user this command applies to. The plugin MUST use the previously-mapped Jellyfin user. |
| `mediaKind` | `"movie" \| "episode"` | yes | Hard signal: lets the plugin short-circuit resolution. |
| `providerIds.tmdb` | string | optional | TMDB id, normalized as string. |
| `providerIds.imdb` | string | optional | IMDb id. |
| `providerIds.tvdb` | string | optional | TVDB id. |
| `watchedAt` | ISO 8601 UTC | optional | The timestamp Watchoffit recorded for the watch. Defaults to the time the plugin applies the command. |

#### 4.1.2 `mark_unplayed`

```jsonc
{
  "kind": "mark_unplayed",
  "jellyfinItemId": "jf-movie-1",
  "watchoffitUserId": "u_01HZ",
  "mediaKind": "movie"
}
```

Fields are identical to `mark_played` except `kind` and the absence of
`watchedAt`.

#### 4.1.3 `ping`

```jsonc
{
  "kind": "ping",
  "jellyfinItemId": "jf-item-1",
  "watchoffitUserId": "u_01HZ",
  "mediaKind": "movie",
  "nonce": "abc123"
}
```

The plugin replies with an `ack` of `status: "ok"` whose `payload.note`
echoes the `nonce`. The `jellyfinItemId` / `watchoffitUserId` are present only
to satisfy the shared identity block; they are not otherwise used.

#### 4.1.4 `reconcile_request`

```jsonc
{
  "kind": "reconcile_request",
  "jellyfinItemId": "jf-movie-1",
  "watchoffitUserId": "u_01HZ",
  "mediaKind": "movie",
  "reason": "post_restart"        // or "missed_ack" | "manual"
}
```

`reason` is a **closed enum** of one of three literals:
`"missed_ack" | "post_restart" | "manual"`. Receivers MUST reject any
other value with `INVALID_ENVELOPE`. The plugin MAY use the value for
diagnostics; adding a new value is a major-version bump.

#### 4.1.5 `rotate_credential`

```jsonc
{
  "kind": "rotate_credential"
}
```

Internal v1 command that delivers a fresh credential to the plugin. The
command payload carries no fields; the new credential is returned in the
`ack` payload's `newCredential` field together with `rotatedAt`. See
§2.3 for the lifecycle and §4.3 for the ack shape.

### 4.2 Events (Jellyfin → Watchoffit)

| `kind` | Purpose |
| --- | --- |
| `playback_start` | User pressed Play. |
| `playback_progress` | Mid-playback position update. |
| `playback_stop` | User stopped playback (or it ended naturally). |
| `user_data` | Mirror of Jellyfin's `UserDataSaved`. |
| `heartbeat` | Periodic liveness signal. |

#### 4.2.1 `playback_start`

```jsonc
{
  "kind": "playback_start",
  "jellyfinItemId": "jf-movie-1",
  "watchoffitUserId": "u_01HZ",
  "mediaKind": "movie",
  "providerIds": { "tmdb": "603" },
  "sessionId": "sess_01HZ",
  "positionTicks": 0,
  "runtimeTicks": 7200000000,
  "startedAt": "2026-08-26T20:00:00.000Z"
}
```

| Field | Type | Required | Description |
| --- | --- | --- | --- |
| `kind` | `"playback_start"` | yes | Discriminator. |
| `sessionId` | string (1–128) | yes | Jellyfin playback session id. Used to deduplicate parallel progress bursts. |
| `positionTicks` | non-negative integer | yes | Position in Jellyfin ticks (1 tick = 100ns). Almost always `0` at start. |
| `runtimeTicks` | non-negative integer | yes | Runtime in Jellyfin ticks. |
| `startedAt` | ISO 8601 UTC | yes | Time Jellyfin recorded the start. |

#### 4.2.2 `playback_progress`

```jsonc
{
  "kind": "playback_progress",
  "jellyfinItemId": "jf-movie-1",
  "watchoffitUserId": "u_01HZ",
  "mediaKind": "movie",
  "sessionId": "sess_01HZ",
  "positionTicks": 1200000000,
  "runtimeTicks": 7200000000,
  "isPaused": false
}
```

`isPaused` mirrors Jellyfin's `Paused` flag.

#### 4.2.3 `playback_stop`

```jsonc
{
  "kind": "playback_stop",
  "jellyfinItemId": "jf-movie-1",
  "watchoffitUserId": "u_01HZ",
  "mediaKind": "movie",
  "sessionId": "sess_01HZ",
  "positionTicks": 7200000000,
  "runtimeTicks": 7200000000,
  "playedToCompletion": true,
  "stoppedAt": "2026-08-26T20:30:00.000Z"
}
```

`playedToCompletion` mirrors Jellyfin's `PlayedToCompletion` flag.

#### 4.2.4 `user_data`

```jsonc
{
  "kind": "user_data",
  "jellyfinItemId": "jf-movie-1",
  "watchoffitUserId": "u_01HZ",
  "mediaKind": "movie",
  "played": true,
  "playCount": 2,
  "isFavorite": false,
  "lastPlayedAt": "2026-08-26T19:00:00.000Z"   // nullable
}
```

`lastPlayedAt` may be `null` when Jellyfin has no record of the user
playing the item.

#### 4.2.5 `heartbeat`

```jsonc
{
  "kind": "heartbeat",
  "jellyfinItemId": "jf-server-id",
  "watchoffitUserId": "u_01HZ",
  "mediaKind": "movie",
  "queueDepth": 0,
  "lastSequence": 42,
  "pluginVersion": "1.0.0"
}
```

`queueDepth` is the number of envelopes in the plugin's outbound queue.
`lastSequence` is the highest monotonic `sequence` the plugin has emitted
on this channel. `pluginVersion` is the Jellyfin plugin's
`AssemblyVersion`.

### 4.3 Acks (either direction)

```jsonc
{
  "kind": "ack",
  "header": { /* … */, "correlationId": "cmd_01HZ" },
  "payload": {
    "commandId": "cmd_01HZ",
    "status": "ok",        // or "noop"
    "note": "applied"      // optional, max 256 chars
  }
}
```

`status: "noop"` is used when the command arrived but no side effect was
required (e.g. `mark_played` on an already-played item). It is still
considered delivered.

`header.correlationId` is **required** on every `ack` and MUST equal
`payload.commandId` (cross-field invariant, see §1.4).

Acks for the `rotate_credential` command use a richer payload:

```jsonc
{
  "kind": "ack",
  "header": { /* … */, "correlationId": "cmd_rotate_01HZ" },
  "payload": {
    "commandId": "cmd_rotate_01HZ",
    "status": "ok",
    "newCredential": "cred_01HZ_NEW",
    "rotatedAt": "2026-08-26T20:40:00.000Z",
    "note": "applied"      // optional
  }
}
```

The presence of `newCredential` is the discriminator that selects the
`rotate_credential` ack shape over the generic one.

### 4.4 Errors (either direction)

```jsonc
{
  "kind": "error",
  "header": { /* … */, "correlationId": "cmd_01HZ" },
  "payload": {
    "commandId": "cmd_01HZ",   // optional for unsolicited errors
    "code": "ITEM_NOT_FOUND",
    "message": "no such item"  // max 512 chars
  }
}
```

`code` is an **application-level** error code from
[Application-level codes](#62-application-level-codes). The recommended
literal set is enumerated in §6.2; any other `[A-Z0-9_]{1,64}` string is
also accepted by the schema but SHOULD be avoided so dashboards and
dedup rules stay in sync.

`header.correlationId` is **required** on every `error`. `commandId` is
optional because some errors are unsolicited (transport- or
plugin-side).

> **Parser errors vs application errors.** The `SafeErrorCode` literals in
> §6.1 are returned by `parseV1Envelope` only — they are the result of
> failing to parse or validate an envelope. They MUST NOT appear in the
> `error` envelope's `payload.code`. Code paths that need to surface a
> parser-level failure use the `V1ParseResult` `code` field, not a wire
> envelope.

`message` is for logs and diagnostics only and MUST NOT be surfaced to
end users.

## 5. Capability negotiation

### 5.1 When negotiation happens

1. **At pairing** — the plugin sends its `capabilities` block in every
   envelope during the first 10 minutes after the channel opens. Watchoffit
   replies with its own `capabilities` in the first `heartbeat` it sends.
2. **On reconnect** — both sides re-declare capabilities in the first
   envelope after each transport reconnect. This lets either side change
   `maxPayloadBytes` or `maxBatchSize` without re-pairing.
3. **On heartbeat** — the plugin MAY include `capabilities` in every
   `heartbeat` so Watchoffit always has a fresh view. Watchoffit MUST tolerate the
   field being absent.

### 5.2 What each side declares

| Field | Jellyfin plugin | Watchoffit core |
| --- | --- | --- |
| `minProtocolVersion` | `1` (today) | `1` (today) |
| `maxProtocolVersion` | `1` (today) | `1` (today) |
| `maxPayloadBytes` | max bytes the plugin will produce in one envelope. | max bytes Watchoffit will produce in one envelope. |
| `maxBatchSize` | max events the plugin will batch. v1 always uses `1`. | max commands Watchoffit will batch. v1 always uses `1`. |

### 5.3 Outcome

- **Both speak v1** → channel is open.
- **Plugin older than Watchoffit** (`max < min`) → Watchoffit refuses the channel
  with an `error` of `code: "PROTOCOL_VERSION_UNSUPPORTED"`. The plugin UI
  shows "update required" with a link to the latest release.
- **Plugin newer than Watchoffit** (`min > max`) → Watchoffit refuses the channel
  with the same `code`. The Watchoffit admin UI shows "Watchoffit update required".
- **`maxPayloadBytes` mismatch** → the receiver drops envelopes that
  exceed its own `maxPayloadBytes` with `code: "INVALID_ENVELOPE"`. The
  sender reduces its batch size on the next attempt.
- **`maxBatchSize` mismatch** → the receiver drops over-batched envelopes
  the same way. The sender reduces to the receiver's `maxBatchSize`.

There is no automatic downgrade to an older protocol version. v1 is the
only version that exists today; future versions add new envelopes rather
than re-interpreting old ones.

## 6. Safe error codes

Stable, machine-readable error codes. The set is frozen for v1. Adding a
new code is a minor protocol bump; renaming or removing a code is a major
bump. The C# plugin and the Watchoffit core MUST use the same literal set.

There are two disjoint sets of codes, with different scopes and
lifecycle semantics:

- **Parser-level codes (§6.1)** — returned by `parseV1Envelope` when the
  envelope itself is invalid. They never appear on the wire.
- **Application-level codes (§6.2)** — travel inside the `error` envelope's
  `payload.code`. They describe application-side failures, not wire
  failures.

### 6.1 Parser-level codes

| Code | HTTP-like | Retry class | When |
| --- | --- | --- | --- |
| `PROTOCOL_VERSION_UNSUPPORTED` | 400 | terminal | The envelope `header.version` is not `1`. |
| `INVALID_ENVELOPE` | 400 | terminal | The envelope does not match the schema. The `message` field carries the Zod / JSON issue summary. |
| `AUTH_REQUIRED` | 401 | terminal | The credential is missing, revoked, or rotated. Pair again before retrying. |
| `RATE_LIMITED` | 429 | retryable | The receiver is applying back-pressure. The sender MUST respect `Retry-After` if present, otherwise retry with exponential backoff. |
| `INTERNAL_ERROR` | 500 | retryable | Unexpected exception on the receiver. The sender MUST retry with backoff. |

**Retry classes** (used throughout §6):

- `terminal` — sending the same envelope again will not help. Stop
  retrying, surface the failure, and require operator / code-level
  intervention (e.g. re-pair, schema upgrade).
- `retryable` — the failure is transient. The sender follows the
  exponential-backoff / dead-letter rules in §3.4.

### 6.2 Application-level codes

The `error` envelope's `payload.code` is a `[A-Z0-9_]{1,64}` string. The
Watchoffit core and the Jellyfin plugin SHOULD use the recommended literals
below; the closed set is also exported from the TypeScript skeleton as
`ApplicationErrorCode`.

| Code | HTTP-like | Retry class | When |
| --- | --- | --- | --- |
| `ITEM_NOT_FOUND` | 404 | terminal | The `jellyfinItemId` does not exist on this Jellyfin server. |
| `ITEM_UNRESOLVED` | 422 | terminal | The item exists but has no provider ids Watchoffit can match. |
| `USER_NOT_MAPPED` | 403 | terminal | The `watchoffitUserId` is not mapped to a Jellyfin user on this connection. |
| `LIBRARY_EXCLUDED` | 403 | terminal | The mapped Jellyfin library is disabled in the Watchoffit configuration. |
| `ALREADY_APPLIED` | 200 | terminal | The command was already applied; returned as `error` for visibility, no retry. |
| `OUTBOX_FULL` | 503 | retryable | The plugin's outbound queue is full; sender retries with backoff. |
| `RATE_LIMITED_BY_REMOTE` | 429 | retryable | Jellyfin refused the call because of upstream rate limiting. |

`terminal` codes are surfaced as failures; the dead-letter store in §3.4
records them so an operator can decide whether to re-enqueue after a
config change. `retryable` codes follow the same backoff and
dead-letter rules as the parser-level `RATE_LIMITED` / `INTERNAL_ERROR`
codes.

### 6.3 Mapping rule

| Safe code | Maps to ORPC error |
| --- | --- |
| `PROTOCOL_VERSION_UNSUPPORTED` | `AppErrorCode.JELLYFIN_NOT_CONFIGURED` |
| `INVALID_ENVELOPE` | `AppErrorCode.JELLYFIN_NOT_CONFIGURED` |
| `AUTH_REQUIRED` | `AppErrorCode.JELLYFIN_AUTH_FAILED` |
| `RATE_LIMITED` | `AppErrorCode.JELLYFIN_UNREACHABLE` |
| `INTERNAL_ERROR` | `AppErrorCode.JELLYFIN_UNREACHABLE` |

`AppErrorCode` is the existing Watchoffit error enum in
`packages/api/src/errors.ts`. The C# plugin does not need to know the
mapping; it only emits the safe codes.

## 7. JSON acceptance fixtures

These are the canonical fixtures. Both codebases MUST round-trip them
through their respective parsers without modification. Additional fixtures
MAY be added; removing or renaming one is a breaking change.

Each fixture is checked in as a standalone JSON file in
`packages/core/test/fixtures/watchoffit-plugin-protocol/v1/` (TypeScript
side). The C# test project embeds the same files as resources. Every
timestamp below ends in `Z`; this is the wire-format requirement (see
§1.4 and §8.4) and is enforced by the TypeScript schema with
`z.string().datetime({ offset: false })`. The TypeScript suite contains
a "canonical fixtures" test that deep-equals each fixture's parsed
result against the JSON itself, so any drift between this document and
the implementation fails CI.

### 7.1 `command` — `mark_played` (movie)

```json
{
  "kind": "command",
  "header": {
    "version": 1,
    "kind": "command",
    "id": "cmd_01HZ0001",
    "sequence": 1,
    "timestamp": "2026-08-26T20:34:41.000Z",
    "serverConnectionId": "scn_01HZ0001",
    "capabilities": {
      "minProtocolVersion": 1,
      "maxProtocolVersion": 1,
      "maxPayloadBytes": 65536,
      "maxBatchSize": 50
    }
  },
  "payload": {
    "kind": "mark_played",
    "jellyfinItemId": "jf-movie-1",
    "watchoffitUserId": "u_01HZ0001",
    "mediaKind": "movie",
    "providerIds": { "tmdb": "603", "imdb": "tt0133093" },
    "watchedAt": "2026-08-26T20:34:00.000Z"
  }
}
```

### 7.2 `command` — `mark_played` (episode, no providerIds)

```json
{
  "kind": "command",
  "header": {
    "version": 1,
    "kind": "command",
    "id": "cmd_01HZ0002",
    "sequence": 2,
    "timestamp": "2026-08-26T20:35:00.000Z",
    "serverConnectionId": "scn_01HZ0001"
  },
  "payload": {
    "kind": "mark_played",
    "jellyfinItemId": "jf-ep-1",
    "watchoffitUserId": "u_01HZ0001",
    "mediaKind": "episode"
  }
}
```

### 7.3 `command` — `mark_unplayed`

```json
{
  "kind": "command",
  "header": {
    "version": 1,
    "kind": "command",
    "id": "cmd_01HZ0003",
    "sequence": 3,
    "timestamp": "2026-08-26T20:36:00.000Z",
    "serverConnectionId": "scn_01HZ0001"
  },
  "payload": {
    "kind": "mark_unplayed",
    "jellyfinItemId": "jf-movie-1",
    "watchoffitUserId": "u_01HZ0001",
    "mediaKind": "movie"
  }
}
```

### 7.4 `command` — `ping`

```json
{
  "kind": "command",
  "header": {
    "version": 1,
    "kind": "command",
    "id": "cmd_01HZ0004",
    "sequence": 4,
    "timestamp": "2026-08-26T20:37:00.000Z",
    "serverConnectionId": "scn_01HZ0001"
  },
  "payload": {
    "kind": "ping",
    "jellyfinItemId": "jf-item-1",
    "watchoffitUserId": "u_01HZ0001",
    "mediaKind": "movie",
    "nonce": "ping-2026-08-26T20:37:00Z"
  }
}
```

### 7.5 `command` — `reconcile_request`

```json
{
  "kind": "command",
  "header": {
    "version": 1,
    "kind": "command",
    "id": "cmd_01HZ0005",
    "sequence": 5,
    "timestamp": "2026-08-26T20:38:00.000Z",
    "serverConnectionId": "scn_01HZ0001"
  },
  "payload": {
    "kind": "reconcile_request",
    "jellyfinItemId": "jf-movie-1",
    "watchoffitUserId": "u_01HZ0001",
    "mediaKind": "movie",
    "reason": "post_restart"
  }
}
```

### 7.6 `event` — `playback_start`

```json
{
  "kind": "event",
  "header": {
    "version": 1,
    "kind": "event",
    "id": "evt_01HZ0001",
    "correlationId": "cmd_01HZ0001",
    "sequence": 1,
    "timestamp": "2026-08-26T20:00:00.000Z",
    "serverConnectionId": "scn_01HZ0001"
  },
  "payload": {
    "kind": "playback_start",
    "jellyfinItemId": "jf-movie-1",
    "watchoffitUserId": "u_01HZ0001",
    "mediaKind": "movie",
    "sessionId": "sess_01HZ0001",
    "positionTicks": 0,
    "runtimeTicks": 7200000000,
    "startedAt": "2026-08-26T20:00:00.000Z"
  }
}
```

### 7.7 `event` — `playback_progress`

```json
{
  "kind": "event",
  "header": {
    "version": 1,
    "kind": "event",
    "id": "evt_01HZ0002",
    "sequence": 2,
    "timestamp": "2026-08-26T20:15:00.000Z",
    "serverConnectionId": "scn_01HZ0001"
  },
  "payload": {
    "kind": "playback_progress",
    "jellyfinItemId": "jf-movie-1",
    "watchoffitUserId": "u_01HZ0001",
    "mediaKind": "movie",
    "sessionId": "sess_01HZ0001",
    "positionTicks": 1200000000,
    "runtimeTicks": 7200000000,
    "isPaused": false
  }
}
```

### 7.8 `event` — `playback_stop` (completed)

```json
{
  "kind": "event",
  "header": {
    "version": 1,
    "kind": "event",
    "id": "evt_01HZ0003",
    "sequence": 3,
    "timestamp": "2026-08-26T20:30:00.000Z",
    "serverConnectionId": "scn_01HZ0001"
  },
  "payload": {
    "kind": "playback_stop",
    "jellyfinItemId": "jf-movie-1",
    "watchoffitUserId": "u_01HZ0001",
    "mediaKind": "movie",
    "sessionId": "sess_01HZ0001",
    "positionTicks": 7200000000,
    "runtimeTicks": 7200000000,
    "playedToCompletion": true,
    "stoppedAt": "2026-08-26T20:30:00.000Z"
  }
}
```

### 7.9 `event` — `user_data`

```json
{
  "kind": "event",
  "header": {
    "version": 1,
    "kind": "event",
    "id": "evt_01HZ0004",
    "sequence": 4,
    "timestamp": "2026-08-26T20:31:00.000Z",
    "serverConnectionId": "scn_01HZ0001"
  },
  "payload": {
    "kind": "user_data",
    "jellyfinItemId": "jf-movie-1",
    "watchoffitUserId": "u_01HZ0001",
    "mediaKind": "movie",
    "played": true,
    "playCount": 2,
    "isFavorite": false,
    "lastPlayedAt": "2026-08-26T19:00:00.000Z"
  }
}
```

### 7.10 `event` — `heartbeat`

```json
{
  "kind": "event",
  "header": {
    "version": 1,
    "kind": "event",
    "id": "evt_01HZ0005",
    "sequence": 5,
    "timestamp": "2026-08-26T20:32:00.000Z",
    "serverConnectionId": "scn_01HZ0001",
    "capabilities": {
      "minProtocolVersion": 1,
      "maxProtocolVersion": 1,
      "maxPayloadBytes": 65536,
      "maxBatchSize": 50
    }
  },
  "payload": {
    "kind": "heartbeat",
    "jellyfinItemId": "jf-server-01HZ",
    "watchoffitUserId": "u_01HZ0001",
    "mediaKind": "movie",
    "queueDepth": 0,
    "lastSequence": 5,
    "pluginVersion": "1.0.0"
  }
}
```

### 7.11 `ack`

```json
{
  "kind": "ack",
  "header": {
    "version": 1,
    "kind": "ack",
    "id": "ack_01HZ0001",
    "correlationId": "cmd_01HZ0001",
    "sequence": 1,
    "timestamp": "2026-08-26T20:34:42.000Z",
    "serverConnectionId": "scn_01HZ0001"
  },
  "payload": {
    "commandId": "cmd_01HZ0001",
    "status": "ok",
    "note": "applied"
  }
}
```

### 7.12 `error`

```json
{
  "kind": "error",
  "header": {
    "version": 1,
    "kind": "error",
    "id": "err_01HZ0001",
    "correlationId": "cmd_01HZ0003",
    "sequence": 2,
    "timestamp": "2026-08-26T20:36:01.000Z",
    "serverConnectionId": "scn_01HZ0001"
  },
  "payload": {
    "commandId": "cmd_01HZ0003",
    "code": "ITEM_NOT_FOUND",
    "message": "no such item"
  }
}
```

## 8. C# / TypeScript parity contract

This section is the source of truth for the C# and TypeScript
implementations. Any change here must be reflected in both codebases in the
same commit.

### 8.1 Names

| Concept | TypeScript name | C# name | Notes |
| --- | --- | --- | --- |
| Protocol version | `V1_PROTOCOL_VERSION` | `ProtocolVersion.V1` | `const int V1 = 1;` |
| Envelope | `V1Envelope` | `V1Envelope` | C# class. |
| Envelope kind | `V1EnvelopeKind` | `V1EnvelopeKind` | C# enum with `[JsonStringEnum]` so values serialize as `"command"`, `"event"`, `"ack"`, `"error"`. |
| Header | `V1Header` | `V1Header` | C# record. |
| Command payload | `V1CommandPayload` | `V1CommandPayload` | Sealed abstract class hierarchy; `Kind` is the abstract discriminator. |
| Event payload | `V1EventPayload` | `V1EventPayload` | Sealed abstract class hierarchy. |
| Safe error code | `SafeErrorCode` | `SafeErrorCode` | C# `enum` with `[Description]` attributes for the string literal. |
| Parse result | `V1ParseResult` | `V1ParseResult` | Discriminated union / `OneOf<Ok, Failure>`. |

### 8.2 Required fields in both implementations

The following fields are MANDATORY on every envelope in both languages:

- `header.version` (literal `1`)
- `header.kind`
- `header.id`
- `header.sequence`
- `header.timestamp`
- `header.serverConnectionId`
- `payload.kind` (every command and event payload carries its own
  `kind` discriminator; this duplicates the envelope's `kind` so a payload
  can be inspected without the envelope wrapper)

The following fields are MANDATORY on specific envelope kinds:

- `header.correlationId` — every `ack` and every `error`.
- `payload.commandId` — every `ack` and every `error` whose `header.kind`
  is `"ack"` or `"error"` and which was sent in response to a command.
- `payload.sessionId` — every `playback_start`, `playback_progress`,
  `playback_stop` event.
- `payload.positionTicks` and `payload.runtimeTicks` — every
  `playback_start`, `playback_progress`, `playback_stop` event.
- `payload.isPaused` — every `playback_progress` event.
- `payload.playedToCompletion` — every `playback_stop` event.
- `payload.startedAt` — every `playback_start` event.
- `payload.stoppedAt` — every `playback_stop` event.
- `payload.lastPlayedAt` — every `user_data` event.
- `payload.playCount` — every `user_data` event.
- `payload.played` — every `user_data` event.
- `payload.queueDepth`, `payload.lastSequence`, `payload.pluginVersion` —
  every `heartbeat` event.

### 8.3 Discriminator and JSON

- **TypeScript** uses Zod's `z.discriminatedUnion("kind", [...])`. The
  `kind` field is the literal string. TS narrows the payload type after
  the check, so call sites can do:
  ```ts
  if (envelope.payload.kind === "mark_played") {
    envelope.payload.watchedAt; // narrowed to string | undefined
  }
  ```
- **C#** uses a sealed abstract class hierarchy with a `Kind` property on
  each concrete class. JSON.NET's `TypeNameHandling.Auto` is NOT used.
  Instead, the plugin uses a custom `JsonConverter` that reads the
  `"kind"` field from the payload object and dispatches to the correct
  concrete class. The converter is shared between commands and events:
  ```csharp
  public abstract record V1CommandPayload {
    public abstract string Kind { get; }
  }
  public sealed record MarkPlayedCommand(...) : V1CommandPayload {
    public override string Kind => "mark_played";
  }
  ```
- Both implementations MUST reject unknown discriminator values with
  `INVALID_ENVELOPE`.

### 8.4 Date / time handling

The wire format is **Z-only** ISO 8601 UTC with millisecond precision and
a trailing `Z`. No `+00:00`, no `+0100`, no local times. This is the
single canonical form both sides MUST emit and the only one the parsers
accept.

- **TypeScript.** Every timestamp is a `string` validated by
  `z.string().datetime({ offset: false })`, which rejects any value
  that does not end in `Z`. Date arithmetic is performed by parsing
  with `new Date(value)` and reading `.getTime()`; the result is
  re-serialized as `toISOString()` before going back on the wire, which
  guarantees the `Z` suffix.
- **C#** uses `DateTimeOffset` with `Offset == TimeSpan.Zero` for every
  timestamp. The C# plugin MUST emit timestamps via
  `DateTimeOffset.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss.fffZ")`
  (or any equivalent formatter that produces a trailing `Z` and nothing
  else) so the TypeScript parser accepts the payload. JSON.NET is
  configured with `DateTimeZoneHandling = DateTimeZoneHandling.Utc` and
  `DateFormatHandling = DateFormatHandling.IsoDateFormat`, and a custom
  `JsonConverter<DateTimeOffset>` forces `DateTimeKind.Utc` on read.
  Both sides MUST NOT use `DateTime` (no offset, ambiguous) or
  `DateTimeKind.Local`.

### 8.5 Identifier handling

- `header.id` and `header.correlationId` are opaque strings on the wire.
  In TypeScript they are typed as `string`; in C# they are typed as
  `V1MessageId` (a `readonly record struct` wrapping `string`). The C#
  plugin MUST NOT assume ULID / UUID format; the strings are
  implementation-defined on each side as long as they are unique within
  the dedup window.
- `jellyfinItemId` and `watchoffitUserId` are similarly opaque strings.
- Numeric fields (`sequence`, `positionTicks`, `runtimeTicks`, `playCount`,
  `queueDepth`, `lastSequence`) are encoded as JSON numbers. In
  TypeScript they are `z.number().int().nonnegative()`. In C# they are
  `long` (Int64). The C# plugin MUST use `Int64` even though
  `positionTicks` fits in `Int32` today — a 16-bit field is too small for
  very long media.

### 8.6 Strict object mode

Both implementations parse with strict object mode. Unknown top-level
fields on the envelope, on the header, or on any payload MUST cause a
parse failure with `INVALID_ENVELOPE`. This protects both sides from
silent typos (`watched_At` vs `watchedAt`).

In TypeScript this is achieved by `.strict()` on every Zod object. In C#
this is achieved by setting `MissingMemberHandling.Error` on the
`JsonSerializerSettings` used for protocol traffic.

### 8.7 Test parity

The CI pipelines for both repositories run the same JSON fixtures in
section 7 through their respective parsers and assert byte-for-byte
equality. The C# test project lives in the plugin repository and depends
on the same fixture files; the TypeScript tests live in
`packages/core/test/watchoffit-plugin-protocol-v1.test.ts` in this
repository.

### 8.8 Reference TS skeleton

The canonical TypeScript skeleton lives at
`packages/core/src/integrations/watchoffit-plugin-protocol/v1.ts`. Public
exports go through the barrel
`packages/core/src/integrations/watchoffit-plugin-protocol/index.ts`.

The C# skeleton (to be created in the plugin repository) MUST export the
same names with the same semantics. The shape of the public API is:

```csharp
namespace Watchoffit.Plugin.Protocol.V1;

public static class V1Protocol {
    public const int Version = 1;
}

public sealed record V1Envelope(
    V1EnvelopeKind Kind,
    V1Header Header,
    V1CommandPayload? CommandPayload,
    V1EventPayload? EventPayload,
    V1AckPayload? AckPayload,
    V1ErrorPayload? ErrorPayload
);

public abstract record V1CommandPayload {
    public abstract string Kind { get; }
}
public sealed record MarkPlayedCommand(
    string JellyfinItemId,
    string WatchoffitUserId,
    MediaKind MediaKind,
    ProviderIds? ProviderIds,
    DateTimeOffset? WatchedAt
) : V1CommandPayload { public override string Kind => "mark_played"; }
// … MarkUnplayedCommand, PingCommand, ReconcileRequestCommand …

public abstract record V1EventPayload {
    public abstract string Kind { get; }
}
// … PlaybackStartEvent, PlaybackProgressEvent, PlaybackStopEvent,
//     UserDataEvent, HeartbeatEvent …

public static class V1Parser {
    public static V1ParseResult Parse(string json) { /* … */ }
}
```

The C# implementation MUST be byte-compatible with the JSON produced by
the TypeScript implementation. The C# test project MUST include the
fixtures from section 7 as embedded resources and assert that parsing
them produces the same object graph that the TypeScript tests assert.

# Watchoffit Jellyfin Plugin - Phase 3 Pairing Design

> **Status:** design for Phase 3 implementation.
> **Audience:** Jellyfin plugin C# maintainers and Watchoffit core maintainers.
> **Goal:** take an installed Watchoffit Jellyfin plugin from an empty local state
> to a durable paired state with a valid Watchoffit credential.
This document is constrained by `protocol-v1.md`, `compat.md`,
`versioning.md`, and the current plugin scaffold under `plugins/jellyfin/`.
The plugin remains GPL-3.0, targets `net9.0`, loads into Jellyfin 10.11+, uses
GUID `ed8e9c41-2e0f-5872-93f2-06feb1bc37d1`, and stores private data under
the plugin `dataFolder` from `meta.json` (`/config/plugins/Watchoffit/` in the
standard container layout).

## 1. Goals and non-goals
### 1.1 Goal
Phase 3 implements the pairing flow that lets a Jellyfin administrator enter a
Watchoffit base URL and a one-time code, validates that Watchoffit and the plugin both
speak protocol v1, redeems the code for a long-lived opaque credential,
persists that credential under the plugin data folder, rehydrates it across
Jellyfin restarts, and starts authenticated heartbeat traffic.
### 1.2 Non-goals
Event forwarding is not part of this design.
Outbound commands from Watchoffit to Jellyfin are not part of this design.
Backfill, library inventory, reconciliation, and user mapping are not part of
this design.
Re-connection after a Jellyfin ABI or major-version upgrade is not part of
this design; that belongs with the release and migration plan in
`versioning.md`.
### 1.3 Protocol note
The existing RFC section 2 describes an older HTTPS pairing flow outside the
v1 envelope. This design uses envelope-shaped HTTP requests for Phase 3 because
the task requires `ping`, `challenge_request`, and `redeem_request` command
shapes. Implementation must first add those pairing payloads and ack payloads
to `packages/core/src/integrations/watchoffit-plugin-protocol/v1.ts` and mirror
them in C#. No new envelope top-level fields are introduced.

## 2. User flow
### 2.1 Walkthrough
1. The administrator installs the Watchoffit plugin and opens Dashboard → Plugins
   → Watchoffit.
2. Jellyfin loads the plugin page from `WatchoffitPlugin.GetPages()`.
3. `PairingService` asks `WatchoffitConnectionStore` to load
   `/config/plugins/Watchoffit/connection.json`.
4. If no usable file exists, the page renders in the not-paired state.
5. The administrator starts "Connect Jellyfin" in Watchoffit, which mints a
   single-use code with a 10 minute TTL.
6. The administrator enters the Watchoffit base URL and the code in Jellyfin.
7. The plugin performs `GET /api/watchoffit-plugin/system-info`.
8. If protocol v1 and Jellyfin 10.11+ are accepted, the plugin posts
   `challenge_request`.
9. The plugin posts `redeem_request` with the user-entered code.
10. Watchoffit returns a credential, connection id, server name, and capabilities.
11. The plugin writes `connection.json` atomically before using the credential.
12. The page refreshes to "Connected" and the heartbeat loop starts.
13. The next phase can enable a manual Sync action because credentialed v1
    calls now work.
### 2.2 Not-paired page
```text
+--------------------------------------------------------------------+
| Watchoffit                                                             |
+--------------------------------------------------------------------+
| Status                                                             |
|   Not connected                                                    |
|                                                                    |
| Watchoffit server URL                                                  |
|   [ http://localhost:8096                                      ]   |
|                                                                    |
| Pairing code                                                       |
|   [ AB12CD                                                   ]     |
|                                                                    |
|   [ Connect to Watchoffit ]                                            |
|                                                                    |
| Last attempt                                                       |
|   No connection attempt yet.                                       |
+--------------------------------------------------------------------+
```
### 2.3 Paired page
```text
+--------------------------------------------------------------------+
| Watchoffit                                                             |
+--------------------------------------------------------------------+
| Status                                                             |
|   Connected                                                        |
|                                                                    |
| Watchoffit server                                                      |
|   Family Watchoffit                                                    |
|                                                                    |
| Server connection id                                               |
|   scn_01J5RJ8X4EXAMPLE                                             |
|                                                                    |
| Capabilities                                                       |
|   Protocol 1-1, payload 65536 bytes, batch size 50                 |
|                                                                    |
| Last ping                                                          |
|   2026-08-27T10:14:30.000Z                                         |
|                                                                    |
|   [ Rotate credential ]   [ Disconnect ]                           |
+--------------------------------------------------------------------+
```
The paired page never renders the credential value.

## 3. Wire trace
### 3.1 Shared rules
Every JSON body below is parsed through the v1 envelope schema. During
pre-pairing calls the header uses `serverConnectionId: "pending"` because v1
requires the field before Watchoffit has assigned a real id. Watchoffit accepts that
literal only on unauthenticated pairing endpoints.
Common capability block:
```json
{
  "minProtocolVersion": 1,
  "maxProtocolVersion": 1,
  "maxPayloadBytes": 65536,
  "maxBatchSize": 50
}
```
### 3.2 `GET /api/watchoffit-plugin/system-info`
The pre-check is cheap and must not redeem a code.
```http
GET /api/watchoffit-plugin/system-info HTTP/1.1
Host: watchoffit.example.com
Accept: application/json
X-Watchoffit-Protocol-Min: 1
X-Watchoffit-Protocol-Max: 1
X-Watchoffit-Plugin-Guid: ed8e9c41-2e0f-5872-93f2-06feb1bc37d1
X-Watchoffit-Plugin-Version: 1.0.0.0
X-Jellyfin-Version: 10.11.11
X-Jellyfin-Server-Id: jf_server_01J5RJ8X4LOCAL
```
```http
HTTP/1.1 200 OK
Content-Type: application/json
Cache-Control: no-store
```
```json
{
  "kind": "command",
  "header": {
    "version": 1,
    "kind": "command",
    "id": "cmd_system_info_01J5RJ8X4A",
    "sequence": 1,
    "timestamp": "2026-08-27T10:00:00.000Z",
    "serverConnectionId": "pending",
    "capabilities": { "minProtocolVersion": 1, "maxProtocolVersion": 1, "maxPayloadBytes": 65536, "maxBatchSize": 50 }
  },
  "payload": {
    "kind": "challenge_request",
    "serverConnectionId": "scn_01J5RJ8X4EXAMPLE",
    "watchoffitServerName": "Family Watchoffit",
    "pairingCode": "AB12CD",
    "expiresAt": "2026-08-27T10:10:00.000Z"
  }
}
```
### 3.3 `POST /api/watchoffit-plugin/pairing/challenge`
This binds Jellyfin server facts to a Watchoffit connection id. It does not issue
the long-lived credential.
```http
POST /api/watchoffit-plugin/pairing/challenge HTTP/1.1
Host: watchoffit.example.com
Content-Type: application/json
Accept: application/json
```
```json
{
  "kind": "command",
  "header": {
    "version": 1,
    "kind": "command",
    "id": "cmd_challenge_01J5RJ8X4B",
    "sequence": 1,
    "timestamp": "2026-08-27T10:01:00.000Z",
    "serverConnectionId": "pending",
    "capabilities": { "minProtocolVersion": 1, "maxProtocolVersion": 1, "maxPayloadBytes": 65536, "maxBatchSize": 50 }
  },
  "payload": {
    "kind": "challenge_request",
    "jellyfinServerId": "jf_server_01J5RJ8X4LOCAL",
    "jellyfinVersion": "10.11.11",
    "pluginVersion": "1.0.0.0",
    "pluginGuid": "ed8e9c41-2e0f-5872-93f2-06feb1bc37d1"
  }
}
```
```http
HTTP/1.1 200 OK
Content-Type: application/json
Cache-Control: no-store
```
```json
{
  "kind": "ack",
  "header": {
    "version": 1,
    "kind": "ack",
    "id": "ack_challenge_01J5RJ8X4C",
    "correlationId": "cmd_challenge_01J5RJ8X4B",
    "sequence": 1,
    "timestamp": "2026-08-27T10:01:01.000Z",
    "serverConnectionId": "scn_01J5RJ8X4EXAMPLE"
  },
  "payload": {
    "commandId": "cmd_challenge_01J5RJ8X4B",
    "status": "ok",
    "serverConnectionId": "scn_01J5RJ8X4EXAMPLE",
    "watchoffitServerName": "Family Watchoffit",
    "pairingCode": "AB12CD",
    "expiresAt": "2026-08-27T10:10:00.000Z"
  }
}
```
### 3.4 `POST /api/watchoffit-plugin/pairing/redeem`
Redeem consumes the single-use code and returns the opaque credential.
```http
POST /api/watchoffit-plugin/pairing/redeem HTTP/1.1
Host: watchoffit.example.com
Content-Type: application/json
Accept: application/json
```
```json
{
  "kind": "command",
  "header": {
    "version": 1,
    "kind": "command",
    "id": "cmd_redeem_01J5RJ8X4D",
    "sequence": 2,
    "timestamp": "2026-08-27T10:02:00.000Z",
    "serverConnectionId": "scn_01J5RJ8X4EXAMPLE",
    "capabilities": { "minProtocolVersion": 1, "maxProtocolVersion": 1, "maxPayloadBytes": 65536, "maxBatchSize": 50 }
  },
  "payload": {
    "kind": "redeem_request",
    "pairingCode": "AB12CD",
    "jellyfinServerId": "jf_server_01J5RJ8X4LOCAL"
  }
}
```
```http
HTTP/1.1 200 OK
Content-Type: application/json
Cache-Control: no-store
```
```json
{
  "kind": "ack",
  "header": {
    "version": 1,
    "kind": "ack",
    "id": "ack_redeem_01J5RJ8X4E",
    "correlationId": "cmd_redeem_01J5RJ8X4D",
    "sequence": 2,
    "timestamp": "2026-08-27T10:02:01.000Z",
    "serverConnectionId": "scn_01J5RJ8X4EXAMPLE"
  },
  "payload": {
    "commandId": "cmd_redeem_01J5RJ8X4D",
    "status": "ok",
    "credential": "cred_01J5RJ8X4SECRET",
    "serverConnectionId": "scn_01J5RJ8X4EXAMPLE",
    "watchoffitServerName": "Family Watchoffit",
    "issuedAt": "2026-08-27T10:02:01.000Z"
  }
}
```
The plugin must persist this response before it starts heartbeat traffic.
### 3.5 `POST /api/watchoffit-plugin/pairing/rotate-credential`
Rotation is authenticated with the current credential. The existing RFC already
defines `rotate_credential` and its ack shape.
```http
POST /api/watchoffit-plugin/pairing/rotate-credential HTTP/1.1
Host: watchoffit.example.com
Content-Type: application/json
Accept: application/json
Authorization: Bearer cred_01J5RJ8X4SECRET
```
```json
{
  "kind": "command",
  "header": {
    "version": 1,
    "kind": "command",
    "id": "cmd_rotate_01J5RJ8X4F",
    "sequence": 3,
    "timestamp": "2026-08-27T10:03:00.000Z",
    "serverConnectionId": "scn_01J5RJ8X4EXAMPLE",
    "capabilities": { "minProtocolVersion": 1, "maxProtocolVersion": 1, "maxPayloadBytes": 65536, "maxBatchSize": 50 }
  },
  "payload": {
    "kind": "rotate_credential"
  }
}
```
```http
HTTP/1.1 200 OK
Content-Type: application/json
Cache-Control: no-store
```
```json
{
  "kind": "ack",
  "header": {
    "version": 1,
    "kind": "ack",
    "id": "ack_rotate_01J5RJ8X4G",
    "correlationId": "cmd_rotate_01J5RJ8X4F",
    "sequence": 3,
    "timestamp": "2026-08-27T10:03:01.000Z",
    "serverConnectionId": "scn_01J5RJ8X4EXAMPLE"
  },
  "payload": {
    "commandId": "cmd_rotate_01J5RJ8X4F",
    "status": "ok",
    "newCredential": "cred_01J5RJ8X4NEWSECRET",
    "rotatedAt": "2026-08-27T10:03:01.000Z",
    "note": "applied"
  }
}
```
The plugin writes the new credential atomically before sending with it. If the
write fails, it keeps using the old credential during Watchoffit's 24 hour grace
period and logs the persistence failure without credential values.
### 3.6 `DELETE /api/watchoffit-plugin/pairing/credential`
Disconnect revokes the remote credential and then removes local state.
```http
DELETE /api/watchoffit-plugin/pairing/credential HTTP/1.1
Host: watchoffit.example.com
Content-Type: application/json
Accept: application/json
Authorization: Bearer cred_01J5RJ8X4NEWSECRET
```
```json
{
  "kind": "command",
  "header": {
    "version": 1,
    "kind": "command",
    "id": "cmd_revoke_01J5RJ8X4H",
    "sequence": 4,
    "timestamp": "2026-08-27T10:04:00.000Z",
    "serverConnectionId": "scn_01J5RJ8X4EXAMPLE",
    "capabilities": { "minProtocolVersion": 1, "maxProtocolVersion": 1, "maxPayloadBytes": 65536, "maxBatchSize": 50 }
  },
  "payload": {
    "kind": "revoke_request",
    "jellyfinServerId": "jf_server_01J5RJ8X4LOCAL",
    "jellyfinVersion": "10.11.11",
    "pluginVersion": "1.0.0.0",
    "pluginGuid": "ed8e9c41-2e0f-5872-93f2-06feb1bc37d1"
  }
}
```
```http
HTTP/1.1 200 OK
Content-Type: application/json
Cache-Control: no-store
```
```json
{
  "kind": "ack",
  "header": {
    "version": 1,
    "kind": "ack",
    "id": "ack_revoke_01J5RJ8X4J",
    "correlationId": "cmd_revoke_01J5RJ8X4H",
    "sequence": 4,
    "timestamp": "2026-08-27T10:04:01.000Z",
    "serverConnectionId": "scn_01J5RJ8X4EXAMPLE"
  },
  "payload": {
    "commandId": "cmd_revoke_01J5RJ8X4H",
    "status": "ok",
    "note": "credential revoked"
  }
}
```
`revoke_request` is a separate payload kind from `redeem_request` so the
ack payload shape stays stable across the pair flow. The earlier
proposal to overload `redeem_request.action = "revoke"` was rejected
during codex review: splitting the kinds makes the parser narrow (no
"action" string to validate), keeps the ack union field-presence
discriminator clean, and means future code that needs the credential
mid-revoke can branch on `payload.kind` without conditional parsing.
The endpoint, header, and ack shape are unchanged from the original
RFC; only the payload kind differs.
### 3.7 `POST /api/watchoffit-plugin/ping`
Heartbeat starts 5 seconds after redeem, then repeats every 30 seconds with
random +/- 20 percent jitter.
```http
POST /api/watchoffit-plugin/ping HTTP/1.1
Host: watchoffit.example.com
Content-Type: application/json
Accept: application/json
Authorization: Bearer cred_01J5RJ8X4NEWSECRET
```
```json
{
  "kind": "event",
  "header": {
    "version": 1,
    "kind": "event",
    "id": "evt_heartbeat_01J5RJ8X4K",
    "sequence": 5,
    "timestamp": "2026-08-27T10:04:30.000Z",
    "serverConnectionId": "scn_01J5RJ8X4EXAMPLE",
    "capabilities": { "minProtocolVersion": 1, "maxProtocolVersion": 1, "maxPayloadBytes": 65536, "maxBatchSize": 50 }
  },
  "payload": {
    "kind": "heartbeat",
    "jellyfinItemId": "jf_server_01J5RJ8X4LOCAL",
    "watchoffitUserId": "system",
    "mediaKind": "movie",
    "queueDepth": 0,
    "lastSequence": 5,
    "pluginVersion": "1.0.0.0"
  }
}
```
```http
HTTP/1.1 200 OK
Content-Type: application/json
Cache-Control: no-store
```
```json
{
  "kind": "ack",
  "header": {
    "version": 1,
    "kind": "ack",
    "id": "ack_heartbeat_01J5RJ8X4L",
    "correlationId": "evt_heartbeat_01J5RJ8X4K",
    "sequence": 5,
    "timestamp": "2026-08-27T10:04:30.120Z",
    "serverConnectionId": "scn_01J5RJ8X4EXAMPLE"
  },
  "payload": {
    "commandId": "evt_heartbeat_01J5RJ8X4K",
    "status": "ok",
    "note": "heartbeat"
  }
}
```
The plugin uses a 10 second HTTP timeout. Transient failures retry with
exponential backoff from 5 seconds to 5 minutes, with jitter. `Retry-After`
wins. After 10 failed heartbeat attempts inside 1 hour, the plugin records the
latest failed envelope id and safe error code in local state, marks the
connection unhealthy, and stops tight retrying until the next scheduled
backoff window or manual retry.

## 4. C# classes
### 4.1 `Pairing/PairingState.cs`
Defines `None`, `Challenge`, `Handshake`, `Paired`, `Rotating`, and `Revoked`.
It mirrors the RFC state machine and is the only state enum used by the
pairing code.
### 4.2 `Pairing/WatchoffitConnection.cs`
DTO for `connection.json`: `serverConnectionId`, `baseUrl`, credential wrapper,
capabilities, Watchoffit server name, Jellyfin server id, `lastPingAt`,
`lastErrorCode`, timestamps, and schema `version`. It never includes the
one-time pairing code.
### 4.3 `Pairing/WatchoffitConnectionStore.cs`
Loads and saves under the plugin `dataFolder`. It creates the directory,
checks the top-level `version` before full deserialization, refuses unknown
future versions, and writes atomically through a temporary file plus replace.
It owns DPAPI protection on Windows and plain opaque storage on Linux.
### 4.4 `Pairing/WatchoffitClient.cs`
Typed HTTP client that builds v1 envelopes and calls all endpoints in the wire
trace. Every public method accepts a `CancellationToken`. It owns message ids,
sequence numbers, timestamp formatting, `Authorization` headers, response
parsing, retry classification, timeout mapping, and credential redaction.
### 4.5 `Pairing/WatchoffitConfigPage.cs`
Backs or replaces the placeholder `Configuration/watchoffitConfigPage.html`. It
renders not-paired and paired states, progress, and errors. Inputs are the
Watchoffit base URL, defaulting to `http://localhost:8096`, and the Watchoffit pairing
code. It uses Jellyfin dashboard CSS classes rather than custom CSS.
### 4.6 `Pairing/PairingService.cs`
Orchestrates `None -> Challenge -> Handshake -> Paired`, rotation, disconnect,
heartbeat start/stop, and restart rehydration. It is thread-safe because
Jellyfin can invoke page handlers concurrently. Use a private `SemaphoreSlim`
around state transitions and keep the current connection as an immutable
snapshot.
### 4.7 `Protocol/V1/*.cs`
Adds or completes the C# mirror of the TS v1 schema: envelope records, headers,
capabilities, command/event/ack/error payloads, JSON converters, parser result
types, and strict object validation. Pairing payload additions must be present
here and in `v1.ts` before `WatchoffitClient` is used.

## 5. On-disk schema
### 5.1 Path
```text
/config/plugins/Watchoffit/connection.json
```
Resolve this through Jellyfin's plugin data folder. Do not hard-code
`/config`.
### 5.2 JSON
```json
{
  "version": 1,
  "state": "paired",
  "baseUrl": "https://watchoffit.example.com",
  "serverConnectionId": "scn_01J5RJ8X4EXAMPLE",
  "watchoffitServerName": "Family Watchoffit",
  "jellyfinServerId": "jf_server_01J5RJ8X4LOCAL",
  "credential": {
    "scheme": "plain",
    "value": "cred_01J5RJ8X4SECRET"
  },
  "capabilities": { "minProtocolVersion": 1, "maxProtocolVersion": 1, "maxPayloadBytes": 65536, "maxBatchSize": 50 },
  "createdAt": "2026-08-27T10:02:01.000Z",
  "lastPingAt": "2026-08-27T10:04:30.000Z",
  "lastErrorCode": null,
  "lastErrorAt": null
}
```
### 5.3 Versioning
`WatchoffitConnectionStore` reads `version` first. Version `1` loads with the
schema above. Unknown versions are refused without partially loading
credentials, and the UI shows "Plugin update required". Future migrations read
old known versions, migrate in memory, then write the new file atomically.
### 5.4 Credential handling
On Windows, use DPAPI or the available OS keyring and store
`credential.scheme: "dpapi"`. On Linux, store the credential as-is with
`credential.scheme: "plain"`. The credential remains opaque to the plugin host;
it is the token Watchoffit issued, not a password. Never log or render the value.

## 6. Configuration page HTML
### 6.1 Not paired
```html
<div class="ConfigurationPage" data-role="page-ConfigurationPage">
  <div class="contentSection">
    <h2 class="sectionTitle">Watchoffit</h2>
    <form id="watchoffitPairingForm">
      <div class="inputContainer">
        <label class="inputLabel inputLabelUnfocused" for="watchoffitBaseUrl">Watchoffit server URL</label>
        <input is="emby-input" id="watchoffitBaseUrl" name="watchoffitBaseUrl" type="url" value="http://localhost:8096" required />
      </div>
      <div class="inputContainer">
        <label class="inputLabel inputLabelUnfocused" for="watchoffitPairingCode">Pairing code</label>
        <input is="emby-input" id="watchoffitPairingCode" name="watchoffitPairingCode" autocomplete="one-time-code" maxlength="16" required />
      </div>
      <button is="emby-button" type="submit" class="raised button-submit block">
        <span>Connect to Watchoffit</span>
      </button>
    </form>
    <div class="fieldDescription" id="watchoffitStatus">Not connected.</div>
  </div>
</div>
```
### 6.2 Paired
```html
<div class="ConfigurationPage" data-role="page-ConfigurationPage">
  <div class="contentSection">
    <h2 class="sectionTitle">Watchoffit</h2>
    <div class="paperList">
      <div class="listItem"><span class="listItemBodyText">Status: Connected</span></div>
      <div class="listItem"><span class="listItemBodyText">Server: Family Watchoffit</span></div>
      <div class="listItem"><span class="listItemBodyText">Connection id: scn_01J5RJ8X4EXAMPLE</span></div>
      <div class="listItem"><span class="listItemBodyText">Capabilities: protocol 1-1, payload 65536, batch 50</span></div>
      <div class="listItem"><span class="listItemBodyText">Last ping: 2026-08-27T10:04:30.000Z</span></div>
    </div>
    <button is="emby-button" type="button" class="raised" id="watchoffitRotateCredential">
      <span>Rotate credential</span>
    </button>
    <button is="emby-button" type="button" class="button-cancel" id="watchoffitDisconnect">
      <span>Disconnect</span>
    </button>
  </div>
</div>
```
The form posts back to a Jellyfin plugin page handler added in Phase 3
implementation. Keep the page compact; Jellyfin dashboard fragments should not
be 200-line forms.

## 7. Error states and retries
### 7.1 Watchoffit base URL unreachable
Show "Watchoffit server unreachable" and preserve the entered URL and code. Log
host, path, elapsed time, and timeout class. Do not log the code.
### 7.2 Auth rejected
On `401` or `AUTH_REQUIRED`, move to `Revoked`, stop heartbeat, and show
"Connection revoked or expired. Pair again." Do not send more requests with
that credential.
### 7.3 Code expired
Move from `Handshake` back to `Challenge` and show "Pairing code expired.
Generate a new code in Watchoffit."
### 7.4 Code already used
Return to `Challenge`, show "Pairing code already used", and do not retry the
same code unless Watchoffit returns a cached ack for the same envelope id.
### 7.5 Rate-limited
Honor `Retry-After`. Without it, retry from 5 seconds to 5 minutes with
jitter. Show "Watchoffit is rate-limiting requests. Retrying..." while automatic
retry is active.
### 7.6 Minimum Jellyfin version not met
If Watchoffit rejects the pre-check because Jellyfin is older than `10.11.0`, do
not continue. Show "Jellyfin 10.11.0 or newer is required."
### 7.7 Network blip during redeem
Reuse the same envelope id for retries of the same logical redeem attempt so
Watchoffit can deduplicate. Retry only for transport failures and retryable status
codes such as `503`. If Watchoffit reports that a different request already used
the code, ask for a new code.

## 8. State machine transitions
```mermaid
stateDiagram-v2
  [*] --> None: no connection.json
  None --> Challenge: system-info OK / challenge_request
  Challenge --> Handshake: redeem_request submitted
  Handshake --> Paired: redeem ack + credential persisted
  Handshake --> Challenge: code expired or already used
  Handshake --> None: user cancels
  Paired --> Rotating: rotate_credential submitted
  Rotating --> Paired: rotate ack persisted
  Rotating --> Paired: rotation failed, old credential valid
  Paired --> Revoked: DELETE credential ack
  Paired --> Revoked: AUTH_REQUIRED on ping
  Revoked --> None: local connection forgotten
  Revoked --> [*]: terminal remote credential state
```
`Revoked` is terminal for that credential on the Watchoffit side. The local plugin
can only move from `Revoked` to `None` by forgetting local state and pairing
again.

## 9. Telemetry and logging
Log every state transition with previous state, next state,
`serverConnectionId` when known, and a safe reason code.
Log every HTTP attempt with method, endpoint path, elapsed time, status code,
retry number, and retry class. Do not log request bodies for redeem, rotation
responses, or any envelope that carries a credential.
Log parse failures with the safe parser code and a short validation summary.
Raw JSON may be logged only at debug level after redaction.
Log one connection summary per day while paired: Watchoffit server name,
connection id, last successful ping, last error code, protocol range, and
heartbeat failure count.
No PII beyond what Watchoffit has already given the user is logged. Phase 3 does
not log Jellyfin user names, media titles, pairing codes, or credential values.

## 10. Test plan
Use xUnit and Moq when the C# test project lands. Do not write these tests in
Phase 3 design.
- `WatchoffitClient_SystemInfo_Success_ParsesChallengeEnvelope`
- `WatchoffitClient_SystemInfo_UnsupportedProtocol_ReturnsTerminalFailure`
- `WatchoffitClient_Challenge_Success_ReturnsServerConnectionId`
- `WatchoffitClient_Challenge_400_InvalidEnvelope_IsTerminal`
- `WatchoffitClient_Redeem_Success_ReturnsCredentialAndServerInfo`
- `WatchoffitClient_Redeem_CodeExpired_ReturnsChallengeState`
- `WatchoffitClient_Redeem_CodeAlreadyUsed_ReturnsTerminalAttemptFailure`
- `WatchoffitClient_Redeem_503_RetriesWithSameEnvelopeId`
- `WatchoffitClient_Redeem_Timeout_UsesCancellationToken`
- `WatchoffitClient_Rotate_Success_ReplacesCredentialAfterStoreWrite`
- `WatchoffitClient_Rotate_4xx_DoesNotOverwriteExistingCredential`
- `WatchoffitClient_Disconnect_Success_DeletesLocalConnection`
- `WatchoffitClient_Disconnect_Unreachable_AllowsLocalForgetPath`
- `WatchoffitClient_Ping_Success_UpdatesLastPingAt`
- `WatchoffitClient_Ping_401_MovesToRevoked`
- `WatchoffitClient_Ping_429_HonorsRetryAfter`
- `PairingService_NoneToChallengeToHandshakeToPaired_Success`
- `PairingService_ConcurrentConnect_OnlyOneHandshakeRuns`
- `PairingService_Startup_LoadsConnectionWithoutNetworkCall`
- `PairingService_Startup_DoesNotBlockBeyondFewHundredMilliseconds`
- `PairingService_CancellationDuringRedeem_LeavesNoCredentialInMemory`
- `WatchoffitConnectionStore_ReadWrite_RoundTripsVersion1`
- `WatchoffitConnectionStore_UnknownVersion_RefusesToLoad`
- `WatchoffitConnectionStore_CorruptJson_ReturnsRecoverableFailure`
- `WatchoffitConnectionStore_AtomicWrite_DoesNotReplaceGoodFileOnFailure`
- `WatchoffitConnectionStore_WindowsDpapi_ProtectsAndUnprotectsCredential`
- `V1Parser_RejectsUnknownEnvelopeFields`
- `V1Parser_RejectsUnknownPairingPayloadKind`
- `V1Parser_AcceptsChallengeRequestFixture`
- `V1Parser_AcceptsRedeemRequestFixture`

## 11. Open questions
### 11.1 Pairing code length
Question: should the Watchoffit code be exactly 6 alphanumeric characters or keep
the longer grouped code in the original RFC?
Recommendation: use 6 uppercase alphanumeric characters because the code is
single-use, has a 10 minute TTL, and is easier to type.
Smallest next step: update `protocol-v1.md` and fixtures so all examples use
the same code format.
### 11.2 Linux credential encryption
Question: should `connection.json` encrypt the credential at rest on Linux?
Recommendation: no for Phase 3. Store it as an opaque token with restrictive
file permissions because containerized Jellyfin deployments may not have a
reliable keyring.
Smallest next step: add a future hardening issue for optional Linux keyring
support.
### 11.3 Rotation UX
Question: should rotation auto-redeem or require the user to paste a new code?
Recommendation: auto-rotate while paired. The current credential authenticates
the rotation request, and the v1 rotate ack already returns `newCredential`.
Smallest next step: require manual paste only for revoked or unpaired states.
### 11.4 Ping payload uptime
Question: should `ping` include Jellyfin server uptime?
Recommendation: no for Phase 3. The heartbeat payload already carries
`queueDepth`, `lastSequence`, and `pluginVersion`; uptime is diagnostics, not
pairing correctness.
Smallest next step: add uptime later as an optional heartbeat field if
operators ask for it.
### 11.5 Pairing ack payload shape
Question: should challenge and redeem responses extend ack payloads or return
dedicated command envelopes?
Recommendation: extend ack payloads. Pairing is request-response, and success
or failure maps cleanly to `ack` and `error`.
Smallest next step: add `challenge_request`, `redeem_request`, and their ack
payloads to `v1.ts`, then add JSON acceptance fixtures before C# work starts.
Implementation sequence for Phase 3: commit 1 adds TS/C# pairing payload schema and fixtures so envelopes validate; commit 2 adds `WatchoffitConnection`, `WatchoffitConnectionStore`, and startup rehydration so Jellyfin remembers a paired connection; commit 3 adds `WatchoffitClient` and `PairingService` so the plugin can pre-check, challenge, redeem, rotate, disconnect, and ping; commit 4 replaces the placeholder dashboard page with the two-state UI so an administrator can connect, inspect, rotate, and disconnect from Jellyfin.

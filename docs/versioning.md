# Watchoffit Jellyfin Plugin — Versioning and Distribution

> **Status:** release plan for the first public plugin release.
> **Audience:** Jellyfin plugin C# maintainers, Watchoffit maintainers, and release
> managers.
> **Goal:** ship a custom Jellyfin plugin without breaking existing Watchoffit
> users when Jellyfin moves to a new server ABI or a new .NET target.

This document defines how plugin versions, Jellyfin compatibility, release
artifacts, checksums, catalog entries, and old-version retention work for the
Watchoffit Jellyfin plugin. It complements
[protocol-v1.md](./protocol-v1.md), which defines the wire protocol between
Watchoffit and the plugin.

Baseline for the first public release:

- Jellyfin line: `10.11+`.
- Jellyfin target ABI: `10.11.0.0`.
- .NET target framework: `net9.0`.
- Watchoffit protocol: `v1`.
- Plugin version: `1.0.0.0`.
- Development path in this monorepo: `plugins/jellyfin/`.
- Public plugin repository: `https://github.com/<ORG>/watchoffit-jellyfin-plugin`.

Jellyfin reference points: repository install docs
https://jellyfin.org/docs/general/server/plugins/, repository package fields
https://github.com/jellyfin/jellyfin/blob/master/MediaBrowser.Model/Updates/PackageInfo.cs,
repository version fields
https://github.com/jellyfin/jellyfin/blob/master/MediaBrowser.Model/Updates/VersionInfo.cs,
and installed plugin `meta.json` fields
https://github.com/jellyfin/jellyfin/blob/master/MediaBrowser.Common/Plugins/PluginManifest.cs.

## 1. Goals and non-goals

### 1.1 Goals

Versioning means four separate things here:

1. **Jellyfin compatibility.** A Jellyfin server should only see and install a
   ZIP that was built and tested for a compatible Jellyfin ABI.
2. **.NET compatibility.** A plugin assembly should target the same .NET
   generation as the Jellyfin server line it is built for.
3. **Watchoffit protocol compatibility.** The plugin should only pair with a Watchoffit
   server that supports its declared protocol range.
4. **Release identity.** A release tag should map to one artifact, one
   checksum, one changelog, one `targetAbi`, and one `framework`.

Rule of thumb:

> Publish one plugin version per `(Jellyfin ABI, .NET target)` pair, and list
> that version in the Jellyfin repository manifest with its own `targetAbi`,
> `sourceUrl`, `checksum`, and `timestamp`.

For v1.0:

```text
version:       1.0.0.0
targetAbi:     10.11.0.0
framework:     net9.0
protocol:      v1
```

### 1.2 Non-goal: one multi-target ZIP

We are not shipping a single ZIP with both `net9.0` and future `net11.0`
assemblies.

Reasons:

- Jellyfin loads concrete assemblies from the plugin directory; it does not
  route inside a ZIP by target framework.
- A wrong assembly choice fails at Jellyfin startup, after installation.
- Catalog routing is based on manifest entries, not runtime probing.
- Rollback is cleaner when one ABI line has one artifact.

The C# project may use conditional compilation while preparing a new line, but
the public release artifact remains one ZIP for one compatibility pair.

### 1.3 Non-goal: per-user-type plugin SKUs

We are not shipping separate plugins for admins, families, single-user
instances, managed servers, or advanced users.

Jellyfin identifies plugins by GUID. Splitting behavior into multiple plugin
identities would create duplicate catalog entries, duplicate configuration
screens, unclear migration behavior, and support overhead.

User-type differences belong in Watchoffit authorization, feature flags, plugin
configuration, or negotiated protocol capabilities.

The plugin binary remains one product: **Watchoffit**.

### 1.4 Non-goal: forward compatibility across .NET majors

We are not treating a `net9.0` plugin as forward-compatible with a future
Jellyfin server that runs on `.NET 11`.

When Jellyfin moves to a new .NET major, Watchoffit ships a new plugin release
compiled against that Jellyfin line and target framework. Existing users stay
on the old compatible catalog entry until they upgrade Jellyfin.

## 2. `meta.json` schema

### 2.1 Two JSON shapes

Jellyfin uses two related JSON shapes:

1. **Repository manifest.** This is the catalog JSON served by a repository URL
   such as `https://repo.jellyfin.org/files/plugin/manifest.json`. It is a
   top-level array of plugin packages. Each package has `versions[]`.
2. **Installed plugin `meta.json`.** This is the local manifest in the
   installed plugin directory. It describes the plugin Jellyfin loads from
   disk, including `imagePath` and `assemblies`.

The repository manifest is the update-routing source of truth. The installed
`meta.json` is the runtime-loading source of truth.

Important field distinction: repository manifests use `imageUrl`, installed
plugin manifests use `imagePath`, build metadata uses `framework`, and current
Jellyfin repository version entries do not define `framework` as a consumed
`versions[]` field in `VersionInfo`.

Because release managers still need the .NET target, every release records
`framework` in `build.yaml`, the CI matrix, the ZIP filename, and release notes.

### 2.2 Stable GUID

The plugin GUID is generated once and never changed:

```text
namespace: UUID namespace DNS, 6ba7b810-9dad-11d1-80b4-00c04fd430c8
name:      watchoffit.jellyfin-plugin
guid:      ed8e9c41-2e0f-5872-93f2-06feb1bc37d1
```

This is UUIDv5 over a stable namespace and name. The GUID must stay the same
for new plugin versions, new Jellyfin ABIs, new .NET targets, repository moves,
and maintainer handoffs.

### 2.3 Repository manifest for v1.0.0.0

This is the drop-in repository manifest for v1.0.0.0. If the public repository
serves only Watchoffit, this is the whole `manifest.json`. If it later hosts more
plugins, this object is one entry in the top-level array.

Replace `<ORG>`, the checksum, and the timestamp before publishing.

```json
[
  {
    "category": "General",
    "name": "Watchoffit",
    "description": "Connects Jellyfin to Watchoffit for watched state, resume progress, library inventory, backfill, reconciliation, and outbound watched or resume commands without the Webhook plugin, Jellyfin API keys, or Quick Connect.",
    "overview": "Sync Jellyfin watch activity and library state with Watchoffit.",
    "owner": "<ORG>",
    "guid": "ed8e9c41-2e0f-5872-93f2-06feb1bc37d1",
    "imageUrl": "https://raw.githubusercontent.com/<ORG>/watchoffit-jellyfin-plugin/main/assets/watchoffit.png",
    "versions": [
      {
        "version": "1.0.0.0",
        "changelog": "Initial public release. Adds Watchoffit pairing, encrypted installation credentials, authenticated outbound WebSocket transport with HTTPS long-poll fallback, playback and watched-state events, library inventory, initial backfill, reconciliation, outbound watched/unwatched and resume commands, durable at-least-once delivery, ACK/replay handling, deduplication, credential rotation, revocation, diagnostics, and Watchoffit protocol v1 support for Jellyfin 10.11.",
        "targetAbi": "10.11.0.0",
        "sourceUrl": "https://github.com/<ORG>/watchoffit-jellyfin-plugin/releases/download/v1.0.0.0/watchoffit-jellyfin_1.0.0.0_jellyfin-10.11_net9.0.zip",
        "checksum": "<MD5_HEX_OF_ZIP>",
        "timestamp": "2026-09-01T12:00:00Z"
      }
    ]
  }
]
```

`checksum` must be the MD5 hex digest of the exact ZIP bytes at `sourceUrl`,
because Jellyfin's installer currently validates repository package checksums
with MD5. Publish SHA-256 too, but do not put SHA-256 in the Jellyfin catalog
field unless Jellyfin changes the installer contract.

### 2.4 Installed `meta.json` for v1.0.0.0

The ZIP should include this local `meta.json`, or an equivalent generated file
with the same identity and loading fields.

```json
{
  "category": "General",
  "changelog": "Initial public release. Adds Watchoffit pairing, encrypted installation credentials, authenticated outbound WebSocket transport with HTTPS long-poll fallback, playback and watched-state events, library inventory, initial backfill, reconciliation, outbound watched/unwatched and resume commands, durable at-least-once delivery, ACK/replay handling, deduplication, credential rotation, revocation, diagnostics, and Watchoffit protocol v1 support for Jellyfin 10.11.",
  "description": "Connects Jellyfin to Watchoffit for watched state, resume progress, library inventory, backfill, reconciliation, and outbound watched or resume commands without the Webhook plugin, Jellyfin API keys, or Quick Connect.",
  "guid": "ed8e9c41-2e0f-5872-93f2-06feb1bc37d1",
  "name": "Watchoffit",
  "overview": "Sync Jellyfin watch activity and library state with Watchoffit.",
  "owner": "<ORG>",
  "targetAbi": "10.11.0.0",
  "timestamp": "2026-09-01T12:00:00Z",
  "version": "1.0.0.0",
  "status": "Active",
  "autoUpdate": true,
  "imagePath": "watchoffit.png",
  "assemblies": [
    "Jellyfin.Plugin.Watchoffit.dll"
  ]
}
```

ZIP layout:

```text
Jellyfin.Plugin.Watchoffit.dll
meta.json
watchoffit.png
```

Do not ship Jellyfin host assemblies such as `Jellyfin.Controller.dll`,
`Jellyfin.Model.dll`, `MediaBrowser.Common.dll`, or
`MediaBrowser.Controller.dll`.

### 2.5 Build metadata

Use `build.yaml` or equivalent release metadata in the public plugin
repository. This is where `framework` belongs.

```yaml
name: "Watchoffit"
guid: "ed8e9c41-2e0f-5872-93f2-06feb1bc37d1"
imageUrl: "https://raw.githubusercontent.com/<ORG>/watchoffit-jellyfin-plugin/main/assets/watchoffit.png"
version: "1.0.0.0"
targetAbi: "10.11.0.0"
framework: "net9.0"
owner: "<ORG>"
overview: "Sync Jellyfin watch activity and library state with Watchoffit."
description: >
  Connects Jellyfin to Watchoffit for watched state, resume progress, library
  inventory, backfill, reconciliation, and outbound watched or resume commands
  without the Webhook plugin, Jellyfin API keys, or Quick Connect.
category: "General"
artifacts:
  - "Jellyfin.Plugin.Watchoffit.dll"
changelog: |-
  Initial public release for Jellyfin 10.11 and Watchoffit protocol v1.
```

CI fails when `build.yaml`, the git tag, and the build matrix disagree.

## 3. Version-numbering policy

### 3.1 Format

Use four numeric parts:

```text
MAJOR.MINOR.PATCH.BUILD
```

Normal releases set `BUILD` to `0`. Use the fourth component only for an
emergency rebuild of identical source when Jellyfin needs a strictly higher
version to pick up a corrected ZIP or manifest entry.

### 3.2 Major bumps

Bump `MAJOR` for:

- Jellyfin ABI line changes.
- .NET target major changes.
- Breaking Watchoffit protocol changes.
- Unmigratable plugin configuration changes.
- Pairing or credential-scope changes that require operator action.

Examples:

- `10.11.0.0` / `net9.0` to future `10.12.0.0` / `net11.0`.
- Protocol v1 to protocol v2 when v2 cannot honor v1 envelopes.

### 3.3 Minor bumps

Bump `MINOR` for backward-compatible improvements:

- additive protocol fields,
- new optional command or event kinds,
- new diagnostics,
- better retry or queue visibility,
- support for a tested Jellyfin patch ABI floor with the same .NET target and
  protocol.

Minor releases must preserve existing configuration, credentials, durable queue
format, pairing state, and protocol minimum.

### 3.4 Patch bumps

Bump `PATCH` for:

- security fixes,
- crash fixes,
- retry, reconnect, timeout, or queue corruption fixes,
- Jellyfin security patch compatibility fixes inside the same ABI line,
- documentation or diagnostics text fixes bundled with code fixes.

Patch releases must not require re-pairing.

### 3.5 Worked examples

| Plugin version | Jellyfin ABI | Framework | Protocol | Reason |
| --- | --- | --- | --- | --- |
| `1.0.0.0` | `10.11.0.0` | `net9.0` | `v1` | First public release. |
| `1.1.0.0` | `10.11.0.0` | `net9.0` | `v1` | Adds optional diagnostics and queue metrics. |
| `1.1.1.0` | `10.11.0.0` | `net9.0` | `v1` | Security patch and log redaction. |
| `2.0.0.0` | `10.12.0.0` | `net11.0` | `v1` or `v2` | Future Jellyfin line moves to .NET 11. |

If Jellyfin `10.11.5` needs a plugin-specific compatibility fix, publish
`1.2.0.0` with `targetAbi` `10.11.5.0` and keep the previous `10.11.0.0`
entry for users on older `10.11.x` servers.

## 4. CI workflow

### 4.1 Tag to release

Release tags use:

```text
v<MAJOR>.<MINOR>.<PATCH>.<BUILD>
```

Example:

```text
v1.0.0.0
```

On tag push, GitHub Actions should:

1. Check out the public plugin repository.
2. Resolve `version` from the tag.
3. Read `build.yaml`.
4. Verify tag version equals `build.yaml` `version`.
5. Verify matrix `targetAbi` equals `build.yaml` `targetAbi`.
6. Verify matrix `framework` equals `build.yaml` `framework`.
7. Install the matching .NET SDK, initially `10.0.x`.
8. Restore with the pinned Jellyfin package version for the ABI.
9. Build `Jellyfin.Plugin.Watchoffit.sln` in `Release`.
10. Run unit tests.
11. Run compatibility tests against a containerized Jellyfin server for the
    target ABI.
12. Publish the plugin project to `artifacts/package/`.
13. Copy `meta.json` and `watchoffit.png` into `artifacts/package/`.
14. Zip the package directory.
15. Generate MD5 and SHA-256 checksums from the final ZIP.
16. Verify the ZIP contains `meta.json` and `Jellyfin.Plugin.Watchoffit.dll`.
17. Verify the ZIP does not contain Jellyfin host assemblies.
18. Upload the ZIP and checksum files to the GitHub Release.

### 4.2 Workflow shape

The GitHub Actions workflow should be a tag-triggered `release` workflow with
`contents: write`, `actions/checkout`, `actions/setup-dotnet`, one matrix row
per supported `(targetAbi, framework)` pair, and upload through a release action
such as `softprops/action-gh-release`.

Initial matrix row:

```yaml
- jellyfin: "10.11.0"
  targetAbi: "10.11.0.0"
  framework: "net9.0"
  dotnet: "10.0.x"
```

Use placeholders for organization, repository, and optional secret names until
the public repo exists. Do not invent production secrets in scripts or docs.

### 4.3 Artifact layout

GitHub Release:

```text
watchoffit-jellyfin_1.0.0.0_jellyfin-10.11_net9.0.zip
checksums.md5
checksums.sha256
```

ZIP:

```text
Jellyfin.Plugin.Watchoffit.dll
meta.json
watchoffit.png
<approved direct runtime dependency>.dll
```

Exclude Jellyfin host assemblies, `*.deps.json`, `*.runtimeconfig.json`, and
`*.pdb` from the install ZIP. Debug symbols may be uploaded as separate release
artifacts.

### 4.4 Manifest updates

For v1.0, manually update `manifest.json` in the public plugin repository.
There is one compatibility pair, and the first public catalog text needs human
review.

Manual update fields:

- `versions[0].version`,
- `versions[0].changelog`,
- `versions[0].targetAbi`,
- `versions[0].sourceUrl`,
- `versions[0].checksum`,
- `versions[0].timestamp`.

After three successful stable releases, automate this with:

```text
scripts/update-manifest-from-release.ts
```

The script should read `build.yaml`, read the tag, read `checksums.md5`,
construct `sourceUrl`, prepend a new `versions[]` entry, retain supported old
entries, and validate the final JSON.

### 4.5 Checksums

Generate checksums from the final ZIP bytes:

```bash
cd artifacts/dist
md5sum watchoffit-jellyfin_1.0.0.0_jellyfin-10.11_net9.0.zip > checksums.md5
sha256sum watchoffit-jellyfin_1.0.0.0_jellyfin-10.11_net9.0.zip > checksums.sha256
```

Use the MD5 value in Jellyfin's `checksum` field. Publish SHA-256 in release
notes and `checksums.sha256`.

Never recompute the manifest checksum before a later upload, signing step, or
ZIP repack. The checksum is over the exact bytes at `sourceUrl`.

## 5. Central catalog submission

### 5.1 PR target

Submit the first public catalog entry to:

```text
https://github.com/jellyfin/jellyfin-plugin-repo
```

The PR should make Watchoffit appear in the stable manifest served to Jellyfin
servers:

```text
https://repo.jellyfin.org/files/plugin/manifest.json
```

Some Jellyfin docs and infrastructure references use `manifests.jellyfin.org`
for served manifests. Treat `repo.jellyfin.org/files/plugin/manifest.json` as
the user-facing stable repository URL unless the central repository maintainers
say otherwise.

### 5.2 File to add

Before opening the PR, inspect the current central repository structure and copy
the convention used by a recently updated official plugin.

If the repository expects build metadata, add the current equivalent of:

```text
build.yaml
```

with the fields from section 2.5.

If the repository expects direct manifest JSON, add the package object from
section 2.3 to its manifest source.

Do not submit both formats unless maintainers request both.

### 5.3 Manifest URL and source URL

The catalog metadata must point to public, stable resources:

```text
source repository: https://github.com/<ORG>/watchoffit-jellyfin-plugin
release page:      https://github.com/<ORG>/watchoffit-jellyfin-plugin/releases/tag/v1.0.0.0
sourceUrl:         https://github.com/<ORG>/watchoffit-jellyfin-plugin/releases/download/v1.0.0.0/watchoffit-jellyfin_1.0.0.0_jellyfin-10.11_net9.0.zip
```

Do not point Jellyfin at a local Watchoffit instance, a private repository, a
mutable CDN URL, or a force-pushed branch.

### 5.4 Review expectations

Expect Jellyfin maintainers to review:

- valid JSON or build metadata,
- unique stable GUID,
- reachable public source repository,
- license,
- category and user-facing copy,
- valid `targetAbi`,
- reachable ZIP `sourceUrl`,
- checksum match,
- no bundled host assemblies,
- no obvious malware or unsafe arbitrary execution behavior.

The PR description should include:

```text
Plugin: Watchoffit
Source: https://github.com/<ORG>/watchoffit-jellyfin-plugin
Release: https://github.com/<ORG>/watchoffit-jellyfin-plugin/releases/tag/v1.0.0.0
Target ABI: 10.11.0.0
Framework: net9.0
License: <LICENSE>
Watchoffit protocol: v1
Security model: outbound authenticated channel, no arbitrary webhooks
```

### 5.5 List before public announcement

We must list before announcing v1.0.0.0 publicly.

Release order:

1. Publish the public source repository.
2. Add license and release docs.
3. Tag and publish `v1.0.0.0` with ZIP and checksums.
4. Open the Jellyfin central repository PR.
5. Wait for merge and served manifest update.
6. Install from a clean Jellyfin `10.11` server using the catalog.
7. Announce the release.

Until step 6 passes, the release is not public.

## 6. Multi-version coexistence

### 6.1 Same Jellyfin line

Keep multiple compatible entries in `versions[]` when Jellyfin patch releases
need different plugin builds.

Example:

| Version | targetAbi | framework | Purpose |
| --- | --- | --- | --- |
| `1.1.0.0` | `10.11.5.0` | `net9.0` | Fixes behavior specific to Jellyfin 10.11.5+. |
| `1.0.0.0` | `10.11.0.0` | `net9.0` | Keeps Jellyfin 10.11.0 users installable. |

`targetAbi` is the routing field. Users on a lower compatible Jellyfin patch
line should continue to see the entry built for their floor; users on a newer
patch line can receive the newer entry after compatibility tests pass.

### 6.2 Future .NET 11 line

When Jellyfin ships a .NET 11 server line, publish a new major and keep the old
major:

| Version | targetAbi | framework | Purpose |
| --- | --- | --- | --- |
| `2.0.0.0` | `10.12.0.0` | `net11.0` | Future Jellyfin line on .NET 11. |
| `1.1.1.0` | `10.11.0.0` | `net9.0` | Existing Jellyfin 10.11 / .NET 10 users. |

Do not remove the `1.x` catalog entry when publishing `2.0.0.0`. Users upgrade
Jellyfin on their own schedules, and Watchoffit must preserve sync for existing
servers.

### 6.3 Target ABI discipline

`targetAbi` must be the lowest Jellyfin ABI the ZIP was built and tested
against.

Rules:

- Do not set `targetAbi` lower than the Jellyfin packages used to compile.
- Do not set `targetAbi` lower than the oldest container tested in CI.
- Do not reuse the same plugin version for a different `targetAbi`.
- Do not assume a newer Jellyfin patch is safe until the compatibility job
  passes.

If a build compiled against Jellyfin `10.11.5` declares `targetAbi`
`10.11.0.0`, users on `10.11.0` may install code that calls APIs missing from
their server. That is release-blocking.

## 7. Deprecation / sunset policy

### 7.1 Retention window

Keep old catalog entries for the longer of:

- 18 months after the replacement major plugin line is published,
- 12 months after Jellyfin upstream stops supporting that server line, when
  upstream support status is clear.

Example: if Watchoffit publishes `2.0.0.0` for a future .NET 11 Jellyfin line on
`2027-03-01`, keep the last `1.x` release in the catalog until at least
`2028-09-01`.

### 7.2 Deprecation notice

Mark an old line deprecated in `changelog` only when:

- the replacement major has been public for at least 90 days,
- the old line is security-fix-only,
- Watchoffit still accepts the old protocol or has a documented upgrade path,
- install and upgrade docs exist for the replacement line.

Use explicit dates:

```text
Deprecated: Jellyfin 10.11 / .NET 10 support enters security-fix-only mode on 2027-06-01. This release remains installable for existing Jellyfin 10.11 servers until at least 2028-09-01.
```

### 7.3 Catalog removal

Remove an old version only when:

- the retention window has elapsed,
- the line cannot reasonably be tested or has an unfixed security issue,
- Watchoffit admin warnings have shipped for at least 90 days,
- release notes announced the removal date,
- the final ZIP remains archived on GitHub Releases.

Emergency removal for malware or credential compromise bypasses the normal
window. In that case, remove the catalog entry immediately, revoke affected
credentials where possible, and publish a security advisory.

## 8. Security & integrity

### 8.1 Checksums

`checksum` protects Jellyfin installs from corrupted downloads, accidental
artifact replacement, stale proxy content, and manifest/ZIP mismatches.

It is not a complete trust model. The manifest source, release tag, release
permissions, and review process still matter.

### 8.2 GitHub Releases

Use GitHub Releases for ZIP hosting because they provide source-linked release
history, stable HTTPS download URLs, public audit trail, and simple checksum
publication.

Release rules:

- protect `main`,
- restrict release publishing,
- require CI before tagging,
- prefer signed tags,
- never overwrite an existing ZIP in place,
- publish a higher plugin version when bytes need to change.

### 8.3 No Watchoffit CDN for ZIPs

Do not host stable catalog ZIPs on a Watchoffit CDN.

Reasons:

- a CDN adds a mutable release surface,
- cache invalidation can serve bytes that fail checksum validation,
- CDN access control and logging become part of the release threat model,
- GitHub Releases already match Jellyfin's expected distribution shape.

The Watchoffit website may link to GitHub Releases, but GitHub Releases remain the
canonical ZIP host.

### 8.4 Supply-chain constraints

NuGet rules:

- use only `https://api.nuget.org/v3/index.json` unless a human approves an
  exception,
- do not add random feeds in CI,
- pin Jellyfin package versions centrally,
- review every new `dotnet add package`,
- prefer BCL and Jellyfin APIs over dependencies.

ZIP rules:

- include only plugin DLL, `meta.json`, image, and approved runtime
  dependencies,
- exclude host assemblies,
- exclude test tools and packaging tools,
- exclude secrets, `.env` files, sample credentials, and CI tokens.

Integrity rules:

- publish MD5 for Jellyfin catalog validation,
- publish SHA-256 for humans and maintainers,
- decide before v1.0 whether to add Sigstore attestations.

## 9. Pre-1.0 policy

### 9.1 No public v0.x stable line

Watchoffit should not publish a public `0.x` plugin in the stable Jellyfin catalog.

The first stable catalog release is `1.0.0.0` because the plugin replaces the
existing Jellyfin integration path, stores security-sensitive credentials, and
uses a frozen protocol v1 RFC.

### 9.2 Preview channel

Preview builds, if needed, use a separate preview manifest:

```text
https://raw.githubusercontent.com/<ORG>/watchoffit-jellyfin-plugin/main/manifest-preview.json
```

Preview rules:

- maximum 10 external Jellyfin servers,
- maximum 30 days,
- numeric versions only, such as `1.0.0.0`,
- no `-beta.1` suffix in `version`, because Jellyfin parses versions as
  `System.Version`,
- preview wording goes in `changelog` and release notes,
- no automatic migration promise from preview builds,
- preview users may need to uninstall, delete plugin configuration, and re-pair.

Stable users must not be upgraded to preview builds automatically.

## 10. Open questions

### 10.0 Framework baseline (resolved)

> **Resolved:** Jellyfin 10.11.x runs on `net9.0` (not `net10.0` as the
> earlier draft of this document assumed). When Jellyfin ships a 10.12 line
> on `net10.0`, we will publish a major plugin release with
> `framework: net10.0`.

### 10.1 Artifact signing

Question: signed Git tags only, or signed tags plus Sigstore attestations?

Recommendation: use signed Git tags for v1.0 and add Sigstore after the first
stable release.

Smallest next step: choose the release key owner and document the tag-signing
command in the public plugin repository.

### 10.2 Plugin license

Question: which license applies to the public plugin repository?

Recommendation: GPL-3.0, matching the backlog decision.

Smallest next step: add `LICENSE` with GPL-3.0 text before the catalog PR.

### 10.3 Stable release path

Resolved: publish the first public release as `1.0.0.0` from the Watchoffit
GitHub namespace.

Smallest next step: tag `v1.0.0.0`, let release CI build the ZIP and checksums,
then verify installation from the generated `manifest.json`.

### 10.4 Central repository path

Question: what exact file path does `jellyfin/jellyfin-plugin-repo` require
when Watchoffit submits?

Recommendation: follow the current repository README and mirror a recently
updated official plugin.

Smallest next step: inspect the central repository immediately before opening
the PR.

### 10.5 `framework` in `versions[]`

Question: should Watchoffit put `framework` inside served manifest `versions[]`?

Recommendation: no, unless Jellyfin adds it to `VersionInfo` or the central
repository validator requires it. Keep `framework` in `build.yaml`, CI matrix,
artifact filename, and release notes.

Smallest next step: verify the central repository validator during the PR.

### 10.6 `imagePath` versus `imageUrl`

Question: should the catalog manifest use `imagePath` or `imageUrl`?

Recommendation: use `imageUrl` in the repository manifest and `imagePath` in
installed `meta.json`.

Smallest next step: validate clean catalog install on Jellyfin `10.11`.

### 10.7 Protocol field naming

Question: the backlog says `protocolVersion`, `messageId`, and `serverId`,
while the RFC uses `header.version`, `header.id`, and
`header.serverConnectionId`. Which is authoritative?

Recommendation: the frozen RFC is authoritative. Treat the backlog wording as
pre-RFC shorthand.

Smallest next step: update the backlog in a separate task if it remains
executable work text.

### 10.8 First release timestamp

Question: what timestamp should v1.0.0.0 use?
Recommendation: use the actual UTC build timestamp from CI. The examples use
`2026-09-01T12:00:00Z` only as a placeholder.

Smallest next step: make the packaging job write one timestamp and reuse it in
`meta.json`, release notes, and `manifest.json`.
Tomorrow morning, the release manager can create the public GPL-3.0 plugin
repository, copy in the v1.0.0.0 `build.yaml`, tag `v1.0.0.0`, let CI build
the `net9.0` / `10.11.0.0` ZIP, paste the resulting MD5 into `manifest.json`,
open the `jellyfin/jellyfin-plugin-repo` PR, and wait to announce until a clean
Jellyfin `10.11` server installs Watchoffit from the served catalog.

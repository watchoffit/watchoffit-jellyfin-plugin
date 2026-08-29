# Watchoffit Jellyfin Plugin — Compatibility

> **Status:** compatibility decision for the first public plugin release.
> **Audience:** Jellyfin plugin C# maintainers, Watchoffit maintainers, and release
> managers.
> **Goal:** make the Jellyfin server ABI and .NET target explicit before
> publishing the v1.0.0.0 plugin artifact.

This document answers one narrow release question:

> Which Jellyfin server line can load the Watchoffit plugin, and which .NET target
> should the plugin assembly use?

Short answer:

```text
Jellyfin 10.11.x server runs on net9.0.
```

Citation: Jellyfin `v10.11.11`,
`Jellyfin.Server/Jellyfin.Server.csproj`, `TargetFramework` is `net9.0`:
https://raw.githubusercontent.com/jellyfin/jellyfin/v10.11.11/Jellyfin.Server/Jellyfin.Server.csproj.

The default branch currently targets `net10.0`, but that is not the released
10.11 server line:
https://raw.githubusercontent.com/jellyfin/jellyfin/master/Jellyfin.Server/Jellyfin.Server.csproj.

## 1. Baseline and decision

The first public Watchoffit Jellyfin plugin release should target:

```text
minimum Jellyfin version: 10.11.0
Jellyfin target ABI:      10.11.0.0
plugin TargetFramework:   net9.0
controller package:       Jellyfin.Controller 10.11.11
plugin version:           1.0.0.0
protocol version:         v1
```

The decision is to support the Jellyfin 10.11 line first, not the unreleased
or preview 12.0 line.

The reason is runtime loading, not source compatibility. Jellyfin loads plugin
assemblies into the server process. A plugin compiled for a newer .NET target
than the host process cannot be loaded by that host. Jellyfin 10.11.x is a
`net9.0` server line, so the Watchoffit 10.11 plugin artifact must also be
compatible with `net9.0`.

The decisive source is the server project file in the released Jellyfin tags,
not the README on the default branch and not the NuGet page for a package in
isolation.

Primary citations:

- Jellyfin `v10.11.0`,
  `Jellyfin.Server/Jellyfin.Server.csproj`, `TargetFramework` is `net9.0`:
  https://raw.githubusercontent.com/jellyfin/jellyfin/v10.11.0/Jellyfin.Server/Jellyfin.Server.csproj.
- Jellyfin `v10.11.11`,
  `Jellyfin.Server/Jellyfin.Server.csproj`, `TargetFramework` is `net9.0`:
  https://raw.githubusercontent.com/jellyfin/jellyfin/v10.11.11/Jellyfin.Server/Jellyfin.Server.csproj.
- Jellyfin default branch,
  `Jellyfin.Server/Jellyfin.Server.csproj`, `TargetFramework` is `net10.0`:
  https://raw.githubusercontent.com/jellyfin/jellyfin/master/Jellyfin.Server/Jellyfin.Server.csproj.
- Jellyfin `v12.0-rc5`,
  `Jellyfin.Server/Jellyfin.Server.csproj`, `TargetFramework` is `net10.0`:
  https://raw.githubusercontent.com/jellyfin/jellyfin/v12.0-rc5/Jellyfin.Server/Jellyfin.Server.csproj.
- Jellyfin 10.11.11 release notes:
  https://github.com/jellyfin/jellyfin/releases/tag/v10.11.11.
- Jellyfin installation docs:
  https://jellyfin.org/docs/general/installation/.
- Jellyfin advanced manual install docs, which note that portable .NET DLL
  builds are loaded with `dotnet`:
  https://jellyfin.org/docs/general/installation/advanced/manual/.

No boundary was found inside 10.11.x where the server moved from `net8.0` to
`net9.0` or from `net9.0` to `net10.0`. The checked 10.11.0 and 10.11.11 tags
both target `net9.0`, and the current `Jellyfin.Controller` 10.11 packages are
also published for `net9.0`.

The latest stable release found during this check is Jellyfin 10.11.11,
published June 6, 2026. Jellyfin 12.0 is present as release candidates, not as
a stable 12.0.0 release in the checked release listing.

The official installation page does not present a separate framework matrix
for normal package or container installs. It points users to official binary
packages, containers, and portable builds. For plugin ABI purposes, the
authoritative runtime target remains the released server project file. For the
portable .NET DLL build, the install docs state that `jellyfin.dll` is loaded
with `dotnet`; for 10.11.x that implies a .NET 9 runtime because the server
assembly targets `net9.0`.

## 2. Jellyfin server ↔ .NET runtime matrix

This matrix is for release routing. Use it to decide which plugin artifact is
eligible for a Jellyfin server line.

```text
| Jellyfin version | Server target | Controller package target | Jellyfin.Controller NuGet version |
| --- | --- | --- | --- |
| 10.11.0 | net9.0 | net9.0 | 10.11.0 |
| 10.11.1 | net9.0 | net9.0 | 10.11.1 |
| 10.11.2 | net9.0 | net9.0 | 10.11.2 |
| 10.11.3 | net9.0 | net9.0 | 10.11.3 |
| 10.11.4 | net9.0 | net9.0 | 10.11.4 |
| 10.11.5 | net9.0 | net9.0 | 10.11.5 |
| 10.11.6 | net9.0 | net9.0 | 10.11.6 |
| 10.11.7 | net9.0 | net9.0 | 10.11.7 |
| 10.11.8 | net9.0 | net9.0 | 10.11.8 |
| 10.11.9 | net9.0 | net9.0 | 10.11.9 |
| 10.11.10 | net9.0 | net9.0 | 10.11.10 |
| 10.11.11 | net9.0 | net9.0 | 10.11.11 |
| 10.11.12 | not released at time of check | not released at time of check | none found |
| 10.12.0 | no stable release found; unstable/pre-release line only | no stable package found | none found as stable |
| 12.0-rc5 | net10.0 | net10.0 | 12.0.0-rc5 |
| default branch | net10.0 | net10.0 expected for future packages | pre-release / development |
```

Row citations:

- 10.11.0: tag `v10.11.0`,
  `Jellyfin.Server/Jellyfin.Server.csproj`:
  https://raw.githubusercontent.com/jellyfin/jellyfin/v10.11.0/Jellyfin.Server/Jellyfin.Server.csproj.
- 10.11.1: tag `v10.11.1`,
  `Jellyfin.Server/Jellyfin.Server.csproj`:
  https://raw.githubusercontent.com/jellyfin/jellyfin/v10.11.1/Jellyfin.Server/Jellyfin.Server.csproj.
- 10.11.2: tag `v10.11.2`,
  `Jellyfin.Server/Jellyfin.Server.csproj`:
  https://raw.githubusercontent.com/jellyfin/jellyfin/v10.11.2/Jellyfin.Server/Jellyfin.Server.csproj.
- 10.11.3: tag `v10.11.3`,
  `Jellyfin.Server/Jellyfin.Server.csproj`:
  https://raw.githubusercontent.com/jellyfin/jellyfin/v10.11.3/Jellyfin.Server/Jellyfin.Server.csproj.
- 10.11.4: tag `v10.11.4`,
  `Jellyfin.Server/Jellyfin.Server.csproj`:
  https://raw.githubusercontent.com/jellyfin/jellyfin/v10.11.4/Jellyfin.Server/Jellyfin.Server.csproj.
- 10.11.5: tag `v10.11.5`, release short commit `1e27f46`,
  `Jellyfin.Server/Jellyfin.Server.csproj`:
  https://raw.githubusercontent.com/jellyfin/jellyfin/v10.11.5/Jellyfin.Server/Jellyfin.Server.csproj.
- 10.11.6: tag `v10.11.6`, release short commit `10662e7`,
  `Jellyfin.Server/Jellyfin.Server.csproj`:
  https://raw.githubusercontent.com/jellyfin/jellyfin/v10.11.6/Jellyfin.Server/Jellyfin.Server.csproj.
- 10.11.7: tag `v10.11.7`, release short commit `b2aa80c`,
  `Jellyfin.Server/Jellyfin.Server.csproj`:
  https://raw.githubusercontent.com/jellyfin/jellyfin/v10.11.7/Jellyfin.Server/Jellyfin.Server.csproj.
- 10.11.8: tag `v10.11.8`, release short commit `2c62d40`,
  `Jellyfin.Server/Jellyfin.Server.csproj`:
  https://raw.githubusercontent.com/jellyfin/jellyfin/v10.11.8/Jellyfin.Server/Jellyfin.Server.csproj.
- 10.11.9: tag `v10.11.9`, release short commit `e83a7e6`,
  `Jellyfin.Server/Jellyfin.Server.csproj`:
  https://raw.githubusercontent.com/jellyfin/jellyfin/v10.11.9/Jellyfin.Server/Jellyfin.Server.csproj.
- 10.11.10: tag `v10.11.10`, release short commit `4b4b4cd`,
  `Jellyfin.Server/Jellyfin.Server.csproj`:
  https://raw.githubusercontent.com/jellyfin/jellyfin/v10.11.10/Jellyfin.Server/Jellyfin.Server.csproj.
- 10.11.11: tag `v10.11.11`, release short commit `1fbd873`,
  `Jellyfin.Server/Jellyfin.Server.csproj`:
  https://raw.githubusercontent.com/jellyfin/jellyfin/v10.11.11/Jellyfin.Server/Jellyfin.Server.csproj.
- 10.11 package target: NuGet `Jellyfin.Controller 10.11.11` targets
  `.NET 9.0`:
  https://www.nuget.org/packages/Jellyfin.Controller/10.11.11.
- 10.11.0 package target: NuGet `Jellyfin.Controller 10.11.0` targets
  `.NET 9.0`:
  https://www.nuget.org/packages/Jellyfin.Controller/10.11.0.
- 12.0-rc5: tag `v12.0-rc5`,
  `Jellyfin.Server/Jellyfin.Server.csproj`:
  https://raw.githubusercontent.com/jellyfin/jellyfin/v12.0-rc5/Jellyfin.Server/Jellyfin.Server.csproj.
- 12.0-rc5 package target: NuGet `Jellyfin.Controller 12.0.0-rc5`
  targets `.NET 10.0`:
  https://www.nuget.org/packages/Jellyfin.Controller/12.0.0-rc5.

The 10.11.1 through 10.11.9 rows are included for release-manager clarity.
The highest-confidence file reads during this audit were 10.11.0, 10.11.8,
10.11.10, and 10.11.11; each showed `net9.0`. The release list and NuGet
version list show no stable 10.11.12 and no stable 10.12.0 package at the time
of this check.

## 3. Plugin ↔ Jellyfin compatibility matrix

This matrix answers the install/load question for the next maintainer. It is
about CLR compatibility, not the Watchoffit protocol version.

```text
| Plugin target | Jellyfin version it can be installed on | Reason |
| --- | --- | --- |
| net8.0 | Not suitable for Jellyfin 10.11.x when built against Jellyfin.Controller 10.11.x | The 10.11 controller package is published for net9.0, so a net8.0 plugin cannot reference the 10.11 package directly. A net8.0 plugin belongs to older Jellyfin lines such as 10.9/10.10 only if built against their matching packages. |
| net9.0 | Jellyfin 10.11.0 through 10.11.11 | Jellyfin 10.11.x server targets net9.0, and Jellyfin.Controller 10.11.x targets net9.0. This is the correct target for the first 10.11-compatible Watchoffit artifact. |
| net10.0 | Jellyfin 12.0 release candidates and future stable line that actually targets net10.0 | A net10.0 plugin is too new for a net9.0 Jellyfin 10.11 host. It should be reserved for the Jellyfin 12.0/net10 line after that line is stable and tested. |
```

The practical rule:

```text
Build the plugin for the same .NET generation as the Jellyfin server line it
will be loaded into.
```

For the first Watchoffit release, that means:

```text
TargetFramework=net9.0
PackageReference Include="Jellyfin.Controller" Version="10.11.11"
PackageReference Include="Jellyfin.Model" Version="10.11.11"
targetAbi=10.11.0.0
```

When Jellyfin 12.0 becomes stable, publish a separate artifact:

```text
TargetFramework=net10.0
PackageReference Include="Jellyfin.Controller" Version="<stable 12 package>"
PackageReference Include="Jellyfin.Model" Version="<stable 12 package>"
targetAbi=<stable 12 ABI>
```

Do not ship a `net10.0` assembly as the Jellyfin 10.11 artifact.

## 4. Why the scaffold was first written with `net10.0`

The scaffold was initially written with `net10.0` because the Jellyfin main
repository README and default branch currently mention the .NET 10 SDK and the
server project on the default branch targets `net10.0`.

That is correct for current Jellyfin development and the 12.0 release
candidate line. It is not correct for the stable Jellyfin 10.11 line.

The released Jellyfin 10.11 server tags target `net9.0`, and the
`Jellyfin.Controller` 10.11 packages target `net9.0`. A `net10.0` Watchoffit
plugin may compile on a machine with the .NET 10 SDK, but it is not the right
artifact for Jellyfin 10.11.

Recommendation:

```text
Downgrade the 10.11 scaffold target from net10.0 to net9.0 now.
```

Smallest change:

```text
plugins/jellyfin/Jellyfin.Plugin.Watchoffit/Watchoffit.Plugin.csproj:
  <TargetFramework>net9.0</TargetFramework>

plugins/jellyfin/build.yaml:
  framework: "net9.0"

docs/versioning.md:
  replace the first-release framework baseline with net9.0
```

Those edits are intentionally not made by this document because this task only
allows creating `compat.md`.

Do not keep `net10.0` and bump the Jellyfin minimum for v1.0.0.0 unless the
product decision changes to skip Jellyfin 10.11 entirely. That would postpone
the plugin until the stable Jellyfin 12.0/net10 line is available and tested.

Do not multi-target the release ZIP as the first fix. Multi-targeting the C#
project can be useful during development, but Jellyfin installs and loads a
specific plugin assembly. The release artifact should remain one ZIP for one
Jellyfin ABI and one .NET target.

The future shape should be two catalog entries over time, not one ambiguous
binary:

```text
Watchoffit 1.0.0.0 -> Jellyfin 10.11 ABI -> net9.0
Watchoffit 2.0.0.0 or next ABI release -> Jellyfin 12 ABI -> net10.0
```

## 5. Build smoke test

Run the build from the plugin root:

```bash
dotnet build Jellyfin.Plugin.Watchoffit.sln -c Release
```

The expected framework is `net9.0`.

Expected successful output shape:

```text
Determining projects to restore...
Restored .../Jellyfin.Plugin.Watchoffit/Watchoffit.Plugin.csproj
Watchoffit.Plugin -> .../Jellyfin.Plugin.Watchoffit/bin/Release/net9.0/Jellyfin.Plugin.Watchoffit.dll

Build succeeded.
    0 Warning(s)
    0 Error(s)
```

If the scaffold has not yet been corrected, the output path will instead be:

```text
.../Jellyfin.Plugin.Watchoffit/bin/Release/net10.0/Jellyfin.Plugin.Watchoffit.dll
```

That is a successful compile, but it is not a successful Jellyfin 10.11
compatibility result.

Confirm the assembly target by inspecting the generated `.deps.json`:

```bash
rg -n '"target":|Jellyfin.Plugin.Watchoffit' \
  Jellyfin.Plugin.Watchoffit/bin/Release/net9.0/Jellyfin.Plugin.Watchoffit.deps.json
```

Expected target after the fix:

```text
"target": ".NETCoreApp,Version=v9.0"
```

Alternative check with SDK tooling:

```bash
dotnet --info
dotnet exec --depsfile \
  Jellyfin.Plugin.Watchoffit/bin/Release/net9.0/Jellyfin.Plugin.Watchoffit.deps.json \
  --additionalprobingpath ~/.nuget/packages \
  Jellyfin.Plugin.Watchoffit/bin/Release/net9.0/Jellyfin.Plugin.Watchoffit.dll
```

The second command is not expected to run the plugin as an application; the
plugin is a library. Use the `.deps.json` target as the framework assertion.

Release packaging should include only the plugin payload Jellyfin needs:

```text
Jellyfin.Plugin.Watchoffit.dll
meta.json
watchoffit.png
```

Do not include Jellyfin host assemblies copied from NuGet packages.

Do not treat a build as release-ready until the artifact framework, catalog
entry, installed `meta.json`, and ZIP filename all agree on the same
compatibility pair:

```text
Jellyfin 10.11 ABI / net9.0
```

## 6. Runtime smoke test (deferred)

The real install test is deferred until the scaffold target is changed from
`net10.0` to `net9.0`.

Use the current stable Jellyfin container for the 10.11 line:

```bash
docker run --rm \
  --name watchoffit-jellyfin-compat \
  -p 8096:8096 \
  -v "$PWD/tmp/jellyfin-config:/config" \
  -v "$PWD/tmp/jellyfin-cache:/cache" \
  jellyfin/jellyfin:10.11.11
```

Then install the plugin ZIP manually:

```text
/config/plugins/Watchoffit/Jellyfin.Plugin.Watchoffit.dll
/config/plugins/Watchoffit/meta.json
/config/plugins/Watchoffit/watchoffit.png
```

Restart Jellyfin.

Expected runtime result:

```text
Jellyfin starts without plugin load errors.
Dashboard -> Plugins lists "Watchoffit".
The Watchoffit configuration page opens.
The page renders the private-beta pairing placeholder.
```

Expected negative result for the current `net10.0` scaffold:

```text
Jellyfin 10.11.11 should reject or fail to load the plugin assembly because
the host server targets net9.0 and the plugin assembly targets net10.0.
```

This test cannot be considered valid for the 10.11 release until the runtime
decision in section 4 is applied.

## 7. Risks and unknowns

The following items remain release risks.

```text
| Risk or unknown | Current answer | Source / next check |
| --- | --- | --- |
| When does the stable Jellyfin 12 line ship? | Not confirmed in this audit. Release candidates exist, including v12.0-rc5, but no stable 12.0.0 release was found in the checked stable release listing. | Check https://github.com/jellyfin/jellyfin/releases before every plugin release. |
| Is there a confirmed .NET 10 server target for Jellyfin 12? | Yes for v12.0-rc5 and the default branch; not yet confirmed for a stable 12.0.0 tag because stable 12.0.0 was not found. | v12.0-rc5 server csproj and default branch server csproj. |
| What is the upgrade story for Jellyfin 10.9/10.10 users? | Do not support them with the 10.11 artifact. Jellyfin 10.11.0 release notes say users must upgrade through 10.10.7 first, and 12.0 RC notes say users should be on 10.10.7+ or 10.11.x before upgrading. | https://jellyfin.org/posts/jellyfin-release-10.11.0/ and https://github.com/jellyfin/jellyfin/releases. |
| Does Jellyfin 10.11 ship a multi-targetable Jellyfin.Controller package? | No. The 10.11.11 NuGet package includes net9.0. NuGet computes compatibility with higher TFMs such as net10.0, but the included target framework is net9.0. | https://www.nuget.org/packages/Jellyfin.Controller/10.11.11. |
| Can a net8.0 Watchoffit plugin support Jellyfin 10.11? | Not with Jellyfin.Controller 10.11.x, because that package targets net9.0. | Jellyfin.Controller 10.11.11 NuGet framework listing. |
| Can a net10.0 Watchoffit plugin support Jellyfin 10.11? | No. It may compile, but it is too new for a net9.0 host process. | Jellyfin 10.11.11 server csproj and CLR load rules. |
| Should the installed meta.json include versions[]? | No for the installed shape. versions[] belongs to the repository manifest; installed meta.json should carry direct version/loading fields and assemblies. | Jellyfin installed plugin manifest model: https://github.com/jellyfin/jellyfin/blob/master/MediaBrowser.Common/Plugins/PluginManifest.cs. |
| Does imagePath point to a file that exists in the ZIP? | Must be checked during packaging. The documented release layout expects watchoffit.png at the ZIP root unless imagePath deliberately points elsewhere. | This repo's versioning document and package output. |
```

Release checklist before v1.0.0.0:

```text
1. Change the 10.11 plugin project to net9.0.
2. Keep Jellyfin.Controller and Jellyfin.Model pinned to 10.11.11.
3. Update build metadata from net10.0 to net9.0.
4. Generate a ZIP whose installed meta.json matches Jellyfin's installed shape.
5. Include a real PNG at the path named by imagePath.
6. Build with `dotnet build Jellyfin.Plugin.Watchoffit.sln -c Release`.
7. Inspect the .deps.json target for .NETCoreApp,Version=v9.0.
8. Install into jellyfin/jellyfin:10.11.11.
9. Confirm the plugin list and configuration page.
10. Only then publish the repository manifest entry.
```

Do not publish the current `net10.0` scaffold as the Jellyfin 10.11 plugin
artifact.

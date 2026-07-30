# Stage 1: The Plugin Shell — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** The plugin loads into a real NoMercy MediaServer, appears in the dashboard, registers four scheduled jobs the owner can see individually, reads the TV library, and is fully configurable from the settings page — with passwords in the secret store, never in config.

**Architecture:** A second project, `NoMercy.Plugin.TorrentDownloader`, is the only thing that references `NoMercy.Plugins.Abstractions`. It implements `IPlugin`, `IScheduledTaskPlugin` and `IUiPlugin`, and supplies `Core` with its ports. `Core` gains one new port — `ILibraryQuery` — and still references no NoMercy assembly. No orchestration in this stage: the job ticks do the honest minimum and log, so the wiring is provable before there is a loop to debug through.

**Tech Stack:** net10.0, C# 13, xUnit, FluentAssertions `[7.0.0,8.0.0)`, `NoMercy.Plugins.Abstractions` from a locally-packed feed.

## Global Constraints

Every task's requirements implicitly include this section.

- `net10.0`. Build and test with `& "$env:USERPROFILE\.dotnet\dotnet.exe"` — the `dotnet` on PATH is SDK 8 and cannot build this solution.
- **Explicit types, never `var`.** No exceptions.
- **No useless comments.** A comment earns its place by explaining *why*, or by recording a non-obvious constraint. Never restate the code.
- **Every `.cs` file starts with exactly these two lines:**
  ```csharp
  // SPDX-License-Identifier: MIT
  // Copyright (c) 2026 Phillippe Pelzer - https://github.com/Fill84
  ```
  The NoMercy proprietary header belongs only on files contributed upstream. It must never appear in this repo.
- `[GeneratedRegex]` for constant patterns, with `IgnoreCase | CultureInvariant`.
- **`Core` references no NoMercy assembly.** Only the shell project may reference `NoMercy.Plugins.Abstractions`. A `using NoMercy.Plugins.Abstractions;` inside `Core` is a build break by policy, and `Core`'s tests must keep running with no server clone present.
- **`HttpClient` is always injected, never constructed.** In the shell it comes from `IPluginContext.HttpClient`, which is bounded by the plugin's granted hosts. Constructing one steps around the platform's enforcement point.
- **No `DateTime.Now` / `DateTimeOffset.UtcNow` outside `SystemClock`.** Time comes from the injected `IClock`.
- **Secrets never touch `IPluginConfiguration`.** It is whole-object JSON on disk. Passwords, API keys and tokens go to `IPluginSecretStore` and nowhere else. No env vars, no plaintext, no "set X before first run".
- **No secret in an exception message or a log line.** The Torznab API key is a query parameter, so never log a URL.
- `TreatWarningsAsErrors=true` on both projects. Zero warnings.
- Conventional commits. **No attribution trailers of any kind** — no `Co-Authored-By`, no "generated with", nothing naming a tool or model.
- Never weaken a test to make it pass. If a brief looks wrong when you reach the code, **stop and report** rather than silently correcting it.
- `tests/fixtures/**` is captured evidence: read it, never edit it.
- Do not push. That is the controller's call.

**Baseline:** 257 tests passing, 0 warnings, at HEAD. That number only goes up.

---

## File Structure

```
nuget.config                                        # local feed + nuget.org
scripts/fetch-abstractions.ps1                      # clone + pack the contract
scripts/fetch-abstractions.sh                       # same, for CI
src/NoMercy.Plugin.TorrentDownloader.Core/
  Library/ILibraryQuery.cs                          # NEW port + its three DTOs
src/NoMercy.Plugin.TorrentDownloader/               # NEW project — the shell
  NoMercy.Plugin.TorrentDownloader.csproj
  plugin.json
  TorrentDownloaderPlugin.cs                        # IPlugin, IScheduledTaskPlugin, IUiPlugin
  PluginIdentity.cs                                 # the id/name/version single source
  Adapters/PluginLibraryQueryAdapter.cs             # IPluginLibraryQuery -> ILibraryQuery
  Configuration/TorrentDownloaderSettings.cs        # the config POCO
  Configuration/SettingsGateway.cs                  # config + secrets, kept apart
  Hosting/HostGrants.cs                             # runtime network-host grants
  Views/SettingsView.cs                             # the settings form
tests/NoMercy.Plugin.TorrentDownloader.Tests/       # NEW test project
  NoMercy.Plugin.TorrentDownloader.Tests.csproj
  ManifestTests.cs
  Adapters/PluginLibraryQueryAdapterTests.cs
  Configuration/SettingsGatewayTests.cs
  Hosting/HostGrantsTests.cs
  PluginLifecycleTests.cs
  Views/SettingsViewTests.cs
  TestSupport/FakePluginContext.cs                  # the whole IPluginContext, faked
  TestSupport/FakeLibraryQuery.cs
  TestSupport/FakeSecretStore.cs
  TestSupport/FakeConfiguration.cs
  TestSupport/FakeGrants.cs
.forgejo/workflows/build.yml                        # CI
```

The shell splits by responsibility, not by layer: the plugin class is identity and lifecycle only, and each thing it needs from the host lives behind one small file that can be faked in a test.

---

## Ground truth: the shipped contract

Read from the compiled assembly at `nomercy-media-server@dev`, not from documentation. Spec §13d records this in full. The load-bearing facts:

- `IPlugin`: `Name`, `Description`, `Id` (Guid), `Version`, `void Initialize(IPluginContext)`, and `IDisposable`. **`Initialize` is synchronous and returns void.** There is no async init hook.
- `IScheduledTaskPlugin`: `string CronExpression`, `Task ExecuteAsync(ct)`, plus `IReadOnlyList<PluginScheduledJob> Jobs => []` and `Task ExecuteAsync(string jobName, ct) => ExecuteAsync(ct)`.
- `PluginScheduledJob(string Name, string CronExpression, bool AllowConcurrent = false)`.
- `IUiPlugin`: `IReadOnlyList<PluginNavEntry> NavEntries`, `Task<PluginView> GetViewAsync(PluginViewRequest, ct)`.
- `IPluginContext`: `EventBus`, `Services`, `Logger`, `DataFolderPath`, `Configuration`, `HttpClient`, `PluginId`, `Secrets`, `Library`, `LibraryWriter?`, `Grants`, `Hub`, `PublishAsync<T>(name, payload, ct)`.
- `IPluginLibraryQuery`: `GetLibrariesAsync`, `GetShowsAsync(libraryId?)`, `GetMoviesAsync(libraryId?)`, `GetEpisodesAsync(showId)`, `GetShowFilesAsync(showId)`.
- `PluginLibrary(string Id, string Title, string Type)` — `Type` is `movie`, `tv`, `anime` or `music`.
- `PluginLibraryShow(int Id, string Title, int? Year, string LibraryId, string? Folder, int EpisodeCount, int HaveEpisodeCount)`.
- `PluginLibraryEpisode(int ShowId, int SeasonNumber, int EpisodeNumber, string? Title, DateTime? AirDate, bool HasFile)`.
- `PluginLibraryFile(int ShowId, int? SeasonNumber, int? EpisodeNumber, string Path, string Quality)`.
- `IPluginSecretStore`: `GetAsync`, `SetAsync`, `DeleteAsync`, `KeysAsync`. Keys are namespaced by plugin id **by the implementation** — do not prefix them yourself.
- `IPluginConfiguration`: `GetConfiguration<T>()`, `GetConfigurationAsync<T>()`, `SaveConfiguration<T>`, `SaveConfigurationAsync<T>`, `HasConfiguration()`, `DeleteConfiguration()`. Whole-object JSON on disk.
- `IPluginGrants`: `HasAsync(kind, value)`, `GetAsync(kind)`, `RequestAsync(kind, value, reason)`. `RequestAsync` records and returns — **it never waits for a human.**
- `PluginGrantKind.Capability` / `.NetworkHost` / `.LibraryWrite`; `PluginGrant.Everything == "*"`.
- `PluginHookCapability`: `mediaSource`, `metadata`, `scheduledTask`, `auth`, `encoder`, `ui`, `libraryWrite`. `Elevated` = `{ libraryWrite, auth, encoder }`.
- `PluginUiSection`: `music`, `movies`, `shows`, `tools`, `dashboard`, `settings`. Unknown falls back to `tools`.
- `PluginComponentType`: `PluginContainer`, `PluginText`, `PluginForm`, `PluginButton`, `PluginTable`, `PluginBadge`, `PluginProgress`, `PluginEmptyState`, … (`All` is the full set).
- `PluginFormFieldType`: `text`, `password`, `number`, `toggle`, `select`, `checkbox`, `file`.
- `PluginAbi.Current` is `10.0`; `IsCompatible` requires same major and `minor <= current`.

**The plugin's identity, fixed now and never changed:**

| | |
| --- | --- |
| Id | `395df423-3e2f-4a1c-bc5b-dbc41a9133ef` |
| Name | `Torrent Downloader` |
| Assembly | `NoMercy.Plugin.TorrentDownloader.dll` |
| Version | `0.1.0` |
| targetAbi | `10.0` |

**Capabilities declared in this stage — and deliberately not more.** `hooks: ["scheduledTask", "ui"]`, `rest: false`, `ws: false`, no `network.hosts`, `autoEnabled: false`.

`libraryWrite` is **not** declared until Stage 6 actually ships upgrade-replace, and `rest`/`ws` are not declared until Stage 5 ships controllers and a hub handler. Declaring a capability the plugin does not exercise asks the owner for power it does not use, and `libraryWrite` is elevated, so it would additionally force a consent prompt for a feature that is not there. `network.hosts` stays empty because the hosts are user configuration the manifest genuinely cannot know — those are requested at runtime (Task 4).

---

## Task 1: Build plumbing and a shell that loads

The contract is not on nuget.org. Nothing can compile against it until this task exists, so it comes first and delivers the smallest possible real plugin.

**Files:**
- Create: `scripts/fetch-abstractions.ps1`
- Create: `scripts/fetch-abstractions.sh`
- Create: `nuget.config`
- Create: `src/NoMercy.Plugin.TorrentDownloader/NoMercy.Plugin.TorrentDownloader.csproj`
- Create: `src/NoMercy.Plugin.TorrentDownloader/PluginIdentity.cs`
- Create: `src/NoMercy.Plugin.TorrentDownloader/plugin.json`
- Create: `tests/NoMercy.Plugin.TorrentDownloader.Tests/NoMercy.Plugin.TorrentDownloader.Tests.csproj`
- Create: `tests/NoMercy.Plugin.TorrentDownloader.Tests/ManifestTests.cs`
- Modify: `nomercy-torrent-plugin.sln` (add both new projects)
- Modify: `.gitignore` — **append, preserve every existing line.** Add `_server/` and `_nupkgs/`.

**Interfaces:**
- Produces: `PluginIdentity.Id`, `.Name`, `.Description`, `.Version`, `.AssemblyFileName` — the single source of truth every later task reads instead of repeating a literal.

- [ ] **Step 1: Write the fetch script**

`scripts/fetch-abstractions.ps1`. A sparse clone keeps it to seconds; the root props files are required because the abstractions csproj sets neither `TargetFramework` nor package versions.

```powershell
#!/usr/bin/env pwsh
# Packs NoMercy.Plugins.Abstractions into a local NuGet feed.
#
# The contract is not published to nuget.org. Its csproj is IsPackable and carries
# full package metadata precisely because "a plugin author outside this repository
# has no other way to get it" - but nothing publishes it yet. So we clone and pack.
#
# NoMercy.Events must be packed too: it is a ProjectReference of the abstractions,
# so packing only the abstractions yields a package whose dependency cannot resolve.

$ErrorActionPreference = 'Stop'

$dotnet = Join-Path $env:USERPROFILE '.dotnet\dotnet.exe'
if (-not (Test-Path $dotnet)) { $dotnet = 'dotnet' }

$root   = Split-Path -Parent $PSScriptRoot
$server = Join-Path $root '_server'
$feed   = Join-Path $root '_nupkgs'
$branch = if ($env:SERVER_BRANCH) { $env:SERVER_BRANCH } else { 'dev' }

if (-not (Test-Path $server)) {
    git clone --depth=1 --branch=$branch --filter=blob:none --no-checkout `
        https://github.com/NoMercy-Entertainment/nomercy-media-server.git $server
    git -C $server sparse-checkout init --cone
    git -C $server sparse-checkout set src/NoMercy.Plugins.Abstractions src/NoMercy.Events
    git -C $server checkout $branch
} else {
    git -C $server fetch --depth=1 origin $branch
    git -C $server reset --hard FETCH_HEAD
}

New-Item -ItemType Directory -Force $feed | Out-Null

# MSB9008 about a missing NoMercy.Analyzers is expected under a sparse checkout.
# It is an analyzer reference; the package builds correctly without it.
& $dotnet pack (Join-Path $server 'src\NoMercy.Events\NoMercy.Events.csproj') -c Release -o $feed
if ($LASTEXITCODE -ne 0) { throw 'packing NoMercy.Events failed' }

& $dotnet pack (Join-Path $server 'src\NoMercy.Plugins.Abstractions\NoMercy.Plugins.Abstractions.csproj') -c Release -o $feed
if ($LASTEXITCODE -ne 0) { throw 'packing NoMercy.Plugins.Abstractions failed' }

Get-ChildItem $feed -Filter *.nupkg | ForEach-Object { Write-Host "  $($_.Name)" }
```

`scripts/fetch-abstractions.sh` is the same steps in `sh` with `set -eu`, for the CI container. Write it to match; it needs no `$USERPROFILE` fallback because CI puts `dotnet` on PATH.

- [ ] **Step 2: Write `nuget.config`**

At the repo root, so both projects see it. `<clear/>` first — otherwise a machine-level source can shadow the local feed and produce a confusing restore.

```xml
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <packageSources>
    <clear />
    <add key="nuget.org" value="https://api.nuget.org/v3/index.json" protocolVersion="3" />
    <add key="local" value="./_nupkgs" />
  </packageSources>
</configuration>
```

- [ ] **Step 3: Run the fetch script and confirm the feed**

Run: `pwsh scripts/fetch-abstractions.ps1`
Expected: `_nupkgs/` holds `NoMercy.Events.<version>.nupkg` and `NoMercy.Plugins.Abstractions.<version>.nupkg`. The version is currently `0.1.404` and lags the server's release tag — that is the assembly version from the server's `Directory.Build.props`. **Reference it as `Version="*"` and key nothing on that number.**

- [ ] **Step 4: Write the shell csproj**

```xml
<Project Sdk="Microsoft.NET.Sdk">

    <PropertyGroup>
        <TargetFramework>net10.0</TargetFramework>
        <ImplicitUsings>enable</ImplicitUsings>
        <Nullable>enable</Nullable>
        <LangVersion>latest</LangVersion>
        <TreatWarningsAsErrors>true</TreatWarningsAsErrors>
        <AssemblyName>NoMercy.Plugin.TorrentDownloader</AssemblyName>
        <RootNamespace>NoMercy.Plugin.TorrentDownloader</RootNamespace>
        <Version>0.1.0</Version>
        <Authors>Phillippe Pelzer</Authors>
        <Description>Keeps a TV library complete by downloading missing episodes over BitTorrent.</Description>
    </PropertyGroup>

    <ItemGroup>
        <PackageReference Include="NoMercy.Plugins.Abstractions" Version="*" />
    </ItemGroup>

    <ItemGroup>
        <ProjectReference Include="..\NoMercy.Plugin.TorrentDownloader.Core\NoMercy.Plugin.TorrentDownloader.Core.csproj" />
    </ItemGroup>

    <ItemGroup>
        <None Update="plugin.json">
            <CopyToOutputDirectory>PreserveNewest</CopyToOutputDirectory>
        </None>
    </ItemGroup>

</Project>
```

- [ ] **Step 5: Write `PluginIdentity.cs`**

Every literal that has to agree between the manifest, the plugin class and the tests lives here once.

```csharp
// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Phillippe Pelzer - https://github.com/Fill84

namespace NoMercy.Plugin.TorrentDownloader;

// The manifest and the IPlugin implementation must agree on all of this, and the
// host matches a loaded assembly to its manifest by id. A drift between the two
// is a plugin that either fails to load or loads as something it is not, so both
// sides read these constants and ManifestTests asserts they match the shipped json.
public static class PluginIdentity
{
    public static Guid Id { get; } = new("395df423-3e2f-4a1c-bc5b-dbc41a9133ef");

    public const string Name = "Torrent Downloader";

    public const string Description =
        "Keeps a TV library complete by downloading missing episodes over BitTorrent.";

    public static Version Version { get; } = new(0, 1, 0);

    public const string AssemblyFileName = "NoMercy.Plugin.TorrentDownloader.dll";
}
```

- [ ] **Step 6: Write `plugin.json`**

```json
{
  "id": "395df423-3e2f-4a1c-bc5b-dbc41a9133ef",
  "name": "Torrent Downloader",
  "description": "Keeps a TV library complete by downloading missing episodes over BitTorrent.",
  "version": "0.1.0",
  "targetAbi": "10.0",
  "author": "Phillippe Pelzer",
  "projectUrl": "https://forgejo.phillippepelzer.me/FiLL/nomercy-torrent-plugin",
  "assembly": "NoMercy.Plugin.TorrentDownloader.dll",
  "autoEnabled": false,
  "capabilities": {
    "hooks": ["scheduledTask", "ui"],
    "rest": false,
    "ws": false,
    "ui": {
      "mounts": [
        {
          "section": "settings",
          "label": "Torrent Downloader",
          "icon": "download",
          "route": "/settings"
        }
      ]
    }
  }
}
```

- [ ] **Step 7: Write the test project csproj**

Mirror `Core.Tests`. Its `ItemGroup` is exactly this, and the FluentAssertions range is pinned because v8 changed to a commercial licence:

```xml
<ItemGroup>
    <PackageReference Include="FluentAssertions" Version="[7.0.0,8.0.0)" />
    <PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.*" />
    <PackageReference Include="xunit" Version="2.*" />
    <PackageReference Include="xunit.runner.visualstudio" Version="2.*" />
</ItemGroup>
```

Same `PropertyGroup` too — `net10.0`, `ImplicitUsings`, `Nullable`, `LangVersion`, `IsPackable=false` — plus `TreatWarningsAsErrors`. Note `Core.Tests` does **not** set `TreatWarningsAsErrors` in its csproj; it is passed on the command line. Match that rather than adding it, so both test projects behave the same way.

It references the shell project (which transitively brings the abstractions) and must also copy `plugin.json` to the output directory so `ManifestTests` reads the real shipped file rather than a duplicate:

```xml
<ItemGroup>
    <None Include="..\..\src\NoMercy.Plugin.TorrentDownloader\plugin.json"
          Link="plugin.json"
          CopyToOutputDirectory="PreserveNewest" />
</ItemGroup>
```

- [ ] **Step 8: Write the failing manifest tests**

`ManifestTests.cs`. These are the tests that catch a manifest/code drift, which is otherwise a silent load failure. Deserialise the real `plugin.json` with `PluginManifest` — the host's own type — so a shape change upstream fails here.

Write these six:

1. `Manifest_DeserialisesWithTheHostsOwnType` — reading `plugin.json` into `PluginManifest` succeeds and every `required` member is populated.
2. `Manifest_IdMatchesPluginIdentity` — `manifest.Id == PluginIdentity.Id`.
3. `Manifest_NameAndDescriptionMatchPluginIdentity`.
4. `Manifest_VersionMatchesPluginIdentity` — `Version.Parse(manifest.Version) == PluginIdentity.Version`.
5. `Manifest_AssemblyNameMatchesTheBuiltAssembly` — `manifest.Assembly == PluginIdentity.AssemblyFileName`, and that file exists next to the test assembly.
6. `Manifest_TargetAbiIsCompatibleWithTheShippedAbi` — `PluginAbi.IsCompatible(manifest.TargetAbi)` is true. Uses the host's own compatibility rule rather than restating it.

And four on the declared capabilities, which are the promises to the owner:

7. `Manifest_DeclaresOnlyTheHooksThisStageImplements` — `Hooks` is exactly `["scheduledTask", "ui"]`, and every entry is a member of the `PluginHookCapability` constants.
8. `Manifest_DeclaresNoElevatedHook` — no declared hook is in `PluginHookCapability.Elevated`. This is the test that fails the day someone adds `libraryWrite` without shipping upgrade-replace.
9. `Manifest_DeclaresNeitherRestNorWsUntilTheyAreImplemented` — both false.
10. `Manifest_UiMountUsesAKnownSection` — the mount's `Section` is in `PluginUiSection.All`. An unknown one silently falls back to `tools`, so nothing else would catch a typo.

- [ ] **Step 9: Run the tests to verify they fail**

Run: `& "$env:USERPROFILE\.dotnet\dotnet.exe" test -c Release`
Expected: FAIL — the shell project does not compile yet, or `plugin.json` is not where the tests look. A build failure is the correct red for a task that introduces new types.

- [ ] **Step 10: Add both projects to the solution and make the tests pass**

Run: `& "$env:USERPROFILE\.dotnet\dotnet.exe" sln add src/NoMercy.Plugin.TorrentDownloader/NoMercy.Plugin.TorrentDownloader.csproj tests/NoMercy.Plugin.TorrentDownloader.Tests/NoMercy.Plugin.TorrentDownloader.Tests.csproj`

Then fix whatever the tests report until green.

- [ ] **Step 11: Verify the whole suite and the constraint that matters most**

Run: `& "$env:USERPROFILE\.dotnet\dotnet.exe" test -c Release -p:TreatWarningsAsErrors=true`
Expected: PASS, 267 total (257 existing + 10 new), 0 warnings.

Then confirm `Core` is still clean: `Select-String -Path src/NoMercy.Plugin.TorrentDownloader.Core/**/*.cs -Pattern 'NoMercy.Plugins.Abstractions'` must find **nothing**, and `dotnet build src/NoMercy.Plugin.TorrentDownloader.Core` must still succeed with `_server/` and `_nupkgs/` deleted. State both results in your report.

- [ ] **Step 12: Commit**

```bash
git add nuget.config scripts .gitignore src/NoMercy.Plugin.TorrentDownloader tests/NoMercy.Plugin.TorrentDownloader.Tests nomercy-torrent-plugin.sln
git commit -m "feat(shell): add the plugin project, manifest and local contract feed"
```

---

## Task 2: The library port and its adapter

**Files:**
- Create: `src/NoMercy.Plugin.TorrentDownloader.Core/Library/ILibraryQuery.cs`
- Create: `src/NoMercy.Plugin.TorrentDownloader/Adapters/PluginLibraryQueryAdapter.cs`
- Create: `tests/NoMercy.Plugin.TorrentDownloader.Tests/TestSupport/FakeLibraryQuery.cs`
- Create: `tests/NoMercy.Plugin.TorrentDownloader.Tests/Adapters/PluginLibraryQueryAdapterTests.cs`

**Interfaces:**
- Consumes: `PluginIdentity` (Task 1).
- Produces: `ILibraryQuery`, `LibraryShow`, `LibraryEpisode`, `LibraryFile` — every later stage's view of the library.

- [ ] **Step 1: Write the port in `Core`**

Spec §5.3. Note there is **no** `GetShowFolderAsync`: the folder arrives on the show.

```csharp
// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Phillippe Pelzer - https://github.com/Fill84

namespace NoMercy.Plugin.TorrentDownloader.Core.Library;

public interface ILibraryQuery
{
    Task<IReadOnlyList<LibraryShow>> GetShowsAsync(CancellationToken ct);
    Task<IReadOnlyList<LibraryEpisode>> GetEpisodesAsync(int showId, CancellationToken ct);
    Task<IReadOnlyList<LibraryFile>> GetFilesAsync(int showId, CancellationToken ct);
}

// Folder is nullable because the host's contract makes it nullable. A show with no
// folder cannot be a download target, and the engine must skip it with a reason
// rather than composing a path from null.
public record LibraryShow(
    int ShowId,
    string Title,
    int? Year,
    string LibraryId,
    string? Folder,
    int EpisodeCount,
    int HaveEpisodeCount
);

public record LibraryEpisode(
    int ShowId,
    int SeasonNumber,
    int EpisodeNumber,
    string? Title,
    DateTimeOffset? AirDate,
    bool HasFile
);

public record LibraryFile(
    int ShowId,
    int? SeasonNumber,
    int? EpisodeNumber,
    string Path,
    string Quality
);
```

`AirDate` is `DateTimeOffset?` here while the host gives `DateTime?`. That is deliberate — `Core` uses `DateTimeOffset` everywhere else, and the adapter is the one place that converts. Convert with `DateTime.SpecifyKind(value, DateTimeKind.Utc)` before constructing the offset, because the host's `DateTime` has `Kind` `Unspecified` and letting it be read as local time would shift an air date by up to a day. **This matters for the daily-show path in §18.5.**

- [ ] **Step 2: Write the failing adapter tests**

`FakeLibraryQuery` implements `IPluginLibraryQuery` with settable lists and a call counter. Then eight tests:

1. `GetShowsAsync_ReturnsShowsFromEveryTvLibrary` — two libraries, `tv` and `movie`; only the `tv` one's shows come back.
2. `GetShowsAsync_IncludesAnimeLibraries` — a library of type `anime` is included. Anime is TV for this plugin's purposes and excluding it would silently never download any of it.
3. `GetShowsAsync_ExcludesMusicAndMovieLibraries`.
4. `GetShowsAsync_MapsEveryFieldIncludingANullFolder` — a show with `Folder = null` maps to `Folder = null`, not to empty string. The distinction is load-bearing: empty string would look like the library root.
5. `GetEpisodesAsync_KeepsEpisodesWithNoFile` — an episode with `HasFile = false` is returned, not filtered out. It is the gap the engine looks for, so dropping it would silently disable the whole plugin.
6. `GetEpisodesAsync_TreatsAnUnspecifiedAirDateAsUtc` — host `DateTime` with `Kind = Unspecified` for `2026-07-22T00:00:00` maps to a `DateTimeOffset` with zero offset. Assert `Offset == TimeSpan.Zero` and the date is still the 22nd.
7. `GetEpisodesAsync_MapsANullAirDateToNull`.
8. `GetFilesAsync_MapsPathAndQuality`.

Plus one on efficiency, because the naive implementation is quadratic:

9. `GetShowsAsync_AsksForLibrariesOnceAndShowsOncePerTvLibrary` — with three TV libraries, `GetLibrariesAsync` is called once and `GetShowsAsync` three times, never once per show.

- [ ] **Step 3: Run to verify failure**

Run: `& "$env:USERPROFILE\.dotnet\dotnet.exe" test -c Release --filter PluginLibraryQueryAdapterTests`
Expected: FAIL — `PluginLibraryQueryAdapter` does not exist.

- [ ] **Step 4: Write the adapter**

```csharp
// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Phillippe Pelzer - https://github.com/Fill84

using NoMercy.Plugin.TorrentDownloader.Core.Library;
using NoMercy.Plugins.Abstractions;

namespace NoMercy.Plugin.TorrentDownloader.Adapters;

public sealed class PluginLibraryQueryAdapter(IPluginLibraryQuery library) : ILibraryQuery
{
    // The plugin's subject is episodic television, and the host models anime as its
    // own library type. Both are shows with seasons and episodes here.
    private static readonly string[] ShowLibraryTypes = ["tv", "anime"];

    public async Task<IReadOnlyList<LibraryShow>> GetShowsAsync(CancellationToken ct)
    {
        IReadOnlyList<PluginLibrary> libraries = await library.GetLibrariesAsync(ct);
        List<LibraryShow> shows = [];

        foreach (PluginLibrary candidate in libraries)
        {
            if (!ShowLibraryTypes.Contains(candidate.Type, StringComparer.OrdinalIgnoreCase))
                continue;

            IReadOnlyList<PluginLibraryShow> found = await library.GetShowsAsync(candidate.Id, ct);
            shows.AddRange(found.Select(ToShow));
        }

        return shows;
    }

    public async Task<IReadOnlyList<LibraryEpisode>> GetEpisodesAsync(int showId, CancellationToken ct)
    {
        IReadOnlyList<PluginLibraryEpisode> episodes = await library.GetEpisodesAsync(showId, ct);
        return [.. episodes.Select(ToEpisode)];
    }

    public async Task<IReadOnlyList<LibraryFile>> GetFilesAsync(int showId, CancellationToken ct)
    {
        IReadOnlyList<PluginLibraryFile> files = await library.GetShowFilesAsync(showId, ct);
        return [.. files.Select(ToFile)];
    }

    private static LibraryShow ToShow(PluginLibraryShow show) =>
        new(
            show.Id,
            show.Title,
            show.Year,
            show.LibraryId,
            show.Folder,
            show.EpisodeCount,
            show.HaveEpisodeCount
        );

    private static LibraryEpisode ToEpisode(PluginLibraryEpisode episode) =>
        new(
            episode.ShowId,
            episode.SeasonNumber,
            episode.EpisodeNumber,
            episode.Title,
            ToUtc(episode.AirDate),
            episode.HasFile
        );

    private static LibraryFile ToFile(PluginLibraryFile file) =>
        new(file.ShowId, file.SeasonNumber, file.EpisodeNumber, file.Path, file.Quality);

    // The host's DateTime arrives with Kind Unspecified. Constructing a DateTimeOffset
    // from it directly would apply the server's local offset and shift an air date by
    // up to a day, which is exactly the comparison the daily-show path depends on.
    private static DateTimeOffset? ToUtc(DateTime? value) =>
        value is DateTime date
            ? new DateTimeOffset(DateTime.SpecifyKind(date, DateTimeKind.Utc))
            : null;
}
```

- [ ] **Step 5: Run to verify pass**

Run: `& "$env:USERPROFILE\.dotnet\dotnet.exe" test -c Release -p:TreatWarningsAsErrors=true`
Expected: PASS, 276 total, 0 warnings.

- [ ] **Step 6: Commit**

```bash
git add src/NoMercy.Plugin.TorrentDownloader.Core/Library src/NoMercy.Plugin.TorrentDownloader/Adapters tests/NoMercy.Plugin.TorrentDownloader.Tests
git commit -m "feat(shell): read the library through the host's read-only contract"
```

---

## Task 3: Settings and secrets, kept apart

The rule this task enforces: **a password never reaches `IPluginConfiguration`.** It is whole-object JSON on disk, so a secret written through it lands in plaintext.

**Files:**
- Create: `src/NoMercy.Plugin.TorrentDownloader/Configuration/TorrentDownloaderSettings.cs`
- Create: `src/NoMercy.Plugin.TorrentDownloader/Configuration/SettingsGateway.cs`
- Create: `tests/NoMercy.Plugin.TorrentDownloader.Tests/TestSupport/FakeConfiguration.cs`
- Create: `tests/NoMercy.Plugin.TorrentDownloader.Tests/TestSupport/FakeSecretStore.cs`
- Create: `tests/NoMercy.Plugin.TorrentDownloader.Tests/Configuration/SettingsGatewayTests.cs`

**Interfaces:**
- Produces: `TorrentDownloaderSettings` (the config POCO), `SettingsGateway.LoadAsync/SaveAsync`, and `IndexerSettings.ApiKey` handling.

- [ ] **Step 1: Write the settings POCO**

`IPluginConfiguration.GetConfiguration<T>()` requires `class, new()`, so this is a mutable class with a parameterless constructor and defaults matching spec §11's cron table. **`ApiKey` and `Password` are deliberately absent** — they live in the secret store, keyed by the entry's `Name`.

```csharp
// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Phillippe Pelzer - https://github.com/Fill84

namespace NoMercy.Plugin.TorrentDownloader.Configuration;

public class TorrentDownloaderSettings
{
    public string TransfersCron { get; set; } = "* * * * *";
    public string FeedCron { get; set; } = "*/15 * * * *";
    public string SearchCron { get; set; } = "0 */6 * * *";
    public string MaintenanceCron { get; set; } = "0 4 * * *";

    public string IncompleteFolder { get; set; } = string.Empty;
    public string IntakeFolder { get; set; } = string.Empty;

    public List<IndexerSettings> Indexers { get; set; } = [];
    public List<TorrentClientSettings> Clients { get; set; } = [];
}

// No ApiKey here. It goes to IPluginSecretStore under the key this entry's Name
// produces, because IPluginConfiguration is whole-object JSON on disk and a key
// written through it would sit in plaintext next to the rest of the settings.
public class IndexerSettings
{
    public string Name { get; set; } = string.Empty;
    public string Kind { get; set; } = "torznab";
    public string Url { get; set; } = string.Empty;
    public int Priority { get; set; } = 25;
    public bool Enabled { get; set; } = true;
    public int MinimumIntervalSeconds { get; set; } = 15;
    public List<string> Categories { get; set; } = [];
}

// No Password here, for the same reason.
public class TorrentClientSettings
{
    public string Name { get; set; } = string.Empty;
    public string Kind { get; set; } = "qbittorrent";
    public string Url { get; set; } = string.Empty;
    public string Username { get; set; } = string.Empty;
    public bool Enabled { get; set; } = true;
}
```

- [ ] **Step 2: Write the failing gateway tests**

`FakeConfiguration` implements `IPluginConfiguration` over an in-memory object and **records every object it was asked to save**, so a test can inspect the serialised JSON. `FakeSecretStore` implements `IPluginSecretStore` over a dictionary.

Ten tests:

1. `LoadAsync_ReturnsDefaultsWhenNothingIsSaved` — `HasConfiguration()` false yields the default cron values, not nulls.
2. `LoadAsync_ReturnsSavedSettings` — round-trip.
3. `SaveAsync_WritesSettingsToConfiguration`.
4. `SaveAsync_SendsAnIndexerApiKeyToTheSecretStore` — after saving an indexer with an API key, `secrets.GetAsync(key)` returns it.
5. `SaveAsync_NeverWritesAnApiKeyIntoConfiguration` — **the important one.** Serialise everything `FakeConfiguration` was asked to save with `JsonSerializer.Serialize` and assert the API key's literal text does not appear anywhere in it. Assert on the serialised JSON, not on a property: a property assertion passes while a future added field leaks the key.
6. `SaveAsync_NeverWritesAClientPasswordIntoConfiguration` — same shape, for the client password.
7. `LoadAsync_FillsAnApiKeyBackFromTheSecretStore`.
8. `SaveAsync_RemovesASecretWhenItsEntryIsDeleted` — an indexer removed from the settings has its secret deleted rather than left orphaned in the store forever.
9. `SaveAsync_LeavesAnExistingSecretAloneWhenTheFormSubmitsAnEmptyValue` — a settings form does not echo a password back, so it posts empty. Empty must mean "unchanged", never "erase". Getting this wrong silently breaks every indexer on the first settings save.
10. `SecretKeyFor_IsStableAcrossRenamesOfUnrelatedEntries` — the key derives from the entry name only, so editing one indexer never disturbs another's secret.

Do **not** prefix secret keys with the plugin id: `IPluginSecretStore` namespaces by plugin id itself, and a caller-chosen prefix cannot widen scope anyway.

- [ ] **Step 3: Run to verify failure**

Run: `& "$env:USERPROFILE\.dotnet\dotnet.exe" test -c Release --filter SettingsGatewayTests`
Expected: FAIL — `SettingsGateway` does not exist.

- [ ] **Step 4: Write `SettingsGateway`**

Shape it as: `LoadAsync(ct)` reads config (defaults when absent) then fills each entry's secret from the store; `SaveAsync(settings, ct)` splits secrets out, saves the secret-free object to config, writes each non-empty secret, and deletes secrets whose entry is gone. Key derivation is one private static method, `SecretKeyFor(string kind, string name)`, producing e.g. `indexer:prowlarr:apikey`.

Carry the secrets on a separate transport type — do not add them to `IndexerSettings`, or the next person to serialise that object leaks them. A `record IndexerSecret(string Name, string ApiKey)` alongside is enough.

- [ ] **Step 5: Run to verify pass**

Run: `& "$env:USERPROFILE\.dotnet\dotnet.exe" test -c Release -p:TreatWarningsAsErrors=true`
Expected: PASS, 286 total, 0 warnings.

- [ ] **Step 6: Commit**

```bash
git add src/NoMercy.Plugin.TorrentDownloader/Configuration tests/NoMercy.Plugin.TorrentDownloader.Tests
git commit -m "feat(shell): keep settings in config and secrets in the protected store"
```

---

## Task 4: Runtime network-host grants

The manifest cannot name the hosts, because they are whatever the user typed into the settings form. `IPluginGrants` is how a plugin asks for one after install.

**Files:**
- Create: `src/NoMercy.Plugin.TorrentDownloader/Hosting/HostGrants.cs`
- Create: `tests/NoMercy.Plugin.TorrentDownloader.Tests/TestSupport/FakeGrants.cs`
- Create: `tests/NoMercy.Plugin.TorrentDownloader.Tests/Hosting/HostGrantsTests.cs`

**Interfaces:**
- Consumes: `TorrentDownloaderSettings` (Task 3).
- Produces: `HostGrants.EnsureAsync(settings, ct)` returning `IReadOnlyList<string>` of hosts still ungranted.

- [ ] **Step 1: Write the failing tests**

`FakeGrants` implements `IPluginGrants` over a set of `(kind, value)` pairs and records requests.

Nine tests:

1. `EnsureAsync_RequestsAHostThatIsNotGranted`.
2. `EnsureAsync_DoesNotRequestAHostAlreadyGranted`.
3. `EnsureAsync_ReturnsUngrantedHostsSoTheyCanBeShown` — the return value is what the settings page renders as "waiting for your approval".
4. `EnsureAsync_ReturnsEmptyWhenEveryHostIsGranted`.
5. `EnsureAsync_TreatsAWildcardGrantAsCoveringEveryHost` — a grant of `PluginGrant.Everything` means nothing needs requesting. **Use the constant, never a literal `"*"`.**
6. `EnsureAsync_ExtractsTheHostFromAConfiguredUrl` — `https://prowlarr.local:9696/api` requests `prowlarr.local`, not the whole URL. A grant is a host pattern.
7. `EnsureAsync_IgnoresDisabledEntries` — a disabled indexer's host is not requested. Asking for access the plugin will not use is asking for too much.
8. `EnsureAsync_SkipsAnEntryWithAnUnparseableUrl` — an empty or malformed URL is skipped without throwing, and reported. A half-filled settings form must not break the tick.
9. `EnsureAsync_RequestsEachDistinctHostOnce` — two indexers on one host produce one request, and the reason text names the plugin's purpose.

The reason string is shown to the owner and is treated as untrusted text by the host. Make it specific: `"Torrent Downloader needs to reach the indexer you configured at {host}."`

- [ ] **Step 2: Run to verify failure**

Expected: FAIL — `HostGrants` does not exist.

- [ ] **Step 3: Write `HostGrants`**

Collect distinct hosts from enabled indexers and clients, parse each with `Uri.TryCreate(..., UriKind.Absolute, out Uri? parsed)` and take `parsed.Host`, check `HasAsync(PluginGrantKind.NetworkHost, host)`, and also check for `PluginGrant.Everything` once up front. Request the missing ones and return them.

`RequestAsync` records and returns immediately — it never waits for a human — so `EnsureAsync` must be safe to call on every tick. Asking twice for the same thing is explicitly not an error and does not queue a second prompt.

- [ ] **Step 4: Run to verify pass**

Expected: PASS, 295 total, 0 warnings.

- [ ] **Step 5: Commit**

```bash
git add src/NoMercy.Plugin.TorrentDownloader/Hosting tests/NoMercy.Plugin.TorrentDownloader.Tests
git commit -m "feat(shell): request network-host grants for user-configured hosts at runtime"
```

---

## Task 5: The plugin class — lifecycle and the four jobs

**Files:**
- Create: `src/NoMercy.Plugin.TorrentDownloader/TorrentDownloaderPlugin.cs`
- Create: `tests/NoMercy.Plugin.TorrentDownloader.Tests/TestSupport/FakePluginContext.cs`
- Create: `tests/NoMercy.Plugin.TorrentDownloader.Tests/PluginLifecycleTests.cs`

**Interfaces:**
- Consumes: everything from Tasks 1-4.
- Produces: `TorrentDownloaderPlugin`, implementing `IPlugin`, `IScheduledTaskPlugin` and `IUiPlugin`. `JobNames.Transfers/Feed/Search/Maintenance`.

- [ ] **Step 1: Write the failing lifecycle tests**

`FakePluginContext` implements the whole of `IPluginContext` from the fakes built in Tasks 2-4, with `Logger` as `NullLogger.Instance`, `LibraryWriter` settable to null, and `DataFolderPath` pointing at a temp directory.

Fourteen tests here, plus one more in Step 5. The first four are the load-critical ones — a plugin that throws from `Initialize` fails to load, and `Initialize` is synchronous with nowhere to await:

1. `Initialize_DoesNotThrow`.
2. `Initialize_DoesNoIoAndReadsNoConfiguration` — assert `FakeConfiguration.Reads == 0` and `FakeSecretStore.Reads == 0` after `Initialize`. It has nowhere to await, so real work belongs on the first tick.
3. `Initialize_DoesNotTouchTheNetwork` — the fake `HttpClient`'s handler records zero requests.
4. `Dispose_IsSafeBeforeInitialize` — the host may dispose a plugin whose load failed earlier.
5. `Dispose_IsIdempotent`.
6. `Identity_MatchesPluginIdentity` — `Name`, `Description`, `Id`, `Version`.

Then the jobs:

7. `Jobs_DeclaresTheFourCadences` — exactly four, named `transfers`, `feed`, `search`, `maintenance`.
8. `Jobs_TakesEachCronFromSettings` — non-default cron values in settings appear in `Jobs`.
9. `Jobs_DisallowConcurrentExecution` — every job's `AllowConcurrent` is false. An expensive cycle overrunning its interval must skip, not pile up.
10. `CronExpression_MatchesTheTransfersJob` — the single legacy expression stays consistent with the fastest job, for a host that reads it instead of `Jobs`.
11. `ExecuteAsync_RoutesEachJobNameToItsOwnWork` — each of the four names reaches a distinct path. Assert via the logger or a counter, not by reaching into privates.
12. `ExecuteAsync_ThrowsForAnUnknownJobName` — a name the plugin does not know is an error, not a silent no-op. A typo in a cron registration must be loud.

And two on tick behaviour, since this stage has no orchestrator:

13. `ExecuteAsync_BeforeInitializeThrowsInvalidOperation` — a tick without a context is a host bug and must say so rather than NullReferenceException.
14. `ExecuteAsync_HonoursCancellation` — a cancelled token surfaces as `OperationCanceledException`.

- [ ] **Step 2: Run to verify failure**

Expected: FAIL — `TorrentDownloaderPlugin` does not exist.

- [ ] **Step 3: Write the plugin class**

Structure it as:

- Fields: `IPluginContext? _context`, and a lazily-built `SettingsGateway`.
- `Initialize(IPluginContext context)` — assign `_context` and nothing else. No config read, no I/O, no network.
- A private `IPluginContext Context => _context ?? throw new InvalidOperationException("the plugin was ticked before Initialize");`
- `Jobs` — reads settings synchronously via `Configuration.GetConfiguration<TorrentDownloaderSettings>()` (the sync overload exists and this property cannot await), falling back to defaults when the context is absent so a host reading `Jobs` before `Initialize` gets the defaults rather than a throw.

  **`Jobs` tolerating a missing context while `ExecuteAsync` throws on one is deliberate, not an oversight — do not "fix" it either way.** `Jobs` is a property the host may read while discovering and registering a plugin, which can happen before `Initialize`; returning the defaults is the useful answer and a throw there would fail registration. `ExecuteAsync` running without a context is a different thing: a tick can only happen after registration, so no context means the host called out of order, and that is a bug worth surfacing loudly rather than papering over with defaults and doing work against nothing.
- `ExecuteAsync(string jobName, ct)` — a `switch` on the four names with a `default` that throws `ArgumentOutOfRangeException`.
- `ExecuteAsync(ct)` — delegates to the transfers job.
- Each job method, for this stage: call `HostGrants.EnsureAsync` and log any ungranted hosts, log what it *would* do, and return. **No orchestration.** Write it so Stage 4 replaces a body, not the structure.
- `NavEntries` — one entry, `Section = PluginUiSection.Settings`, `Route = "/settings"`, matching `plugin.json`'s mount.
- `GetViewAsync` — delegates to Task 6's `SettingsView`; until then, return an empty view.
- `Dispose` — null-safe, idempotent, sets a `_disposed` flag.

- [ ] **Step 4: Run to verify pass**

Expected: PASS, 309 total, 0 warnings (the 15th test lands in Step 5).

- [ ] **Step 5: Add a manifest-agreement test**

Add to `ManifestTests.cs`: `Manifest_UiMountAgreesWithNavEntries` — the mount in `plugin.json` and the plugin's `NavEntries` describe the same section and route. They are two declarations of one fact, so they can drift; nothing else would catch it.

- [ ] **Step 6: Commit**

```bash
git add src/NoMercy.Plugin.TorrentDownloader tests/NoMercy.Plugin.TorrentDownloader.Tests
git commit -m "feat(shell): implement the plugin lifecycle and four independently scheduled jobs"
```

---

## Task 6: The settings view

The plugin is not usable until the owner can configure it from the dashboard. No env vars, no config-file editing.

**Files:**
- Create: `src/NoMercy.Plugin.TorrentDownloader/Views/SettingsView.cs`
- Create: `tests/NoMercy.Plugin.TorrentDownloader.Tests/Views/SettingsViewTests.cs`
- Modify: `src/NoMercy.Plugin.TorrentDownloader/TorrentDownloaderPlugin.cs` (`GetViewAsync` delegates here)

- [ ] **Step 1: Write the failing view tests**

Ten tests:

1. `Build_ReturnsADeclarativeTreeNotAWebView` — `Components` is populated and `WebView` is null. The webview is the escape hatch and a settings form is ordinary UI.
2. `Build_UsesOnlyKnownComponentTags` — walk the tree recursively and assert every `Component` is in `PluginComponentType.All`. A typo resolves to nothing on the client and is not an error anyone sees.
3. `Build_GivesEveryComponentAUniqueId` — walk the tree and assert no duplicate `Id`. Duplicates break client reconciliation.
4. `Build_UsesOnlyKnownFormFieldTypes` — every field `Type` is in `PluginFormFieldType.All`.
5. `Build_RendersASecretFieldAsPassword` — the API key field's type is `PluginFormFieldType.Password`.
6. `Build_NeverPutsASecretValueInTheTree` — **the important one.** With an API key set, serialise the whole view to JSON and assert the key's literal text does not appear. A settings form must not echo a stored secret back to the client.
7. `Build_LeavesASecretFieldEmptyButMarksThatOneIsStored` — the field's value is empty and its placeholder says a value is saved, so the owner can tell "unset" from "set, not shown".
8. `Build_ShowsUngrantedHostsWhenThereAreAny` — the ungranted hosts from `HostGrants` are rendered so the owner knows why nothing is downloading.
9. `Build_OmitsTheGrantWarningWhenEverythingIsGranted`.
10. `Build_ShowsAnEmptyStateWhenNoIndexerIsConfigured` — uses `PluginComponentType.EmptyState` rather than an empty form, so a first-run user is told what to do.

- [ ] **Step 2: Run to verify failure**

Expected: FAIL — `SettingsView` does not exist.

- [ ] **Step 3: Write `SettingsView`**

A static `Build(TorrentDownloaderSettings settings, IReadOnlyList<string> ungrantedHosts, IReadOnlySet<string> storedSecretKeys)` returning `PluginView`. Pure — no I/O, no context — which is what makes all ten tests cheap.

Compose: a `PluginContainer` holding a `PluginText` heading; a grant warning (`PluginBadge` + `PluginText`) when `ungrantedHosts` is non-empty; a `PluginForm` of the four cron fields and the two folder paths; a `PluginForm` per indexer and per client with a `password` field left empty; and a `PluginEmptyState` when there are no indexers.

Set `RefreshInterval = 0`: a settings page does not change on its own.

- [ ] **Step 4: Wire it into `GetViewAsync` and run**

`GetViewAsync` loads settings, calls `HostGrants.EnsureAsync`, reads `secrets.KeysAsync()` for which secrets exist, and calls `SettingsView.Build`. Route anything other than `/settings` to a `PluginEmptyState` rather than throwing — an unknown route is a client asking for something this version does not have.

Expected: PASS, 320 total, 0 warnings.

- [ ] **Step 5: Commit**

```bash
git add src/NoMercy.Plugin.TorrentDownloader tests/NoMercy.Plugin.TorrentDownloader.Tests
git commit -m "feat(shell): add the settings view, with secrets never echoed back"
```

---

## Task 7: CI

**Files:**
- Create: `.forgejo/workflows/build.yml`

- [ ] **Step 1: Write the workflow**

Adapt the radiostation plugin's workflow at `F:\DevProjects\NoMercyEntertainment-Developement\nomercy-radiostation-plugin\.forgejo\workflows\build.yml`. **Read it first** — it encodes several hard-won runner workarounds, and each comment in it explains a real failure:

- `actions/checkout@v4` does not work: the runner sets `GITHUB_SERVER_URL` to an internal `http://forgejo:3000` the workflow container cannot resolve. Check out manually from the public URL with a basic-auth header.
- Build and release must be one job, because artifact upload/download routes through that same unresolvable hostname.
- The API base URL must be the public one, for the same reason.

Differences from the radiostation workflow, all of which matter:

1. **Run the tests.** The radiostation workflow only builds. This one runs `dotnet test -c Release -p:TreatWarningsAsErrors=true` over the whole solution, and the job fails if any test fails.
2. Use `scripts/fetch-abstractions.sh` rather than inlining the clone-and-pack, so local and CI cannot drift.
3. The `nuget.config` is committed at the repo root, so do not generate one.
4. Package the shell's `bin/Release/net10.0` output: the plugin DLL, `plugin.json`, `README.md`, **and `NoMercy.Plugin.TorrentDownloader.Core.dll`** — the shell depends on it and the plugin will fail to load without it. Do not ship `NoMercy.Plugins.Abstractions.dll` or `NoMercy.Events.dll`: those are the host's, they are in its shared-assembly set, and shipping a copy is how a plugin ends up with two incompatible identities of the same type.
5. `SERVER_BRANCH: dev`.

- [ ] **Step 2: Verify locally what can be verified locally**

The workflow cannot run on this machine. Verify instead that:
- `bash scripts/fetch-abstractions.sh` works from a clean state — delete `_server/` and `_nupkgs/` first.
- The staged file list matches what the packaging step copies. List the actual contents of `bin/Release/net10.0` in your report so the controller can check the packaging step against reality rather than against the plan's guess.

- [ ] **Step 3: Commit**

```bash
git add .forgejo scripts
git commit -m "ci: build, test and package the plugin against a locally packed contract"
```

---

## Self-Review

**Spec coverage.** §5.3 library port → Task 2. §10.1 configuration → Task 3. §10.2 secrets → Task 3, with the no-plaintext rule as its own test. §10.3 network access → Task 4. §11 four jobs → Task 5. §12 elevated/consent → Task 1's manifest, `autoEnabled: false`. §12.1 views → Task 6. §13d contract facts → the ground-truth section, and each fact lands in the task that depends on it. §16 CI → Task 7.

**Deliberately not in this stage,** and each is a later stage in §14 rather than an omission: the SQLite store (2), `ITorrentClient` (3), the orchestrator and completion handoff (4), REST and WS (5), upgrade-replace and the daily-show air-date path (6), and the deferred indexer work (0b-2). The job bodies in Task 5 log and return; that is the honest state of a shell without an engine, and Stage 4 replaces bodies rather than structure.

**Type consistency.** `ILibraryQuery` returns `LibraryShow`/`LibraryEpisode`/`LibraryFile` with `ShowId` as the first member in each, matching the host's `PluginLibraryEpisode`/`PluginLibraryFile`. `AirDate` is `DateTimeOffset?` in `Core` and `DateTime?` at the host boundary — converted in exactly one place, `PluginLibraryQueryAdapter.ToUtc`, and tested. `TorrentDownloaderSettings` is a mutable class with a parameterless constructor because `GetConfiguration<T>()` constrains `T : class, new()`; every other new type is a record.

**Test-count arithmetic.** 257 baseline → Task 1 +10 (267) → Task 2 +9 (276) → Task 3 +10 (286) → Task 4 +9 (295) → Task 5 +15 (310: fourteen lifecycle tests plus the manifest-agreement test in Step 5) → Task 6 +10 (320). Task 7 adds none. A task landing a different number is not automatically wrong, but say so in the report.

**Two risks worth naming.** First, the fakes: `FakePluginContext` has to implement the whole of `IPluginContext`, and if the interface gains a member the shell's tests stop compiling. That is the correct failure — a contract change should be loud. Second, none of this proves the plugin loads into a *real* server; it proves the contract is implemented correctly. A manual install into a running server is the acceptance test, and it belongs to the controller after Task 7.

# Stage 0a: Release Domain Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build the pure domain core that turns a torrent release name into structured data and decides whether it is the episode we want and how good it is.

**Architecture:** A standalone .NET class library with no reference to `NoMercy.Plugins.Abstractions` and no I/O of any kind. Everything here is a pure function over strings and records, so the whole subsystem is exhaustively unit-testable without a server, a network, or a database. Later Stage 0 plans (indexers, download clients, store, scheduler) consume the types defined here.

**Tech Stack:** .NET 10, C# 13, xUnit, FluentAssertions, source-generated regex.

## Global Constraints

- **Target framework `net10.0`.** Matches the media server's `Directory.Build.props`.
- **Explicit types, never `var`.** Hard rule, carried from the host repo's conventions.
- **No useless comments.** Default is zero. Comment only a hidden constraint a reader could not infer from the code. Rationale belongs in the commit message.
- **Every `.cs` file starts with this two-line header**, at line 1, then a blank line, then the `using` block:

  ```csharp
  // SPDX-License-Identifier: MIT
  // Copyright (c) 2026 Phillippe Pelzer - https://github.com/Fill84
  ```

  Do **not** use the NoMercy MediaServer proprietary header here. It asserts three things that are false in this repo: that the file is part of NoMercy MediaServer, that NoMercy Entertainment holds the copyright, and that distribution and commercial use are prohibited — the last of which contradicts this repo's MIT `LICENSE`. That header belongs only on files contributed upstream to `nomercy-media-server`.
- **`[GeneratedRegex]` for every constant pattern.** Requires the containing class and the method to be `partial` and the method `static`.
- **Every case-insensitive pattern carries `RegexOptions.IgnoreCase | RegexOptions.CultureInvariant`.** The two cases differ and the distinction is worth knowing:
  - **Runtime-constructed regexes** — `Regex.IsMatch(input, pattern, options)` and `new Regex(...)`, used in Tasks 8 and 9 for user-supplied term patterns — resolve casing against the *ambient culture*. `CultureInvariant` is **required** there. Measured on `net10.0`: under `tr-TR`, `"SILO SEASON 3 EPISODE 4"` does not match `season\s*(\d{1,2})\s*episode\s*(\d{1,3})` with `IgnoreCase` alone, because uppercase `I` folds to dotless `ı`. Adding `CultureInvariant` makes it match.
  - **`[GeneratedRegex]`** bakes its case-folding table at compile time, so it already matches invariantly and the same measurement returns `True` either way. Declaring `CultureInvariant` there is **not** a bug fix — it is an explicit statement of intent that also removes any dependence on the build machine's culture. Keep it for uniformity, but do not write a test asserting a culture difference for a generated pattern: there is none to observe, and a test that cannot fail is worse than no test.
  - Patterns using only explicit character ranges (`[A-Za-z0-9]`) do no case folding and need neither option.
- **`Core` has zero reference to `NoMercy.Plugins.Abstractions`, `NoMercy.Events`, or any NoMercy assembly.** This is the property that keeps the test loop free of the abstractions-packing CI dance. A PR that adds one is wrong.
- **No I/O in `Core.Releases` or `Core.Profiles`.** No `HttpClient`, no `File`, no `DateTime.Now`. These namespaces are pure.
- **FluentAssertions pinned to `[7.0.0,8.0.0)`.** Version 8 changed to a commercial licence. Do not let a restore float past it.
- **Conventional commits on `master`.** `type(scope): description`. No attribution trailers of any kind.
- **All string comparison against release names is culture-invariant.** Use `StringComparison.OrdinalIgnoreCase` and `ToLowerInvariant()`, never the culture-sensitive overloads. Release names are machine text, not prose.

**Spec:** `docs/superpowers/specs/2026-07-29-torrent-download-plugin-design.md`, sections §5.2 (`Releases/`, `Profiles/`), §8 (release profiles) and §15 (testing).

---

## File Structure

```
nomercy-torrent-plugin.sln
├── src/NoMercy.Plugin.TorrentDownloader.Core/
│   ├── NoMercy.Plugin.TorrentDownloader.Core.csproj
│   ├── Releases/
│   │   ├── SizeParser.cs             "1.4 GB" → 1503238553
│   │   ├── EpisodeSlot.cs            (Season, Episode) value
│   │   ├── ReleaseNameParser.cs      season/episode/pack/quality/codec/group/flags
│   │   ├── Quality.cs                Resolution + ReleaseSource + Quality record
│   │   ├── VideoCodec.cs             enum
│   │   ├── LanguageTags.cs           extraction result
│   │   ├── LanguageTagExtractor.cs   title → languages + dual-audio
│   │   ├── ParsedRelease.cs          the whole parse result
│   │   ├── TitleMatcher.cs           does this title belong to this show
│   │   └── ReleaseInfo.cs            an indexer candidate
│   └── Profiles/
│       ├── QualityDefinition.cs
│       ├── QualityLadder.cs
│       ├── LanguageProfile.cs
│       ├── GroupPreference.cs
│       ├── TermRule.cs
│       ├── ReleaseProfile.cs
│       ├── FilterVerdict.cs
│       ├── FilterContext.cs
│       ├── ReleaseFilter.cs
│       ├── ScoreContext.cs
│       └── ReleaseScorer.cs
└── tests/NoMercy.Plugin.TorrentDownloader.Core.Tests/
    ├── NoMercy.Plugin.TorrentDownloader.Core.Tests.csproj
    ├── Releases/
    │   ├── SizeParserTests.cs
    │   ├── ReleaseNameParserEpisodeTests.cs
    │   ├── ReleaseNameParserQualityTests.cs
    │   ├── ReleaseNameParserGroupTests.cs
    │   ├── LanguageTagExtractorTests.cs
    │   └── TitleMatcherTests.cs
    └── Profiles/
        ├── QualityLadderTests.cs
        ├── ReleaseFilterTests.cs
        ├── ReleaseScorerTests.cs
        └── DecisionTests.cs
```

One type per file. `Releases/` knows nothing about `Profiles/`; the dependency runs one way.

---

## Task 1:1 Solution scaffold and SizeParser

**Files:**
- Create: `nomercy-torrent-plugin.sln`
- Create: `src/NoMercy.Plugin.TorrentDownloader.Core/NoMercy.Plugin.TorrentDownloader.Core.csproj`
- Create: `tests/NoMercy.Plugin.TorrentDownloader.Core.Tests/NoMercy.Plugin.TorrentDownloader.Core.Tests.csproj`
- Create: `src/NoMercy.Plugin.TorrentDownloader.Core/Releases/SizeParser.cs`
- Create: `.gitignore`
- Test: `tests/NoMercy.Plugin.TorrentDownloader.Core.Tests/Releases/SizeParserTests.cs`

**Interfaces:**
- Produces: `SizeParser.Parse(string? text) : long` — bytes, `0` when nothing parses. Used by every indexer in Stage 0b to turn a listing's size cell into bytes.

- [ ] **Step 1: Create the projects**

```bash
cd f:/DevProjects/NoMercyEntertainment-Developement/nomercy-torrent-plugin
dotnet new sln -n nomercy-torrent-plugin
dotnet new classlib -n NoMercy.Plugin.TorrentDownloader.Core -o src/NoMercy.Plugin.TorrentDownloader.Core -f net10.0
dotnet new xunit -n NoMercy.Plugin.TorrentDownloader.Core.Tests -o tests/NoMercy.Plugin.TorrentDownloader.Core.Tests -f net10.0
dotnet sln add src/NoMercy.Plugin.TorrentDownloader.Core/NoMercy.Plugin.TorrentDownloader.Core.csproj
dotnet sln add tests/NoMercy.Plugin.TorrentDownloader.Core.Tests/NoMercy.Plugin.TorrentDownloader.Core.Tests.csproj
dotnet add tests/NoMercy.Plugin.TorrentDownloader.Core.Tests reference src/NoMercy.Plugin.TorrentDownloader.Core
rm src/NoMercy.Plugin.TorrentDownloader.Core/Class1.cs
rm tests/NoMercy.Plugin.TorrentDownloader.Core.Tests/UnitTest1.cs
```

- [ ] **Step 2: Set the project properties**

Replace `src/NoMercy.Plugin.TorrentDownloader.Core/NoMercy.Plugin.TorrentDownloader.Core.csproj` with:

```xml
<Project Sdk="Microsoft.NET.Sdk">

    <PropertyGroup>
        <TargetFramework>net10.0</TargetFramework>
        <ImplicitUsings>enable</ImplicitUsings>
        <Nullable>enable</Nullable>
        <LangVersion>latest</LangVersion>
        <TreatWarningsAsErrors>true</TreatWarningsAsErrors>
        <AssemblyName>NoMercy.Plugin.TorrentDownloader.Core</AssemblyName>
        <RootNamespace>NoMercy.Plugin.TorrentDownloader.Core</RootNamespace>
        <Description>Domain core for the NoMercy torrent download plugin. No host dependency.</Description>
    </PropertyGroup>

</Project>
```

Replace `tests/NoMercy.Plugin.TorrentDownloader.Core.Tests/NoMercy.Plugin.TorrentDownloader.Core.Tests.csproj` with:

```xml
<Project Sdk="Microsoft.NET.Sdk">

    <PropertyGroup>
        <TargetFramework>net10.0</TargetFramework>
        <ImplicitUsings>enable</ImplicitUsings>
        <Nullable>enable</Nullable>
        <LangVersion>latest</LangVersion>
        <IsPackable>false</IsPackable>
    </PropertyGroup>

    <ItemGroup>
        <PackageReference Include="FluentAssertions" Version="[7.0.0,8.0.0)" />
        <PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.*" />
        <PackageReference Include="xunit" Version="2.*" />
        <PackageReference Include="xunit.runner.visualstudio" Version="2.*" />
    </ItemGroup>

    <ItemGroup>
        <ProjectReference Include="..\..\src\NoMercy.Plugin.TorrentDownloader.Core\NoMercy.Plugin.TorrentDownloader.Core.csproj" />
    </ItemGroup>

</Project>
```

- [ ] **Step 3: Add `.gitignore`**

**A `.gitignore` may already exist at the repo root.** If so, append these entries to it and preserve every line already there — do not overwrite the file.

```gitignore
bin/
obj/
.vs/
.idea/
*.user
*.suo
artifacts/
TestResults/
```

- [ ] **Step 4: Write the failing test**

Create `tests/NoMercy.Plugin.TorrentDownloader.Core.Tests/Releases/SizeParserTests.cs`:

```csharp
using FluentAssertions;
using NoMercy.Plugin.TorrentDownloader.Core.Releases;
using Xunit;

namespace NoMercy.Plugin.TorrentDownloader.Core.Tests.Releases;

public class SizeParserTests
{
    [Theory]
    [InlineData("1.4 GB", 1503238553L)]
    [InlineData("700 MB", 734003200L)]
    [InlineData("2.5GB", 2684354560L)]
    [InlineData("1,234 MB", 1293942784L)]
    [InlineData("4 TB", 4398046511104L)]
    [InlineData("512 KB", 524288L)]
    public void Parse_ReadsUnitAndValue(string text, long expected)
    {
        SizeParser.Parse(text).Should().Be(expected);
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("N/A")]
    [InlineData("unknown")]
    public void Parse_ReturnsZeroWhenNothingParses(string? text)
    {
        SizeParser.Parse(text).Should().Be(0L);
    }

    [Fact]
    public void Parse_PrefersLongestUnitSoGigabytesAreNotReadAsBytes()
    {
        SizeParser.Parse("1.4 GB").Should().BeGreaterThan(SizeParser.Parse("1.4 MB"));
    }
}
```

- [ ] **Step 5: Run tests to verify they fail**

Run: `dotnet test tests/NoMercy.Plugin.TorrentDownloader.Core.Tests`
Expected: FAIL — the build breaks with "The name 'SizeParser' does not exist".

- [ ] **Step 6: Write the implementation**

Create `src/NoMercy.Plugin.TorrentDownloader.Core/Releases/SizeParser.cs`:

```csharp
using System.Globalization;
using System.Text.RegularExpressions;

namespace NoMercy.Plugin.TorrentDownloader.Core.Releases;

public static partial class SizeParser
{
    private static readonly Dictionary<string, long> Units = new(StringComparer.OrdinalIgnoreCase)
    {
        ["B"] = 1L,
        ["KB"] = 1024L,
        ["MB"] = 1024L * 1024L,
        ["GB"] = 1024L * 1024L * 1024L,
        ["TB"] = 1024L * 1024L * 1024L * 1024L,
    };

    [GeneratedRegex(@"([\d.,]+)\s*(TB|GB|MB|KB|B)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SizePattern();

    public static long Parse(string? text)
    {
        Match match = SizePattern().Match(text ?? string.Empty);
        if (!match.Success)
            return 0L;

        string number = match.Groups[1].Value.Replace(",", string.Empty);
        if (!double.TryParse(number, NumberStyles.Float, CultureInfo.InvariantCulture, out double value))
            return 0L;

        return (long)(value * Units[match.Groups[2].Value]);
    }
}
```

- [ ] **Step 7: Run tests to verify they pass**

Run: `dotnet test tests/NoMercy.Plugin.TorrentDownloader.Core.Tests`
Expected: PASS, 11 cases (suite total 11).

- [ ] **Step 8: Commit**

```bash
git add .gitignore nomercy-torrent-plugin.sln src tests
git commit -m "feat(core): scaffold Core and Tests projects with SizeParser"
```

---

## Task 2:2 Episode and season-pack parsing

**Files:**
- Create: `src/NoMercy.Plugin.TorrentDownloader.Core/Releases/EpisodeSlot.cs`
- Create: `src/NoMercy.Plugin.TorrentDownloader.Core/Releases/ReleaseNameParser.cs`
- Test: `tests/NoMercy.Plugin.TorrentDownloader.Core.Tests/Releases/ReleaseNameParserEpisodeTests.cs`

**Interfaces:**
- Produces:
  - `readonly record struct EpisodeSlot(int Season, int Episode)`
  - `ReleaseNameParser.ParseEpisode(string? title) : EpisodeSlot?`
  - `ReleaseNameParser.ParseSeasonPack(string? title) : int?` — season number when the title names a season but no episode
  - `ReleaseNameParser.EpisodeMarkerIndex(string? title) : int?` — start index of the earliest episode marker. Task 6's `TitleMatcher` uses this to bound the show-name scope.

Three notations are recognised because all three appear in the wild: `S03E04`, `3x04`, and `Season 3 Episode 4`. When more than one matches, the **earliest** wins — a marker appearing later in a title is usually part of an episode *name*, not the slot.

- [ ] **Step 1: Write the failing test**

Create `tests/NoMercy.Plugin.TorrentDownloader.Core.Tests/Releases/ReleaseNameParserEpisodeTests.cs`:

```csharp
using FluentAssertions;
using NoMercy.Plugin.TorrentDownloader.Core.Releases;
using Xunit;

namespace NoMercy.Plugin.TorrentDownloader.Core.Tests.Releases;

public class ReleaseNameParserEpisodeTests
{
    [Theory]
    [InlineData("Silo.S03E04.1080p.WEB.H264-CAKES", 3, 4)]
    [InlineData("Silo s03e04 1080p", 3, 4)]
    [InlineData("Silo 3x04 1080p", 3, 4)]
    [InlineData("Silo Season 3 Episode 4", 3, 4)]
    [InlineData("Some Show S01E123 1080p", 1, 123)]
    public void ParseEpisode_ReadsEverySupportedNotation(string title, int season, int episode)
    {
        ReleaseNameParser.ParseEpisode(title).Should().Be(new EpisodeSlot(season, episode));
    }

    [Theory]
    [InlineData("Silo S03 1080p WEB H264-CAKES")]
    [InlineData("Silo 2026 1080p WEB")]
    [InlineData("")]
    [InlineData(null)]
    public void ParseEpisode_ReturnsNullWhenNoEpisodeIsNamed(string? title)
    {
        ReleaseNameParser.ParseEpisode(title).Should().BeNull();
    }

    [Fact]
    public void ParseEpisode_TakesTheEarliestMarkerNotTheLast()
    {
        ReleaseNameParser
            .ParseEpisode("Silo S03E04 The 1x02 Incident 1080p")
            .Should()
            .Be(new EpisodeSlot(3, 4));
    }

    [Fact]
    public void ParseEpisode_IgnoresACrossNotationGluedToMoreDigits()
    {
        ReleaseNameParser.ParseEpisode("Show 1920x1080 1080p").Should().BeNull();
    }

    [Theory]
    [InlineData("Silo.S03.1080p.WEB.H264-CAKES", 3)]
    [InlineData("Silo S02 1080p", 2)]
    [InlineData("Silo Season 3 COMPLETE 1080p", 3)]
    public void ParseSeasonPack_ReadsTheSeasonWhenNoEpisodeIsNamed(string title, int season)
    {
        ReleaseNameParser.ParseSeasonPack(title).Should().Be(season);
    }

    [Fact]
    public void ParseSeasonPack_ReturnsNullWhenTheTitleNamesAnEpisode()
    {
        ReleaseNameParser.ParseSeasonPack("Silo.S03E04.1080p").Should().BeNull();
    }

    [Fact]
    public void ParseSeasonPack_DoesNotTreatASpaceSeparatedEpisodeMarkerAsAPack()
    {
        ReleaseNameParser.ParseSeasonPack("Show S03 E04 1080p").Should().BeNull();
    }

    [Fact]
    public void EpisodeMarkerIndex_PointsAtTheStartOfTheMarker()
    {
        ReleaseNameParser.EpisodeMarkerIndex("Silo S03E04 1080p").Should().Be(5);
    }

    [Fact]
    public void EpisodeMarkerIndex_ReturnsNullWhenThereIsNoMarker()
    {
        ReleaseNameParser.EpisodeMarkerIndex("Silo 2026 1080p").Should().BeNull();
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/NoMercy.Plugin.TorrentDownloader.Core.Tests --filter ReleaseNameParserEpisodeTests`
Expected: FAIL — `EpisodeSlot` and `ReleaseNameParser` do not exist.

- [ ] **Step 3: Write EpisodeSlot**

Create `src/NoMercy.Plugin.TorrentDownloader.Core/Releases/EpisodeSlot.cs`:

```csharp
namespace NoMercy.Plugin.TorrentDownloader.Core.Releases;

public readonly record struct EpisodeSlot(int Season, int Episode)
{
    public override string ToString() => $"S{Season:00}E{Episode:00}";
}
```

- [ ] **Step 4: Write ReleaseNameParser**

Create `src/NoMercy.Plugin.TorrentDownloader.Core/Releases/ReleaseNameParser.cs`:

```csharp
using System.Text.RegularExpressions;

namespace NoMercy.Plugin.TorrentDownloader.Core.Releases;

public static partial class ReleaseNameParser
{
    [GeneratedRegex(@"s(\d{1,2})e(\d{1,3})", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SeasonEpisodePattern();

    [GeneratedRegex(@"(?<!\d)(\d{1,2})x(\d{1,3})(?!\d)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex CrossPattern();

    [GeneratedRegex(@"season\s*(\d{1,2})\s*episode\s*(\d{1,3})", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex VerbosePattern();

    // The trailing \b is load-bearing: without it the digit group backtracks to a
    // single digit and the lookahead passes against the second one.
    [GeneratedRegex(@"\bs(\d{1,2})\b(?!\s*e\d)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SeasonPackPattern();

    [GeneratedRegex(@"season\s*(\d{1,2})", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex VerboseSeasonPackPattern();

    private static Match? EarliestEpisodeMatch(string? title)
    {
        string text = title ?? string.Empty;
        Match? earliest = null;

        foreach (Regex pattern in new[] { SeasonEpisodePattern(), CrossPattern(), VerbosePattern() })
        {
            Match match = pattern.Match(text);
            if (match.Success && (earliest is null || match.Index < earliest.Index))
                earliest = match;
        }

        return earliest;
    }

    public static int? EpisodeMarkerIndex(string? title) => EarliestEpisodeMatch(title)?.Index;

    public static EpisodeSlot? ParseEpisode(string? title)
    {
        Match? match = EarliestEpisodeMatch(title);
        if (match is null)
            return null;

        return new EpisodeSlot(
            int.Parse(match.Groups[1].Value),
            int.Parse(match.Groups[2].Value)
        );
    }

    public static int? ParseSeasonPack(string? title)
    {
        if (ParseEpisode(title) is not null)
            return null;

        string text = title ?? string.Empty;

        Match verbose = VerboseSeasonPackPattern().Match(text);
        if (verbose.Success)
            return int.Parse(verbose.Groups[1].Value);

        Match compact = SeasonPackPattern().Match(text);
        return compact.Success ? int.Parse(compact.Groups[1].Value) : null;
    }
}
```

- [ ] **Step 5: Run tests to verify they pass**

Run: `dotnet test tests/NoMercy.Plugin.TorrentDownloader.Core.Tests --filter ReleaseNameParserEpisodeTests`
Expected: PASS, 18 cases (suite total 29).

- [ ] **Step 6: Commit**

```bash
git add src tests
git commit -m "feat(core): parse season, episode and season-pack markers from release names"
```

---

## Task 3:3 Quality and codec parsing

**Files:**
- Create: `src/NoMercy.Plugin.TorrentDownloader.Core/Releases/Quality.cs`
- Create: `src/NoMercy.Plugin.TorrentDownloader.Core/Releases/VideoCodec.cs`
- Modify: `src/NoMercy.Plugin.TorrentDownloader.Core/Releases/ReleaseNameParser.cs`
- Test: `tests/NoMercy.Plugin.TorrentDownloader.Core.Tests/Releases/ReleaseNameParserQualityTests.cs`

**Interfaces:**
- Produces:
  - `enum Resolution { Unknown, Sd480, Sd576, Hd720, Fhd1080, Uhd2160 }`
  - `enum ReleaseSource { Unknown, Cam, Telesync, DvdRip, Hdtv, WebRip, WebDl, BluRay, Remux }`
  - `readonly record struct Quality(Resolution Resolution, ReleaseSource Source)`
  - `enum VideoCodec { Unknown, H264, H265, Av1 }`
  - `ReleaseNameParser.ParseQuality(string? title) : Quality`
  - `ReleaseNameParser.ParseCodec(string? title) : VideoCodec`

Source detection is ordered most-specific-first. `REMUX` must be tested before `BluRay` and `WEB-DL` before a bare `WEB`, or a remux reads as an ordinary BluRay and every web release collapses into one bucket.

The codec patterns allow a space or dot between the letter and the number, because sites that render scene names with dots as spaces turn `H.265` into `H 265`.

- [ ] **Step 1: Write the failing test**

Create `tests/NoMercy.Plugin.TorrentDownloader.Core.Tests/Releases/ReleaseNameParserQualityTests.cs`:

```csharp
using FluentAssertions;
using NoMercy.Plugin.TorrentDownloader.Core.Releases;
using Xunit;

namespace NoMercy.Plugin.TorrentDownloader.Core.Tests.Releases;

public class ReleaseNameParserQualityTests
{
    [Theory]
    [InlineData("Silo S03E04 2160p WEB-DL", Resolution.Uhd2160)]
    [InlineData("Silo S03E04 4K WEB-DL", Resolution.Uhd2160)]
    [InlineData("Silo S03E04 1080p WEB-DL", Resolution.Fhd1080)]
    [InlineData("Silo S03E04 1080i HDTV", Resolution.Fhd1080)]
    [InlineData("Silo S03E04 720p HDTV", Resolution.Hd720)]
    [InlineData("Silo S03E04 576p PDTV", Resolution.Sd576)]
    [InlineData("Silo S03E04 480p WEBRip", Resolution.Sd480)]
    [InlineData("Silo S03E04 WEB-DL", Resolution.Unknown)]
    public void ParseQuality_ReadsResolution(string title, Resolution expected)
    {
        ReleaseNameParser.ParseQuality(title).Resolution.Should().Be(expected);
    }

    [Theory]
    [InlineData("Silo S03E04 1080p BluRay REMUX", ReleaseSource.Remux)]
    [InlineData("Silo S03E04 1080p BluRay", ReleaseSource.BluRay)]
    [InlineData("Silo S03E04 1080p Blu-Ray", ReleaseSource.BluRay)]
    [InlineData("Silo S03E04 1080p BDRip", ReleaseSource.BluRay)]
    [InlineData("Silo S03E04 1080p WEB-DL", ReleaseSource.WebDl)]
    [InlineData("Silo S03E04 1080p WEBDL", ReleaseSource.WebDl)]
    [InlineData("Silo S03E04 1080p WEBRip", ReleaseSource.WebRip)]
    [InlineData("Silo S03E04 1080p WEB", ReleaseSource.WebRip)]
    [InlineData("Silo S03E04 1080p HDTV", ReleaseSource.Hdtv)]
    [InlineData("Silo S03E04 DVDRip", ReleaseSource.DvdRip)]
    [InlineData("Silo S03E04 1080p", ReleaseSource.Unknown)]
    public void ParseQuality_ReadsSourceMostSpecificFirst(string title, ReleaseSource expected)
    {
        ReleaseNameParser.ParseQuality(title).Source.Should().Be(expected);
    }

    [Theory]
    [InlineData("Silo S03E04 1080p WEB x265-CAKES", VideoCodec.H265)]
    [InlineData("Silo S03E04 1080p WEB H265-CAKES", VideoCodec.H265)]
    [InlineData("Silo S03E04 1080p WEB H 265-CAKES", VideoCodec.H265)]
    [InlineData("Silo.S03E04.1080p.WEB.H.265-CAKES", VideoCodec.H265)]
    [InlineData("Silo S03E04 1080p WEB HEVC-CAKES", VideoCodec.H265)]
    [InlineData("Silo S03E04 1080p WEB x264-CAKES", VideoCodec.H264)]
    [InlineData("Silo S03E04 1080p WEB H 264-CAKES", VideoCodec.H264)]
    [InlineData("Silo S03E04 1080p WEB AVC-CAKES", VideoCodec.H264)]
    [InlineData("Silo S03E04 1080p WEB AV1-CAKES", VideoCodec.Av1)]
    [InlineData("Silo S03E04 1080p HDTV-CAKES", VideoCodec.Unknown)]
    public void ParseCodec_ReadsEverySpelling(string title, VideoCodec expected)
    {
        ReleaseNameParser.ParseCodec(title).Should().Be(expected);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/NoMercy.Plugin.TorrentDownloader.Core.Tests --filter ReleaseNameParserQualityTests`
Expected: FAIL — `Resolution`, `ReleaseSource`, `Quality` and `VideoCodec` do not exist.

- [ ] **Step 3: Write the value types**

Create `src/NoMercy.Plugin.TorrentDownloader.Core/Releases/Quality.cs`:

```csharp
namespace NoMercy.Plugin.TorrentDownloader.Core.Releases;

public enum Resolution
{
    Unknown = 0,
    Sd480 = 1,
    Sd576 = 2,
    Hd720 = 3,
    Fhd1080 = 4,
    Uhd2160 = 5,
}

public enum ReleaseSource
{
    Unknown = 0,
    Cam = 1,
    Telesync = 2,
    DvdRip = 3,
    Hdtv = 4,
    WebRip = 5,
    WebDl = 6,
    BluRay = 7,
    Remux = 8,
}

public readonly record struct Quality(Resolution Resolution, ReleaseSource Source)
{
    public override string ToString() => $"{Resolution}/{Source}";
}
```

Create `src/NoMercy.Plugin.TorrentDownloader.Core/Releases/VideoCodec.cs`:

```csharp
namespace NoMercy.Plugin.TorrentDownloader.Core.Releases;

public enum VideoCodec
{
    Unknown = 0,
    H264 = 1,
    H265 = 2,
    Av1 = 3,
}
```

- [ ] **Step 4: Add the parsers**

Append to `src/NoMercy.Plugin.TorrentDownloader.Core/Releases/ReleaseNameParser.cs`, inside the existing class:

```csharp
    [GeneratedRegex(@"\b(2160p|4k|uhd)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Uhd2160Pattern();

    [GeneratedRegex(@"\b1080[pi]\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Fhd1080Pattern();

    [GeneratedRegex(@"\b720[pi]\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Hd720Pattern();

    [GeneratedRegex(@"\b576[pi]\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Sd576Pattern();

    [GeneratedRegex(@"\b480[pi]\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Sd480Pattern();

    [GeneratedRegex(@"\bremux\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex RemuxPattern();

    [GeneratedRegex(@"\b(blu[\s._-]?ray|bdrip|brrip|bdremux)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex BluRayPattern();

    [GeneratedRegex(@"\bweb[\s._-]?dl\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex WebDlPattern();

    [GeneratedRegex(@"\b(web[\s._-]?rip|web)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex WebRipPattern();

    [GeneratedRegex(@"\b(hdtv|pdtv|sdtv)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex HdtvPattern();

    [GeneratedRegex(@"\bdvd[\s._-]?rip\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex DvdRipPattern();

    [GeneratedRegex(@"\b(telesync|\bts\b)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex TelesyncPattern();

    [GeneratedRegex(@"\b(cam|camrip)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex CamPattern();

    [GeneratedRegex(@"\b(x[\s.]?265|h[\s.]?265|hevc)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex HevcPattern();

    [GeneratedRegex(@"\b(x[\s.]?264|h[\s.]?264|avc)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex H264Pattern();

    [GeneratedRegex(@"\bav1\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Av1Pattern();

    public static Quality ParseQuality(string? title)
    {
        string text = title ?? string.Empty;
        return new Quality(ParseResolution(text), ParseSource(text));
    }

    private static Resolution ParseResolution(string text)
    {
        if (Uhd2160Pattern().IsMatch(text))
            return Resolution.Uhd2160;
        if (Fhd1080Pattern().IsMatch(text))
            return Resolution.Fhd1080;
        if (Hd720Pattern().IsMatch(text))
            return Resolution.Hd720;
        if (Sd576Pattern().IsMatch(text))
            return Resolution.Sd576;
        if (Sd480Pattern().IsMatch(text))
            return Resolution.Sd480;
        return Resolution.Unknown;
    }

    private static ReleaseSource ParseSource(string text)
    {
        if (RemuxPattern().IsMatch(text))
            return ReleaseSource.Remux;
        if (BluRayPattern().IsMatch(text))
            return ReleaseSource.BluRay;
        if (WebDlPattern().IsMatch(text))
            return ReleaseSource.WebDl;
        if (WebRipPattern().IsMatch(text))
            return ReleaseSource.WebRip;
        if (HdtvPattern().IsMatch(text))
            return ReleaseSource.Hdtv;
        if (DvdRipPattern().IsMatch(text))
            return ReleaseSource.DvdRip;
        if (TelesyncPattern().IsMatch(text))
            return ReleaseSource.Telesync;
        if (CamPattern().IsMatch(text))
            return ReleaseSource.Cam;
        return ReleaseSource.Unknown;
    }

    public static VideoCodec ParseCodec(string? title)
    {
        string text = title ?? string.Empty;
        if (HevcPattern().IsMatch(text))
            return VideoCodec.H265;
        if (H264Pattern().IsMatch(text))
            return VideoCodec.H264;
        if (Av1Pattern().IsMatch(text))
            return VideoCodec.Av1;
        return VideoCodec.Unknown;
    }
```

- [ ] **Step 5: Run tests to verify they pass**

Run: `dotnet test tests/NoMercy.Plugin.TorrentDownloader.Core.Tests --filter ReleaseNameParserQualityTests`
Expected: PASS, 29 cases (suite total 58).

- [ ] **Step 6: Commit**

```bash
git add src tests
git commit -m "feat(core): parse resolution, source and video codec from release names"
```

---

## Task 4:4 Release group, PROPER/REPACK, and ParsedRelease

**Files:**
- Create: `src/NoMercy.Plugin.TorrentDownloader.Core/Releases/ParsedRelease.cs`
- Modify: `src/NoMercy.Plugin.TorrentDownloader.Core/Releases/ReleaseNameParser.cs`
- Test: `tests/NoMercy.Plugin.TorrentDownloader.Core.Tests/Releases/ReleaseNameParserGroupTests.cs`

**Interfaces:**
- Consumes: `EpisodeSlot` (Task 2), `Quality`, `VideoCodec` (Task 3), `LanguageTags` (Task 5 — this task leaves the language fields empty and Task 5 wires them).
- Produces:
  - `ReleaseNameParser.ParseGroup(string? title) : string?`
  - `ReleaseNameParser.IsProper(string? title) : bool`, `ReleaseNameParser.IsRepack(string? title) : bool`
  - `record ParsedRelease` with `Title`, `Episode`, `SeasonPack`, `Quality`, `Codec`, `ReleaseGroup`, `IsProper`, `IsRepack`, `Languages`, `IsDualAudio`
  - `ReleaseNameParser.Parse(string? title) : ParsedRelease`

Two group conventions are recognised. Scene releases put the group last after a hyphen. Anime fansubs put it first in square brackets. The bracket form is checked first because an anime title carries both a leading `[Group]` and often a trailing `-something` that is not a group.

- [ ] **Step 1: Write the failing test**

Create `tests/NoMercy.Plugin.TorrentDownloader.Core.Tests/Releases/ReleaseNameParserGroupTests.cs`:

```csharp
using FluentAssertions;
using NoMercy.Plugin.TorrentDownloader.Core.Releases;
using Xunit;

namespace NoMercy.Plugin.TorrentDownloader.Core.Tests.Releases;

public class ReleaseNameParserGroupTests
{
    [Theory]
    [InlineData("Silo.S03E04.1080p.WEB.H264-CAKES", "CAKES")]
    [InlineData("Silo S03E04 1080p WEB H264-NTb", "NTb")]
    [InlineData("Some.Show.S01E01.1080p.WEB-DL-Group_Name", "Group_Name")]
    [InlineData("[SubsPlease] Frieren - 01 (1080p) [ABCD1234]", "SubsPlease")]
    [InlineData("[Erai-raws] Show - 12 [1080p]", "Erai-raws")]
    public void ParseGroup_ReadsSceneAndFansubConventions(string title, string expected)
    {
        ReleaseNameParser.ParseGroup(title).Should().Be(expected);
    }

    [Theory]
    [InlineData("Silo S03E04 1080p WEB H264")]
    [InlineData("")]
    [InlineData(null)]
    public void ParseGroup_ReturnsNullWhenNoGroupIsNamed(string? title)
    {
        ReleaseNameParser.ParseGroup(title).Should().BeNull();
    }

    [Theory]
    [InlineData("Silo.S03E04.PROPER.1080p.WEB.H264-CAKES", true, false)]
    [InlineData("Silo.S03E04.REPACK.1080p.WEB.H264-CAKES", false, true)]
    [InlineData("Silo.S03E04.1080p.WEB.H264-CAKES", false, false)]
    public void Parse_ReadsProperAndRepackFlags(string title, bool proper, bool repack)
    {
        ParsedRelease parsed = ReleaseNameParser.Parse(title);
        parsed.IsProper.Should().Be(proper);
        parsed.IsRepack.Should().Be(repack);
    }

    [Fact]
    public void Parse_FillsEveryFieldFromOneTitle()
    {
        ParsedRelease parsed = ReleaseNameParser.Parse("Silo.S03E04.1080p.WEB-DL.H264-CAKES");

        parsed.Title.Should().Be("Silo.S03E04.1080p.WEB-DL.H264-CAKES");
        parsed.Episode.Should().Be(new EpisodeSlot(3, 4));
        parsed.SeasonPack.Should().BeNull();
        parsed.Quality.Should().Be(new Quality(Resolution.Fhd1080, ReleaseSource.WebDl));
        parsed.Codec.Should().Be(VideoCodec.H264);
        parsed.ReleaseGroup.Should().Be("CAKES");
        parsed.IsProper.Should().BeFalse();
        parsed.IsRepack.Should().BeFalse();
    }

    [Fact]
    public void Parse_FillsSeasonPackWhenNoEpisodeIsNamed()
    {
        ParsedRelease parsed = ReleaseNameParser.Parse("Silo.S03.1080p.WEB-DL.H264-CAKES");

        parsed.Episode.Should().BeNull();
        parsed.SeasonPack.Should().Be(3);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/NoMercy.Plugin.TorrentDownloader.Core.Tests --filter ReleaseNameParserGroupTests`
Expected: FAIL — `ParsedRelease` and `ParseGroup` do not exist.

- [ ] **Step 3: Write ParsedRelease**

Create `src/NoMercy.Plugin.TorrentDownloader.Core/Releases/ParsedRelease.cs`:

```csharp
namespace NoMercy.Plugin.TorrentDownloader.Core.Releases;

public record ParsedRelease
{
    public required string Title { get; init; }
    public EpisodeSlot? Episode { get; init; }
    public int? SeasonPack { get; init; }
    public Quality Quality { get; init; }
    public VideoCodec Codec { get; init; }
    public string? ReleaseGroup { get; init; }
    public bool IsProper { get; init; }
    public bool IsRepack { get; init; }
    public IReadOnlyList<string> Languages { get; init; } = [];
    public bool IsDualAudio { get; init; }
}
```

- [ ] **Step 4: Add the group, flag and composition parsers**

Append to the `ReleaseNameParser` class:

```csharp
    [GeneratedRegex(@"^\[([^\]]+)\]")]
    private static partial Regex FansubGroupPattern();

    [GeneratedRegex(@"-([A-Za-z0-9_]+)\s*$")]
    private static partial Regex SceneGroupPattern();

    [GeneratedRegex(@"\bproper\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ProperPattern();

    [GeneratedRegex(@"\brepack\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex RepackPattern();

    public static string? ParseGroup(string? title)
    {
        string text = (title ?? string.Empty).Trim();

        Match fansub = FansubGroupPattern().Match(text);
        if (fansub.Success)
            return fansub.Groups[1].Value;

        Match scene = SceneGroupPattern().Match(text);
        return scene.Success ? scene.Groups[1].Value : null;
    }

    public static bool IsProper(string? title) => ProperPattern().IsMatch(title ?? string.Empty);

    public static bool IsRepack(string? title) => RepackPattern().IsMatch(title ?? string.Empty);

    public static ParsedRelease Parse(string? title)
    {
        string text = title ?? string.Empty;

        return new ParsedRelease
        {
            Title = text,
            Episode = ParseEpisode(text),
            SeasonPack = ParseSeasonPack(text),
            Quality = ParseQuality(text),
            Codec = ParseCodec(text),
            ReleaseGroup = ParseGroup(text),
            IsProper = IsProper(text),
            IsRepack = IsRepack(text),
        };
    }
```

- [ ] **Step 5: Run tests to verify they pass**

Run: `dotnet test tests/NoMercy.Plugin.TorrentDownloader.Core.Tests --filter ReleaseNameParserGroupTests`
Expected: PASS, 13 cases (suite total 71).

- [ ] **Step 6: Commit**

```bash
git add src tests
git commit -m "feat(core): parse release group and proper/repack flags into ParsedRelease"
```

---

## Task 5:5 Language tag extraction

**Files:**
- Create: `src/NoMercy.Plugin.TorrentDownloader.Core/Releases/LanguageTags.cs`
- Create: `src/NoMercy.Plugin.TorrentDownloader.Core/Releases/LanguageTagExtractor.cs`
- Modify: `src/NoMercy.Plugin.TorrentDownloader.Core/Releases/ReleaseNameParser.cs` (wire into `Parse`)
- Test: `tests/NoMercy.Plugin.TorrentDownloader.Core.Tests/Releases/LanguageTagExtractorTests.cs`

**Interfaces:**
- Produces:
  - `record LanguageTags(IReadOnlyList<string> Languages, bool IsDualAudio)`
  - `LanguageTagExtractor.Extract(string? title) : LanguageTags`
- Modifies: `ReleaseNameParser.Parse` now fills `Languages` and `IsDualAudio`.

This is the piece that makes the dual-audio anime case expressible. The token table comes from `torrent-feed`'s `_FOREIGN_MARKERS`, but the behaviour is inverted: that code used the table to **reject**, this one uses it to **report**, and the profile decides.

The table is deliberately conservative. `IT`, `ES` and `DE` are omitted even though they are real language codes, because they are common English substrings and release-group fragments — including them reopens false rejections. `MULTI` and `DUAL` are dual-audio markers, not languages.

A release with no recognised marker reports `["English"]`. Untagged scene releases are English by convention, and treating "no tag" as "no language" would make a `Required: ["English"]` profile reject almost everything.

- [ ] **Step 1: Write the failing test**

Create `tests/NoMercy.Plugin.TorrentDownloader.Core.Tests/Releases/LanguageTagExtractorTests.cs`:

```csharp
using FluentAssertions;
using NoMercy.Plugin.TorrentDownloader.Core.Releases;
using Xunit;

namespace NoMercy.Plugin.TorrentDownloader.Core.Tests.Releases;

public class LanguageTagExtractorTests
{
    [Theory]
    [InlineData("Silo.S03E04.FRENCH.1080p.WEB.H264", "French")]
    [InlineData("Silo.S03E04.GERMAN.1080p.WEB.H264", "German")]
    [InlineData("Silo.S03E04.ITA.1080p.WEB.H264", "Italian")]
    [InlineData("Silo.S03E04.SPANISH.1080p", "Spanish")]
    [InlineData("Silo.S03E04.POLISH.1080p", "Polish")]
    [InlineData("Silo.S03E04.JPN.1080p", "Japanese")]
    [InlineData("Silo.S03E04.NL.1080p", "Dutch")]
    public void Extract_RecognisesLanguageTags(string title, string expected)
    {
        LanguageTagExtractor.Extract(title).Languages.Should().Contain(expected);
    }

    [Fact]
    public void Extract_ReportsBothLanguagesOfAMultiAudioRelease()
    {
        LanguageTags tags = LanguageTagExtractor.Extract("Silo.S03E04.ITA.ENG.1080p.WEB.H264");

        tags.Languages.Should().BeEquivalentTo(["Italian", "English"]);
    }

    [Fact]
    public void Extract_TreatsAnUntaggedReleaseAsEnglish()
    {
        LanguageTags tags = LanguageTagExtractor.Extract("Silo.S03E04.1080p.WEB.H264-CAKES");

        tags.Languages.Should().BeEquivalentTo(["English"]);
        tags.IsDualAudio.Should().BeFalse();
    }

    [Theory]
    [InlineData("Frieren S01E01 1080p Dual Audio")]
    [InlineData("Frieren S01E01 1080p Dual-Audio")]
    [InlineData("Frieren S01E01 1080p DUAL")]
    [InlineData("Silo S03E04 MULTi 1080p")]
    public void Extract_DetectsDualAudioMarkers(string title)
    {
        LanguageTagExtractor.Extract(title).IsDualAudio.Should().BeTrue();
    }

    [Theory]
    [InlineData("Serie.S01E01.[Cap.101].1080p", "Spanish")]
    [InlineData("Serie.S01E01.Capitulo.5.1080p", "Spanish")]
    [InlineData("Serie.Staffel.2.1080p", "German")]
    [InlineData("Serie.Odcinek.5.1080p", "Polish")]
    [InlineData("Serie.Saison.2.1080p", "French")]
    [InlineData("Serie.Seizoen.2.1080p", "Dutch")]
    public void Extract_RecognisesForeignEpisodeWordsWhenNoLanguageTagIsPresent(
        string title,
        string expected
    )
    {
        LanguageTagExtractor.Extract(title).Languages.Should().Contain(expected);
    }

    [Fact]
    public void Extract_DoesNotReadCaptainAsASpanishChapterMarker()
    {
        LanguageTagExtractor
            .Extract("Captain Show S01E01 1080p WEB H264-CAKES")
            .Languages
            .Should()
            .BeEquivalentTo(["English"]);
    }

    [Theory]
    [InlineData("Greek.S01E01.1080p.WEB.H264-GROUP")]
    [InlineData("Russian.Doll.S01E01.1080p.NF.WEB-DL-NTG")]
    [InlineData("Premier.League.PL.Matchday10.S01E01.1080p")]
    public void Extract_IgnoresLanguageWordsInTheShowName(string title)
    {
        LanguageTagExtractor.Extract(title).Languages.Should().BeEquivalentTo(["English"]);
    }

    [Fact]
    public void Parse_FillsLanguageFieldsOnParsedRelease()
    {
        ParsedRelease parsed = ReleaseNameParser.Parse("Frieren S01E01 1080p WEB Dual Audio-Group");

        parsed.IsDualAudio.Should().BeTrue();
        parsed.Languages.Should().NotBeEmpty();
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/NoMercy.Plugin.TorrentDownloader.Core.Tests --filter LanguageTagExtractorTests`
Expected: FAIL — `LanguageTags` and `LanguageTagExtractor` do not exist.

- [ ] **Step 3: Write LanguageTags**

Create `src/NoMercy.Plugin.TorrentDownloader.Core/Releases/LanguageTags.cs`:

```csharp
namespace NoMercy.Plugin.TorrentDownloader.Core.Releases;

public record LanguageTags(IReadOnlyList<string> Languages, bool IsDualAudio);
```

- [ ] **Step 4: Write LanguageTagExtractor**

Create `src/NoMercy.Plugin.TorrentDownloader.Core/Releases/LanguageTagExtractor.cs`:

```csharp
using System.Text.RegularExpressions;

namespace NoMercy.Plugin.TorrentDownloader.Core.Releases;

public static partial class LanguageTagExtractor
{
    private const string English = "English";

    // "IT", "ES" and "DE" are deliberately absent. They are real language codes
    // and also common English substrings and release-group fragments, so
    // including them produces false positives on English releases.
    private static readonly Dictionary<string, string> Markers = new(StringComparer.OrdinalIgnoreCase)
    {
        ["ENG"] = English,
        ["ENGLISH"] = English,
        ["FR"] = "French",
        ["VF"] = "French",
        ["VFF"] = "French",
        ["VFQ"] = "French",
        ["VFI"] = "French",
        ["VOSTFR"] = "French",
        ["FRENCH"] = "French",
        ["TRUEFRENCH"] = "French",
        ["GER"] = "German",
        ["GERMAN"] = "German",
        ["ITA"] = "Italian",
        ["ITALIAN"] = "Italian",
        ["SPANISH"] = "Spanish",
        ["ESP"] = "Spanish",
        ["ESPANOL"] = "Spanish",
        ["CASTELLANO"] = "Spanish",
        ["LATINO"] = "Spanish",
        ["NL"] = "Dutch",
        ["DUTCH"] = "Dutch",
        ["PL"] = "Polish",
        ["PLSUB"] = "Polish",
        ["POLISH"] = "Polish",
        ["KOR"] = "Korean",
        ["KOREAN"] = "Korean",
        ["JPN"] = "Japanese",
        ["JAPANESE"] = "Japanese",
        ["CHINESE"] = "Chinese",
        ["CANTONESE"] = "Chinese",
        ["MANDARIN"] = "Chinese",
        ["RUS"] = "Russian",
        ["RUSSIAN"] = "Russian",
        ["HINDI"] = "Hindi",
        ["TAMIL"] = "Tamil",
        ["TELUGU"] = "Telugu",
        ["SWEDISH"] = "Swedish",
        ["DANISH"] = "Danish",
        ["NORWEGIAN"] = "Norwegian",
        ["FINNISH"] = "Finnish",
        ["NORDIC"] = "Nordic",
        ["CZECH"] = "Czech",
        ["HUN"] = "Hungarian",
        ["HUNGARIAN"] = "Hungarian",
        ["TURKISH"] = "Turkish",
        ["POR"] = "Portuguese",
        ["PORTUGUESE"] = "Portuguese",
        ["PTBR"] = "Portuguese",
        ["GREEK"] = "Greek",
        ["HEBREW"] = "Hebrew",
        ["ARABIC"] = "Arabic",
        ["THAI"] = "Thai",
        ["VIETNAMESE"] = "Vietnamese",
        ["INDONESIAN"] = "Indonesian",
    };

    private static readonly (Regex Pattern, string Language)[] EpisodeWordHints =
    [
        (CapituloPattern(), "Spanish"),
        (EpisodioPattern(), "Italian"),
        (FolgePattern(), "German"),
        (StaffelPattern(), "German"),
        (OdcinekPattern(), "Polish"),
        (SeizoenPattern(), "Dutch"),
        (SaisonPattern(), "French"),
    ];

    [GeneratedRegex(@"[A-Za-z0-9]+")]
    private static partial Regex TokenPattern();

    [GeneratedRegex(@"\bdual([\s._-]?audio)?\b|\bmulti\d?\b|\bdubbed\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex DualAudioPattern();

    // "cap" must be followed by a number so it never matches "Captain".
    [GeneratedRegex(@"\bcap\.?\s*\d|\bcapitulo\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex CapituloPattern();

    [GeneratedRegex(@"\bepisodio\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex EpisodioPattern();

    [GeneratedRegex(@"\bfolge\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex FolgePattern();

    [GeneratedRegex(@"\bstaffel\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex StaffelPattern();

    [GeneratedRegex(@"\bodcinek\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex OdcinekPattern();

    [GeneratedRegex(@"\bseizoen\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SeizoenPattern();

    [GeneratedRegex(@"\bsaison\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SaisonPattern();

    public static LanguageTags Extract(string? title)
    {
        string text = title ?? string.Empty;
        string scope = ScopeAfterEpisodeMarker(text);
        List<string> languages = [];

        foreach (Match token in TokenPattern().Matches(scope))
        {
            if (Markers.TryGetValue(token.Value, out string? language) && !languages.Contains(language))
                languages.Add(language);
        }

        foreach ((Regex pattern, string language) in EpisodeWordHints)
        {
            if (pattern.IsMatch(scope) && !languages.Contains(language))
                languages.Add(language);
        }

        if (languages.Count == 0)
            languages.Add(English);

        return new LanguageTags(languages, DualAudioPattern().IsMatch(scope));
    }

    // Tags follow the episode marker; the show name precedes it. Scanning the whole title
    // makes a show called "Greek" or "Russian Doll" report that language and then fail an
    // English-required profile, so the episode is never grabbed.
    private static string ScopeAfterEpisodeMarker(string text) =>
        ReleaseNameParser.EpisodeMarkerIndex(text) is int index ? text[index..] : text;
}
```

- [ ] **Step 5: Wire it into Parse**

In `src/NoMercy.Plugin.TorrentDownloader.Core/Releases/ReleaseNameParser.cs`, replace the body of `Parse` with:

```csharp
    public static ParsedRelease Parse(string? title)
    {
        string text = title ?? string.Empty;
        LanguageTags tags = LanguageTagExtractor.Extract(text);

        return new ParsedRelease
        {
            Title = text,
            Episode = ParseEpisode(text),
            SeasonPack = ParseSeasonPack(text),
            Quality = ParseQuality(text),
            Codec = ParseCodec(text),
            ReleaseGroup = ParseGroup(text),
            IsProper = IsProper(text),
            IsRepack = IsRepack(text),
            Languages = tags.Languages,
            IsDualAudio = tags.IsDualAudio,
        };
    }
```

- [ ] **Step 6: Run tests to verify they pass**

Run: `dotnet test tests/NoMercy.Plugin.TorrentDownloader.Core.Tests --filter LanguageTagExtractorTests`
Expected: PASS, 24 cases (suite total 95).

- [ ] **Step 7: Run the whole suite to check nothing regressed**

Run: `dotnet test tests/NoMercy.Plugin.TorrentDownloader.Core.Tests`
Expected: PASS, all cases.

- [ ] **Step 8: Commit**

```bash
git add src tests
git commit -m "feat(core): extract language tags and dual-audio markers from release names"
```

---

## Task 6:6 TitleMatcher

**Files:**
- Create: `src/NoMercy.Plugin.TorrentDownloader.Core/Releases/TitleMatcher.cs`
- Test: `tests/NoMercy.Plugin.TorrentDownloader.Core.Tests/Releases/TitleMatcherTests.cs`

**Interfaces:**
- Consumes: `ReleaseNameParser.EpisodeMarkerIndex` (Task 2).
- Produces:
  - `TitleMatcher.Normalize(string? text) : string` — lowercase, alphanumerics only. Used by the scorer and the blacklist for stable title identity.
  - `TitleMatcher.Matches(string? title, string? showName) : bool`

**This is the most important function in the plan and the easiest to get subtly wrong.** Read the rule before writing code.

A release title puts the show name immediately before the episode marker, sometimes behind a franchise or release-group prefix. So the name is accepted in exactly two positions:

- **leading** the title, where only a trailing year or country code may follow it — `Lucky 2026 S01E02`, `Big Brother US S28E08`
- **ending exactly where the episode marker begins** — `Special Ops Lioness S02E01`, `[ToonsHub] The World Is Dancing S01E04`

Everywhere else is rejected. That is what stops `Lucky` matching `Lucky Hank` (the scope ends with `Hank`) or `We.Were.the.Lucky.Ones` (ends with `Ones`).

There is one fallback, and its restriction matters: search-result highlighting can eat the space between two tokens, turning `Lucky 2026` into `Lucky2026`. So separator-free forms are compared — **but only in the leading position**. At character level `Unlucky` ends with `Lucky` exactly as `OpsLioness` ends with `Lioness`, so a trailing character-level fallback cannot tell a glued token from an ordinary English word, and would reopen the very bug this function exists to close.

- [ ] **Step 1: Write the failing test**

Create `tests/NoMercy.Plugin.TorrentDownloader.Core.Tests/Releases/TitleMatcherTests.cs`:

```csharp
using FluentAssertions;
using NoMercy.Plugin.TorrentDownloader.Core.Releases;
using Xunit;

namespace NoMercy.Plugin.TorrentDownloader.Core.Tests.Releases;

public class TitleMatcherTests
{
    [Theory]
    [InlineData("Lucky 2026 S01E02 1080p WEB H264-CAKES", "Lucky")]
    [InlineData("Lucky.2026.S01E02.1080p", "Lucky")]
    [InlineData("Big Brother US S28E08 1080p", "Big Brother US")]
    [InlineData("Big Brother US S28E08 1080p", "Big Brother")]
    [InlineData("Silo S03E04 1080p WEB H264-CAKES", "Silo")]
    [InlineData("Silo S03 1080p WEB H264-CAKES", "Silo")]
    public void Matches_AcceptsTheNameLeadingTheTitle(string title, string showName)
    {
        TitleMatcher.Matches(title, showName).Should().BeTrue();
    }

    [Theory]
    [InlineData("Special Ops Lioness S02E01 1080p", "Lioness")]
    [InlineData("[ToonsHub] The World Is Dancing S01E04 1080p", "The World Is Dancing")]
    public void Matches_AcceptsTheNameEndingWhereTheMarkerBegins(string title, string showName)
    {
        TitleMatcher.Matches(title, showName).Should().BeTrue();
    }

    [Theory]
    [InlineData("Lucky Hank S01E02 1080p", "Lucky")]
    [InlineData("We.Were.the.Lucky.Ones.S01E01.1080p", "Lucky")]
    [InlineData("Unlucky S01E01 1080p", "Lucky")]
    [InlineData("Silo S03E04 The Lucky One 1080p", "Lucky")]
    public void Matches_RejectsTheNameAppearingAnywhereElse(string title, string showName)
    {
        TitleMatcher.Matches(title, showName).Should().BeFalse();
    }

    [Fact]
    public void Matches_AcceptsAGluedLeadingTokenFromSearchHighlighting()
    {
        TitleMatcher.Matches("Lucky2026 S01E02 1080p", "Lucky").Should().BeTrue();
    }

    [Fact]
    public void Matches_DoesNotApplyTheGluedFallbackToTheTrailingPosition()
    {
        TitleMatcher.Matches("OpsLioness S02E01 1080p", "Lioness").Should().BeFalse();
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    public void Matches_RejectsAnEmptyShowName(string? showName)
    {
        TitleMatcher.Matches("Silo S03E04 1080p", showName).Should().BeFalse();
    }

    [Theory]
    [InlineData("Elite S01E01 1080p WEB", "Élite")]
    [InlineData("Pokemon S01E01 1080p WEB", "Pokémon")]
    public void Matches_AcceptsAReleaseThatStrippedDiacriticsFromTheShowName(
        string title,
        string showName
    )
    {
        TitleMatcher.Matches(title, showName).Should().BeTrue();
    }

    [Fact]
    public void Normalize_FoldsDiacritics()
    {
        TitleMatcher.Normalize("Pokémon").Should().Be("pokemon");
    }

    [Theory]
    [InlineData("Silo S03E04 1080p WEB H264-CAKES", "silos03e041080pwebh264cakes")]
    [InlineData("Lucky Hank", "luckyhank")]
    [InlineData("", "")]
    [InlineData(null, "")]
    public void Normalize_StripsEverythingButLowercaseAlphanumerics(string? text, string expected)
    {
        TitleMatcher.Normalize(text).Should().Be(expected);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/NoMercy.Plugin.TorrentDownloader.Core.Tests --filter TitleMatcherTests`
Expected: FAIL — `TitleMatcher` does not exist.

- [ ] **Step 3: Write the implementation**

Create `src/NoMercy.Plugin.TorrentDownloader.Core/Releases/TitleMatcher.cs`:

```csharp
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace NoMercy.Plugin.TorrentDownloader.Core.Releases;

public static partial class TitleMatcher
{
    // Kept short on purpose. Every entry here is a token the show name is
    // allowed to absorb, so a loose list reopens false matches.
    private static readonly HashSet<string> CountryCodes = new(StringComparer.OrdinalIgnoreCase)
    {
        "US",
        "UK",
        "AU",
        "CA",
        "NZ",
        "IE",
        "ZA",
    };

    // Bounds the name scope on a season-pack title. ReleaseNameParser's season-pack
    // pattern is not reusable here: it is private, and its public wrapper returns a
    // season number rather than the index this needs to slice the scope.
    [GeneratedRegex(@"\bs\d{1,2}\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SeasonTokenPattern();

    [GeneratedRegex(@"^(19|20)\d{2}$")]
    private static partial Regex YearPattern();

    [GeneratedRegex(@"[^A-Za-z0-9]+")]
    private static partial Regex TokenSeparatorPattern();

    [GeneratedRegex("[^a-z0-9]")]
    private static partial Regex NonAlphanumericPattern();

    public static string Normalize(string? text) =>
        NonAlphanumericPattern()
            .Replace(FoldDiacritics(text ?? string.Empty).ToLowerInvariant(), string.Empty);

    // Scene releases strip diacritics, so "Élite" arrives as "Elite". Without folding,
    // the ASCII-only separator class treats the accent itself as a separator and splits
    // "Pokémon" into "Pok" and "mon", which matches nothing.
    private static string FoldDiacritics(string text)
    {
        string decomposed = text.Normalize(NormalizationForm.FormD);
        StringBuilder builder = new(decomposed.Length);

        foreach (char character in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(character) != UnicodeCategory.NonSpacingMark)
                builder.Append(character);
        }

        return builder.ToString().Normalize(NormalizationForm.FormC);
    }

    public static bool Matches(string? title, string? showName)
    {
        string text = title ?? string.Empty;
        string[] want = Tokenize(showName);
        if (want.Length == 0)
            return false;

        string[] have = Tokenize(ScopeBeforeMarker(text));
        if (have.Length == 0)
            return false;

        if (LeadsWithQualifiersOnly(have, want))
            return true;

        if (EndsWith(have, want))
            return true;

        return GluedLeadingMatch(have, want);
    }

    private static string ScopeBeforeMarker(string title)
    {
        int? markerIndex = ReleaseNameParser.EpisodeMarkerIndex(title);
        if (markerIndex is int index)
            return title[..index];

        Match season = SeasonTokenPattern().Match(title);
        return season.Success ? title[..season.Index] : title;
    }

    private static string[] Tokenize(string? text) =>
        TokenSeparatorPattern()
            .Split(FoldDiacritics(text ?? string.Empty))
            .Where(token => token.Length > 0)
            .ToArray();

    private static bool IsQualifier(string token) =>
        YearPattern().IsMatch(token) || CountryCodes.Contains(token);

    private static bool LeadsWithQualifiersOnly(string[] have, string[] want)
    {
        if (have.Length < want.Length)
            return false;

        for (int index = 0; index < want.Length; index++)
        {
            if (!string.Equals(have[index], want[index], StringComparison.OrdinalIgnoreCase))
                return false;
        }

        for (int index = want.Length; index < have.Length; index++)
        {
            if (!IsQualifier(have[index]))
                return false;
        }

        return true;
    }

    private static bool EndsWith(string[] have, string[] want)
    {
        if (have.Length < want.Length)
            return false;

        int offset = have.Length - want.Length;
        for (int index = 0; index < want.Length; index++)
        {
            if (!string.Equals(have[offset + index], want[index], StringComparison.OrdinalIgnoreCase))
                return false;
        }

        return true;
    }

    private static bool GluedLeadingMatch(string[] have, string[] want)
    {
        string joined = Normalize(string.Concat(have));
        string wanted = Normalize(string.Concat(want));

        if (wanted.Length == 0 || !joined.StartsWith(wanted, StringComparison.Ordinal))
            return false;

        string remainder = joined[wanted.Length..];
        return remainder.Length == 0 || IsQualifier(remainder);
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/NoMercy.Plugin.TorrentDownloader.Core.Tests --filter TitleMatcherTests`
Expected: PASS, 23 cases (suite total 118).

- [ ] **Step 5: Commit**

```bash
git add src tests
git commit -m "feat(core): match a release title to a show name by name-scope position"
```

---

## Task 7:7 Profile types and quality ladder

**Files:**
- Create: `src/NoMercy.Plugin.TorrentDownloader.Core/Releases/ReleaseInfo.cs`
- Create: `src/NoMercy.Plugin.TorrentDownloader.Core/Profiles/QualityDefinition.cs`
- Create: `src/NoMercy.Plugin.TorrentDownloader.Core/Profiles/QualityLadder.cs`
- Create: `src/NoMercy.Plugin.TorrentDownloader.Core/Profiles/LanguageProfile.cs`
- Create: `src/NoMercy.Plugin.TorrentDownloader.Core/Profiles/GroupPreference.cs`
- Create: `src/NoMercy.Plugin.TorrentDownloader.Core/Profiles/TermRule.cs`
- Create: `src/NoMercy.Plugin.TorrentDownloader.Core/Profiles/ReleaseProfile.cs`
- Test: `tests/NoMercy.Plugin.TorrentDownloader.Core.Tests/Profiles/QualityLadderTests.cs`

**Interfaces:**
- Consumes: `Quality`, `Resolution`, `ReleaseSource`, `VideoCodec` (Task 3).
- Produces: `ReleaseInfo`, `QualityDefinition`, `QualityLadder`, `LanguageProfile`, `GroupPreference`, `TermRule`, `TermKind`, `ReleaseProfile`. Tasks 8, 9 and 10 consume all of these; Stage 0b's indexers produce `ReleaseInfo`.

**The ladder is also the allowed set.** A quality that matches no rung is not merely low-ranked, it is not wanted at all. That keeps one list where a separate allowed-set and ordering would drift apart.

- [ ] **Step 1: Write the failing test**

Create `tests/NoMercy.Plugin.TorrentDownloader.Core.Tests/Profiles/QualityLadderTests.cs`:

```csharp
using FluentAssertions;
using NoMercy.Plugin.TorrentDownloader.Core.Profiles;
using NoMercy.Plugin.TorrentDownloader.Core.Releases;
using Xunit;

namespace NoMercy.Plugin.TorrentDownloader.Core.Tests.Profiles;

public class QualityLadderTests
{
    private static QualityLadder Ladder() =>
        new(
            [
                new QualityDefinition("HDTV-720p", Resolution.Hd720, ReleaseSource.Hdtv),
                new QualityDefinition("WEB-720p", Resolution.Hd720, ReleaseSource.Unknown),
                new QualityDefinition("WEB-1080p", Resolution.Fhd1080, ReleaseSource.Unknown),
                new QualityDefinition("BluRay-1080p", Resolution.Fhd1080, ReleaseSource.BluRay),
            ],
            "WEB-1080p"
        );

    [Fact]
    public void RankOf_OrdersByLadderPosition()
    {
        QualityLadder ladder = Ladder();

        ladder.RankOf(new Quality(Resolution.Fhd1080, ReleaseSource.BluRay)).Should().Be(3);
        ladder.RankOf(new Quality(Resolution.Fhd1080, ReleaseSource.WebDl)).Should().Be(2);
        ladder.RankOf(new Quality(Resolution.Hd720, ReleaseSource.Hdtv)).Should().Be(0);
    }

    [Fact]
    public void RankOf_ReturnsMinusOneForAQualityNotOnTheLadder()
    {
        Ladder().RankOf(new Quality(Resolution.Uhd2160, ReleaseSource.WebDl)).Should().Be(-1);
    }

    [Fact]
    public void RankOf_PrefersTheMostSpecificRung()
    {
        Ladder()
            .RankOf(new Quality(Resolution.Hd720, ReleaseSource.Hdtv))
            .Should()
            .Be(0, "the HDTV-specific rung must win over the source-agnostic WEB-720p rung");
    }

    [Fact]
    public void IsAllowed_IsTrueOnlyForQualitiesOnTheLadder()
    {
        QualityLadder ladder = Ladder();

        ladder.IsAllowed(new Quality(Resolution.Fhd1080, ReleaseSource.WebDl)).Should().BeTrue();
        ladder.IsAllowed(new Quality(Resolution.Sd480, ReleaseSource.WebRip)).Should().BeFalse();
    }

    [Fact]
    public void MeetsCutoff_IsTrueAtOrAboveTheCutoffRung()
    {
        QualityLadder ladder = Ladder();

        ladder.MeetsCutoff(new Quality(Resolution.Fhd1080, ReleaseSource.WebDl)).Should().BeTrue();
        ladder.MeetsCutoff(new Quality(Resolution.Fhd1080, ReleaseSource.BluRay)).Should().BeTrue();
        ladder.MeetsCutoff(new Quality(Resolution.Hd720, ReleaseSource.Hdtv)).Should().BeFalse();
    }

    [Fact]
    public void MeetsCutoff_IsFalseForAQualityNotOnTheLadder()
    {
        Ladder().MeetsCutoff(new Quality(Resolution.Uhd2160, ReleaseSource.WebDl)).Should().BeFalse();
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/NoMercy.Plugin.TorrentDownloader.Core.Tests --filter QualityLadderTests`
Expected: FAIL — the `Profiles` types do not exist.

- [ ] **Step 3: Write ReleaseInfo**

Create `src/NoMercy.Plugin.TorrentDownloader.Core/Releases/ReleaseInfo.cs`:

```csharp
namespace NoMercy.Plugin.TorrentDownloader.Core.Releases;

public record ReleaseInfo
{
    public required string IndexerName { get; init; }
    public required string TorrentId { get; init; }
    public required string Title { get; init; }
    public string? DetailUrl { get; init; }
    public string? MagnetUri { get; init; }
    public string? DownloadUrl { get; init; }
    public string? InfoHash { get; init; }
    public long SizeBytes { get; init; }
    public int Seeders { get; init; }
    public int Leechers { get; init; }
    public int IndexerPriority { get; init; }
    public DateTimeOffset? PublishedAt { get; init; }
}
```

- [ ] **Step 4: Write the profile value types**

Create `src/NoMercy.Plugin.TorrentDownloader.Core/Profiles/QualityDefinition.cs`:

```csharp
using NoMercy.Plugin.TorrentDownloader.Core.Releases;

namespace NoMercy.Plugin.TorrentDownloader.Core.Profiles;

public record QualityDefinition(string Name, Resolution Resolution, ReleaseSource Source)
{
    public bool Matches(Quality quality) =>
        Resolution == quality.Resolution
        && (Source == ReleaseSource.Unknown || Source == quality.Source);

    public bool IsSourceSpecific => Source != ReleaseSource.Unknown;
}
```

Create `src/NoMercy.Plugin.TorrentDownloader.Core/Profiles/QualityLadder.cs`:

```csharp
using NoMercy.Plugin.TorrentDownloader.Core.Releases;

namespace NoMercy.Plugin.TorrentDownloader.Core.Profiles;

public record QualityLadder(IReadOnlyList<QualityDefinition> Ordered, string CutoffName)
{
    public int RankOf(Quality quality)
    {
        int specific = -1;
        int agnostic = -1;

        for (int index = 0; index < Ordered.Count; index++)
        {
            QualityDefinition definition = Ordered[index];
            if (!definition.Matches(quality))
                continue;

            if (definition.IsSourceSpecific)
                specific = index;
            else if (agnostic < 0)
                agnostic = index;
        }

        return specific >= 0 ? specific : agnostic;
    }

    public int CutoffRank
    {
        get
        {
            for (int index = 0; index < Ordered.Count; index++)
            {
                if (string.Equals(Ordered[index].Name, CutoffName, StringComparison.OrdinalIgnoreCase))
                    return index;
            }

            return int.MaxValue;
        }
    }

    public bool IsAllowed(Quality quality) => RankOf(quality) >= 0;

    public bool MeetsCutoff(Quality quality)
    {
        int rank = RankOf(quality);
        return rank >= 0 && rank >= CutoffRank;
    }
}
```

Create `src/NoMercy.Plugin.TorrentDownloader.Core/Profiles/LanguageProfile.cs`:

```csharp
namespace NoMercy.Plugin.TorrentDownloader.Core.Profiles;

public record LanguageProfile(
    IReadOnlyList<string> Required,
    IReadOnlyList<string> Preferred,
    IReadOnlyList<string> Forbidden,
    bool RequireDualAudio
)
{
    public static LanguageProfile EnglishOnly { get; } = new(["English"], [], [], false);
}
```

Create `src/NoMercy.Plugin.TorrentDownloader.Core/Profiles/GroupPreference.cs`:

```csharp
namespace NoMercy.Plugin.TorrentDownloader.Core.Profiles;

public record GroupPreference(string Group, int Score);
```

Create `src/NoMercy.Plugin.TorrentDownloader.Core/Profiles/TermRule.cs`:

```csharp
namespace NoMercy.Plugin.TorrentDownloader.Core.Profiles;

public enum TermKind
{
    Required = 0,
    Forbidden = 1,
    Preferred = 2,
}

public record TermRule(string Pattern, TermKind Kind, int Score);
```

Create `src/NoMercy.Plugin.TorrentDownloader.Core/Profiles/ReleaseProfile.cs`:

```csharp
using NoMercy.Plugin.TorrentDownloader.Core.Releases;

namespace NoMercy.Plugin.TorrentDownloader.Core.Profiles;

public record ReleaseProfile
{
    public required string Name { get; init; }
    public required QualityLadder Quality { get; init; }
    public LanguageProfile Language { get; init; } = LanguageProfile.EnglishOnly;
    public VideoCodec Codec { get; init; } = VideoCodec.Unknown;
    public IReadOnlyList<string> BlockedGroups { get; init; } = [];
    public IReadOnlyList<GroupPreference> PreferredGroups { get; init; } = [];
    public IReadOnlyList<TermRule> Terms { get; init; } = [];
    public long? MinSizeBytes { get; init; }
    public long? MaxSizeBytes { get; init; }
    public int MinSeeders { get; init; }
    public bool AllowSeasonPacks { get; init; }
}
```

- [ ] **Step 5: Run tests to verify they pass**

Run: `dotnet test tests/NoMercy.Plugin.TorrentDownloader.Core.Tests --filter QualityLadderTests`
Expected: PASS, 6 cases (suite total 124).

- [ ] **Step 6: Commit**

```bash
git add src tests
git commit -m "feat(core): add release profile types and the quality ladder"
```

---

## Task 8:8 ReleaseFilter

**Files:**
- Create: `src/NoMercy.Plugin.TorrentDownloader.Core/Profiles/FilterVerdict.cs`
- Create: `src/NoMercy.Plugin.TorrentDownloader.Core/Profiles/FilterContext.cs`
- Create: `src/NoMercy.Plugin.TorrentDownloader.Core/Profiles/ReleaseFilter.cs`
- Test: `tests/NoMercy.Plugin.TorrentDownloader.Core.Tests/Profiles/ReleaseFilterTests.cs`

**Interfaces:**
- Consumes: `ReleaseInfo`, `ParsedRelease`, `TitleMatcher`, `EpisodeSlot` (Tasks 2–7).
- Produces:
  - `record FilterVerdict(bool Accepted, string Reason)` with `FilterVerdict.Accept()` and `FilterVerdict.Reject(string)`
  - `record FilterContext(string ShowName, EpisodeSlot? WantedSlot, ReleaseProfile Profile, IReadOnlySet<string> BlacklistedNormalisedTitles, IReadOnlySet<string> BlacklistedInfoHashes)`
  - `ReleaseFilter.Evaluate(ReleaseInfo release, ParsedRelease parsed, FilterContext context) : FilterVerdict`

**Every rejection carries a reason, and the reason is user-visible** — the interactive-search view shows it per candidate. A reason of "rejected" is a bug; it must say which rule and what the values were.

Checks run in the order the spec lists them so a reader can diff the two. `WantedSlot` of `null` means unconstrained, which is what a browse-style cycle passes.

- [ ] **Step 1: Write the failing test**

Create `tests/NoMercy.Plugin.TorrentDownloader.Core.Tests/Profiles/ReleaseFilterTests.cs`:

```csharp
using FluentAssertions;
using NoMercy.Plugin.TorrentDownloader.Core.Profiles;
using NoMercy.Plugin.TorrentDownloader.Core.Releases;
using Xunit;

namespace NoMercy.Plugin.TorrentDownloader.Core.Tests.Profiles;

public class ReleaseFilterTests
{
    private static ReleaseProfile Profile() =>
        new()
        {
            Name = "default",
            Quality = new QualityLadder(
                [
                    new QualityDefinition("WEB-720p", Resolution.Hd720, ReleaseSource.Unknown),
                    new QualityDefinition("WEB-1080p", Resolution.Fhd1080, ReleaseSource.Unknown),
                ],
                "WEB-1080p"
            ),
            MinSeeders = 3,
            MaxSizeBytes = 10L * 1024 * 1024 * 1024,
        };

    private static FilterContext Context(
        ReleaseProfile? profile = null,
        EpisodeSlot? slot = null,
        IReadOnlySet<string>? titles = null,
        IReadOnlySet<string>? hashes = null
    ) =>
        new(
            "Silo",
            slot ?? new EpisodeSlot(3, 4),
            profile ?? Profile(),
            titles ?? new HashSet<string>(),
            hashes ?? new HashSet<string>()
        );

    private static ReleaseInfo Release(
        string title,
        int seeders = 50,
        long size = 2L * 1024 * 1024 * 1024,
        string? infoHash = null
    ) =>
        new()
        {
            IndexerName = "test",
            TorrentId = "1",
            Title = title,
            Seeders = seeders,
            SizeBytes = size,
            InfoHash = infoHash,
        };

    private static FilterVerdict Evaluate(ReleaseInfo release, FilterContext context) =>
        new ReleaseFilter().Evaluate(release, ReleaseNameParser.Parse(release.Title), context);

    [Fact]
    public void Evaluate_AcceptsAReleaseThatPassesEveryRule()
    {
        FilterVerdict verdict = Evaluate(
            Release("Silo.S03E04.1080p.WEB-DL.H264-CAKES"),
            Context()
        );

        verdict.Accepted.Should().BeTrue();
    }

    [Fact]
    public void Evaluate_RejectsAReleaseOfADifferentShow()
    {
        FilterVerdict verdict = Evaluate(
            Release("Lucky.Hank.S03E04.1080p.WEB-DL.H264-CAKES"),
            Context()
        );

        verdict.Accepted.Should().BeFalse();
        verdict.Reason.Should().Contain("show name");
    }

    [Fact]
    public void Evaluate_RejectsAReleaseOfADifferentEpisode()
    {
        FilterVerdict verdict = Evaluate(
            Release("Silo.S03E05.1080p.WEB-DL.H264-CAKES"),
            Context()
        );

        verdict.Accepted.Should().BeFalse();
        verdict.Reason.Should().Contain("S03E04");
    }

    [Fact]
    public void Evaluate_RejectsASeasonPackWhenPacksAreNotAllowed()
    {
        FilterVerdict verdict = Evaluate(Release("Silo.S03.1080p.WEB-DL.H264-CAKES"), Context());

        verdict.Accepted.Should().BeFalse();
        verdict.Reason.Should().Contain("season pack");
    }

    [Fact]
    public void Evaluate_AcceptsASeasonPackOfTheWantedSeasonWhenPacksAreAllowed()
    {
        ReleaseProfile profile = Profile() with { AllowSeasonPacks = true };

        FilterVerdict verdict = Evaluate(
            Release("Silo.S03.1080p.WEB-DL.H264-CAKES"),
            Context(profile)
        );

        verdict.Accepted.Should().BeTrue();
    }

    [Fact]
    public void Evaluate_RejectsAReleaseMissingARequiredLanguage()
    {
        ReleaseProfile profile = Profile() with
        {
            Language = new LanguageProfile(["Japanese"], [], [], false),
        };

        FilterVerdict verdict = Evaluate(
            Release("Silo.S03E04.1080p.WEB-DL.H264-CAKES"),
            Context(profile)
        );

        verdict.Accepted.Should().BeFalse();
        verdict.Reason.Should().Contain("Japanese");
    }

    [Fact]
    public void Evaluate_RejectsAReleaseCarryingAForbiddenLanguage()
    {
        ReleaseProfile profile = Profile() with
        {
            Language = new LanguageProfile(["English"], [], ["German"], false),
        };

        FilterVerdict verdict = Evaluate(
            Release("Silo.S03E04.GERMAN.ENG.1080p.WEB-DL.H264-CAKES"),
            Context(profile)
        );

        verdict.Accepted.Should().BeFalse();
        verdict.Reason.Should().Contain("German");
    }

    [Fact]
    public void Evaluate_RejectsANonDualAudioReleaseWhenDualAudioIsRequired()
    {
        ReleaseProfile profile = Profile() with
        {
            Language = new LanguageProfile(["English"], [], [], true),
        };

        FilterVerdict verdict = Evaluate(
            Release("Silo.S03E04.1080p.WEB-DL.H264-CAKES"),
            Context(profile)
        );

        verdict.Accepted.Should().BeFalse();
        verdict.Reason.Should().Contain("dual audio");
    }

    [Fact]
    public void Evaluate_RejectsABlockedReleaseGroup()
    {
        ReleaseProfile profile = Profile() with { BlockedGroups = ["CAKES"] };

        FilterVerdict verdict = Evaluate(
            Release("Silo.S03E04.1080p.WEB-DL.H264-CAKES"),
            Context(profile)
        );

        verdict.Accepted.Should().BeFalse();
        verdict.Reason.Should().Contain("CAKES");
    }

    [Fact]
    public void Evaluate_RejectsAReleaseMissingARequiredTerm()
    {
        ReleaseProfile profile = Profile() with
        {
            Terms = [new TermRule("AMZN", TermKind.Required, 0)],
        };

        FilterVerdict verdict = Evaluate(
            Release("Silo.S03E04.1080p.WEB-DL.H264-CAKES"),
            Context(profile)
        );

        verdict.Accepted.Should().BeFalse();
        verdict.Reason.Should().Contain("AMZN");
    }

    [Fact]
    public void Evaluate_RejectsAReleaseCarryingAForbiddenTerm()
    {
        ReleaseProfile profile = Profile() with
        {
            Terms = [new TermRule("HDR", TermKind.Forbidden, 0)],
        };

        FilterVerdict verdict = Evaluate(
            Release("Silo.S03E04.1080p.HDR.WEB-DL.H264-CAKES"),
            Context(profile)
        );

        verdict.Accepted.Should().BeFalse();
        verdict.Reason.Should().Contain("HDR");
    }

    [Fact]
    public void Evaluate_RejectsAQualityThatIsNotOnTheLadder()
    {
        FilterVerdict verdict = Evaluate(
            Release("Silo.S03E04.2160p.WEB-DL.H264-CAKES"),
            Context()
        );

        verdict.Accepted.Should().BeFalse();
        verdict.Reason.Should().Contain("quality");
    }

    [Fact]
    public void Evaluate_RejectsTheWrongCodecWhenTheProfileNamesOne()
    {
        ReleaseProfile profile = Profile() with { Codec = VideoCodec.H264 };

        FilterVerdict verdict = Evaluate(
            Release("Silo.S03E04.1080p.WEB-DL.x265-CAKES"),
            Context(profile)
        );

        verdict.Accepted.Should().BeFalse();
        verdict.Reason.Should().Contain("codec");
    }

    [Fact]
    public void Evaluate_RejectsAReleaseOverTheSizeLimit()
    {
        FilterVerdict verdict = Evaluate(
            Release("Silo.S03E04.1080p.WEB-DL.H264-CAKES", size: 40L * 1024 * 1024 * 1024),
            Context()
        );

        verdict.Accepted.Should().BeFalse();
        verdict.Reason.Should().Contain("size");
    }

    [Fact]
    public void Evaluate_RejectsAReleaseBelowTheSeederFloor()
    {
        FilterVerdict verdict = Evaluate(
            Release("Silo.S03E04.1080p.WEB-DL.H264-CAKES", seeders: 1),
            Context()
        );

        verdict.Accepted.Should().BeFalse();
        verdict.Reason.Should().Contain("seeders");
    }

    [Fact]
    public void Evaluate_RejectsABlacklistedTitle()
    {
        HashSet<string> titles = [TitleMatcher.Normalize("Silo.S03E04.1080p.WEB-DL.H264-CAKES")];

        FilterVerdict verdict = Evaluate(
            Release("Silo.S03E04.1080p.WEB-DL.H264-CAKES"),
            Context(titles: titles)
        );

        verdict.Accepted.Should().BeFalse();
        verdict.Reason.Should().Contain("release title is blacklisted");
    }

    [Fact]
    public void Evaluate_RejectsABlacklistedInfoHashRegardlessOfCase()
    {
        HashSet<string> hashes = ["abc123"];

        FilterVerdict verdict = Evaluate(
            Release("Silo.S03E04.1080p.WEB-DL.H264-CAKES", infoHash: "ABC123"),
            Context(hashes: hashes)
        );

        verdict.Accepted.Should().BeFalse();
        verdict.Reason.Should().Contain("info hash ABC123 is blacklisted");
    }

    [Fact]
    public void Evaluate_SkipsTheEpisodeCheckWhenNoSlotIsWanted()
    {
        FilterContext context = new(
            "Silo",
            null,
            Profile(),
            new HashSet<string>(),
            new HashSet<string>()
        );

        FilterVerdict verdict = Evaluate(
            Release("Silo.S03E09.1080p.WEB-DL.H264-CAKES"),
            context
        );

        verdict.Accepted.Should().BeTrue();
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/NoMercy.Plugin.TorrentDownloader.Core.Tests --filter ReleaseFilterTests`
Expected: FAIL — `FilterVerdict`, `FilterContext` and `ReleaseFilter` do not exist.

- [ ] **Step 3: Write the verdict and context**

Create `src/NoMercy.Plugin.TorrentDownloader.Core/Profiles/FilterVerdict.cs`:

```csharp
namespace NoMercy.Plugin.TorrentDownloader.Core.Profiles;

public record FilterVerdict(bool Accepted, string Reason)
{
    public static FilterVerdict Accept() => new(true, "match");

    public static FilterVerdict Reject(string reason) => new(false, reason);
}
```

Create `src/NoMercy.Plugin.TorrentDownloader.Core/Profiles/FilterContext.cs`:

```csharp
using NoMercy.Plugin.TorrentDownloader.Core.Releases;

namespace NoMercy.Plugin.TorrentDownloader.Core.Profiles;

public record FilterContext(
    string ShowName,
    EpisodeSlot? WantedSlot,
    ReleaseProfile Profile,
    IReadOnlySet<string> BlacklistedNormalisedTitles,
    IReadOnlySet<string> BlacklistedInfoHashes
);
```

- [ ] **Step 4: Write the filter**

Create `src/NoMercy.Plugin.TorrentDownloader.Core/Profiles/ReleaseFilter.cs`:

```csharp
using System.Text.RegularExpressions;
using NoMercy.Plugin.TorrentDownloader.Core.Releases;

namespace NoMercy.Plugin.TorrentDownloader.Core.Profiles;

public class ReleaseFilter
{
    private const double Gigabyte = 1024d * 1024d * 1024d;

    public FilterVerdict Evaluate(ReleaseInfo release, ParsedRelease parsed, FilterContext context)
    {
        ReleaseProfile profile = context.Profile;

        if (!TitleMatcher.Matches(release.Title, context.ShowName))
            return FilterVerdict.Reject($"show name \"{context.ShowName}\" does not lead or end the title scope");

        FilterVerdict slotVerdict = CheckSlot(parsed, context);
        if (!slotVerdict.Accepted)
            return slotVerdict;

        FilterVerdict languageVerdict = CheckLanguage(parsed, profile.Language);
        if (!languageVerdict.Accepted)
            return languageVerdict;

        if (parsed.ReleaseGroup is string group
            && profile.BlockedGroups.Contains(group, StringComparer.OrdinalIgnoreCase))
            return FilterVerdict.Reject($"blocked release group: {group}");

        FilterVerdict termVerdict = CheckTerms(release.Title, profile);
        if (!termVerdict.Accepted)
            return termVerdict;

        if (!profile.Quality.IsAllowed(parsed.Quality))
            return FilterVerdict.Reject($"quality {parsed.Quality} is not on the {profile.Name} ladder");

        if (profile.Codec != VideoCodec.Unknown && parsed.Codec != profile.Codec)
            return FilterVerdict.Reject($"codec {parsed.Codec} is not the wanted {profile.Codec}");

        FilterVerdict sizeVerdict = CheckSize(release, profile);
        if (!sizeVerdict.Accepted)
            return sizeVerdict;

        if (profile.MinSeeders > 0 && release.Seeders < profile.MinSeeders)
            return FilterVerdict.Reject($"seeders {release.Seeders} below minimum {profile.MinSeeders}");

        return CheckBlacklist(release, context);
    }

    private static FilterVerdict CheckSlot(ParsedRelease parsed, FilterContext context)
    {
        if (context.WantedSlot is not EpisodeSlot slot)
            return FilterVerdict.Accept();

        if (parsed.Episode is EpisodeSlot found)
        {
            return found == slot
                ? FilterVerdict.Accept()
                : FilterVerdict.Reject($"release is {found}, not the wanted {slot}");
        }

        if (parsed.SeasonPack is int packSeason)
        {
            if (!context.Profile.AllowSeasonPacks)
                return FilterVerdict.Reject("season pack not allowed by profile");

            return packSeason == slot.Season
                ? FilterVerdict.Accept()
                : FilterVerdict.Reject($"season pack S{packSeason:00} is not season {slot.Season:00}");
        }

        return FilterVerdict.Reject("no episode or season number found in title");
    }

    private static FilterVerdict CheckLanguage(ParsedRelease parsed, LanguageProfile language)
    {
        foreach (string forbidden in language.Forbidden)
        {
            if (parsed.Languages.Contains(forbidden, StringComparer.OrdinalIgnoreCase))
                return FilterVerdict.Reject($"forbidden language: {forbidden}");
        }

        if (language.Required.Count > 0
            && !language.Required.Any(required =>
                parsed.Languages.Contains(required, StringComparer.OrdinalIgnoreCase)))
            return FilterVerdict.Reject(
                $"language {string.Join("/", parsed.Languages)} is none of the required: "
                    + string.Join(", ", language.Required)
            );

        if (language.RequireDualAudio && !parsed.IsDualAudio)
            return FilterVerdict.Reject("not a dual audio release");

        return FilterVerdict.Accept();
    }

    private static FilterVerdict CheckTerms(string title, ReleaseProfile profile)
    {
        foreach (TermRule term in profile.Terms)
        {
            bool present = Regex.IsMatch(title, term.Pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

            if (term.Kind == TermKind.Required && !present)
                return FilterVerdict.Reject($"required term missing: {term.Pattern}");

            if (term.Kind == TermKind.Forbidden && present)
                return FilterVerdict.Reject($"forbidden term present: {term.Pattern}");
        }

        return FilterVerdict.Accept();
    }

    private static FilterVerdict CheckSize(ReleaseInfo release, ReleaseProfile profile)
    {
        if (profile.MaxSizeBytes is long max && release.SizeBytes > max)
            return FilterVerdict.Reject(
                $"size {release.SizeBytes / Gigabyte:F1} GB over limit {max / Gigabyte:F1} GB"
            );

        if (profile.MinSizeBytes is long min && release.SizeBytes > 0 && release.SizeBytes < min)
            return FilterVerdict.Reject(
                $"size {release.SizeBytes / Gigabyte:F1} GB under floor {min / Gigabyte:F1} GB"
            );

        return FilterVerdict.Accept();
    }

    private static FilterVerdict CheckBlacklist(ReleaseInfo release, FilterContext context)
    {
        if (context.BlacklistedNormalisedTitles.Contains(TitleMatcher.Normalize(release.Title)))
            return FilterVerdict.Reject("this release title is blacklisted for this episode");

        if (release.InfoHash is string hash
            && context.BlacklistedInfoHashes.Contains(hash.ToLowerInvariant()))
            return FilterVerdict.Reject($"info hash {hash} is blacklisted for this episode");

        return FilterVerdict.Accept();
    }
}
```

- [ ] **Step 5: Run tests to verify they pass**

Run: `dotnet test tests/NoMercy.Plugin.TorrentDownloader.Core.Tests --filter ReleaseFilterTests`
Expected: PASS, 18 cases (suite total 142).

- [ ] **Step 6: Commit**

```bash
git add src tests
git commit -m "feat(core): add ReleaseFilter with a user-visible reason on every rejection"
```

---

## Task 9:9 ReleaseScorer

**Files:**
- Create: `src/NoMercy.Plugin.TorrentDownloader.Core/Profiles/ScoreContext.cs`
- Create: `src/NoMercy.Plugin.TorrentDownloader.Core/Profiles/ReleaseScorer.cs`
- Test: `tests/NoMercy.Plugin.TorrentDownloader.Core.Tests/Profiles/ReleaseScorerTests.cs`

**Interfaces:**
- Consumes: `ReleaseInfo`, `ParsedRelease`, `ReleaseProfile`, `TitleMatcher.Normalize`.
- Produces:
  - `record ScoreContext(ReleaseProfile Profile, string? AnnouncedSceneTitle)`
  - `ReleaseScorer.Score(ReleaseInfo release, ParsedRelease parsed, ScoreContext context) : int`

**The weights are the design.** A quality step is worth `10_000`, every other signal is worth less than that, and seeders are log-scaled to a maximum of roughly `230`. That ordering is what makes "a 1080p release with 4 seeders beats a 720p release with 5000 seeders" true, which is the behaviour users actually want and which naive seeder-sorting gets wrong.

- [ ] **Step 1: Write the failing test**

Create `tests/NoMercy.Plugin.TorrentDownloader.Core.Tests/Profiles/ReleaseScorerTests.cs`:

```csharp
using FluentAssertions;
using NoMercy.Plugin.TorrentDownloader.Core.Profiles;
using NoMercy.Plugin.TorrentDownloader.Core.Releases;
using Xunit;

namespace NoMercy.Plugin.TorrentDownloader.Core.Tests.Profiles;

public class ReleaseScorerTests
{
    private static ReleaseProfile Profile() =>
        new()
        {
            Name = "default",
            Quality = new QualityLadder(
                [
                    new QualityDefinition("WEB-720p", Resolution.Hd720, ReleaseSource.Unknown),
                    new QualityDefinition("WEB-1080p", Resolution.Fhd1080, ReleaseSource.Unknown),
                ],
                "WEB-1080p"
            ),
        };

    private static ReleaseInfo Release(string title, int seeders = 10, int indexerPriority = 0) =>
        new()
        {
            IndexerName = "test",
            TorrentId = "1",
            Title = title,
            Seeders = seeders,
            IndexerPriority = indexerPriority,
        };

    private static int Score(ReleaseInfo release, ScoreContext context) =>
        new ReleaseScorer().Score(release, ReleaseNameParser.Parse(release.Title), context);

    [Fact]
    public void Score_RanksAQualityStepAboveAnySeederDifference()
    {
        ScoreContext context = new(Profile(), null);

        int betterQuality = Score(Release("Silo.S03E04.1080p.WEB.H264-A", seeders: 4), context);
        int moreSeeders = Score(Release("Silo.S03E04.720p.WEB.H264-B", seeders: 5000), context);

        betterQuality.Should().BeGreaterThan(moreSeeders);
    }

    [Fact]
    public void Score_UsesSeedersOnlyAsATieBreak()
    {
        ScoreContext context = new(Profile(), null);

        int many = Score(Release("Silo.S03E04.1080p.WEB.H264-A", seeders: 5000), context);
        int few = Score(Release("Silo.S03E04.1080p.WEB.H264-A", seeders: 4), context);

        many.Should().BeGreaterThan(few);
        (many - few).Should().BeLessThan(10_000);
    }

    [Fact]
    public void Score_BoostsTheExactAnnouncedSceneRelease()
    {
        ScoreContext context = new(Profile(), "Silo.S03E04.1080p.WEB.H264-CAKES");

        int announced = Score(Release("Silo.S03E04.1080p.WEB.H264-CAKES"), context);
        int other = Score(Release("Silo.S03E04.1080p.WEB.H264-OTHER"), context);

        announced.Should().BeGreaterThan(other);
    }

    [Fact]
    public void Score_AppliesPreferredGroupWeightInBothDirections()
    {
        ReleaseProfile profile = Profile() with
        {
            PreferredGroups = [new GroupPreference("CAKES", 10), new GroupPreference("BAD", -10)],
        };
        ScoreContext context = new(profile, null);

        int preferred = Score(Release("Silo.S03E04.1080p.WEB.H264-CAKES"), context);
        int neutral = Score(Release("Silo.S03E04.1080p.WEB.H264-NEUTRAL"), context);
        int discouraged = Score(Release("Silo.S03E04.1080p.WEB.H264-BAD"), context);

        preferred.Should().BeGreaterThan(neutral);
        discouraged.Should().BeLessThan(neutral);
    }

    [Fact]
    public void Score_RewardsPreferredTerms()
    {
        ReleaseProfile profile = Profile() with
        {
            Terms = [new TermRule("AMZN", TermKind.Preferred, 5)],
        };
        ScoreContext context = new(profile, null);

        int withTerm = Score(Release("Silo.S03E04.1080p.AMZN.WEB.H264-A"), context);
        int withoutTerm = Score(Release("Silo.S03E04.1080p.WEB.H264-A"), context);

        withTerm.Should().BeGreaterThan(withoutTerm);
    }

    [Fact]
    public void Score_RewardsProperAndRepack()
    {
        ScoreContext context = new(Profile(), null);

        int proper = Score(Release("Silo.S03E04.PROPER.1080p.WEB.H264-A"), context);
        int plain = Score(Release("Silo.S03E04.1080p.WEB.H264-A"), context);

        proper.Should().BeGreaterThan(plain);
    }

    [Fact]
    public void Score_RewardsDualAudioOnlyWhenTheProfileWantsIt()
    {
        ReleaseProfile wanting = Profile() with
        {
            Language = new LanguageProfile(["English"], ["Japanese"], [], true),
        };

        int scoredWhenWanted = Score(
            Release("Frieren.S01E01.1080p.WEB.Dual.Audio.H264-A"),
            new ScoreContext(wanting, null)
        );
        int scoredWhenIndifferent = Score(
            Release("Frieren.S01E01.1080p.WEB.Dual.Audio.H264-A"),
            new ScoreContext(Profile(), null)
        );

        scoredWhenWanted.Should().BeGreaterThan(scoredWhenIndifferent);
    }

    [Fact]
    public void Score_RewardsHigherIndexerPriority()
    {
        ScoreContext context = new(Profile(), null);

        int trusted = Score(Release("Silo.S03E04.1080p.WEB.H264-A", indexerPriority: 10), context);
        int ordinary = Score(Release("Silo.S03E04.1080p.WEB.H264-A", indexerPriority: 0), context);

        trusted.Should().BeGreaterThan(ordinary);
    }

    [Fact]
    public void Score_NeverLetsPreferencesOutrankAQualityStep()
    {
        ReleaseProfile profile = Profile() with
        {
            PreferredGroups = [new GroupPreference("HUGE", 500)],
        };
        ScoreContext context = new(profile, null);

        int lowerQualityPreferredGroup = Score(Release("Silo.S03E04.720p.WEB.H264-HUGE"), context);
        int higherQualityOtherGroup = Score(Release("Silo.S03E04.1080p.WEB.H264-OTHER"), context);

        higherQualityOtherGroup.Should().BeGreaterThan(lowerQualityPreferredGroup);
    }

    [Fact]
    public void Score_HandlesAQualityNotOnTheLadderWithoutThrowing()
    {
        ScoreContext context = new(Profile(), null);

        Action act = () => Score(Release("Silo.S03E04.2160p.WEB.H264-A"), context);

        act.Should().NotThrow();
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/NoMercy.Plugin.TorrentDownloader.Core.Tests --filter ReleaseScorerTests`
Expected: FAIL — `ScoreContext` and `ReleaseScorer` do not exist.

- [ ] **Step 3: Write the context**

Create `src/NoMercy.Plugin.TorrentDownloader.Core/Profiles/ScoreContext.cs`:

```csharp
namespace NoMercy.Plugin.TorrentDownloader.Core.Profiles;

public record ScoreContext(ReleaseProfile Profile, string? AnnouncedSceneTitle);
```

- [ ] **Step 4: Write the scorer**

Create `src/NoMercy.Plugin.TorrentDownloader.Core/Profiles/ReleaseScorer.cs`:

```csharp
using System.Text.RegularExpressions;
using NoMercy.Plugin.TorrentDownloader.Core.Releases;

namespace NoMercy.Plugin.TorrentDownloader.Core.Profiles;

public class ReleaseScorer
{
    private const int QualityStep = 10_000;
    private const int SceneMatchBonus = 5_000;
    private const int GroupScoreScale = 100;
    private const int TermScoreScale = 100;
    private const int DualAudioBonus = 750;
    private const int ProperBonus = 500;
    private const int RepackBonus = 500;
    private const int PreferredLanguageBonus = 500;
    private const int CodecMatchBonus = 250;
    private const int IndexerPriorityScale = 50;
    private const double SeederScale = 25d;
    private const int MaxSoftScore = QualityStep - 1;

    public int Score(ReleaseInfo release, ParsedRelease parsed, ScoreContext context)
    {
        ReleaseProfile profile = context.Profile;
        int quality = Math.Max(profile.Quality.RankOf(parsed.Quality), 0);

        long soft = SceneScore(release, context);
        soft += GroupScore(parsed, profile);
        soft += TermScore(release.Title, profile);
        soft += FlagScore(parsed);
        soft += LanguageScore(parsed, profile);
        soft += CodecScore(parsed, profile);
        soft += release.IndexerPriority * IndexerPriorityScale;
        soft += (int)(Math.Log(1d + Math.Max(release.Seeders, 0)) * SeederScale);

        // Group and term scores are user-supplied and unbounded, and are multiplied by 100.
        // Clamping the whole soft total is what keeps "one quality step outranks every other
        // signal combined" true by construction rather than by convention.
        return quality * QualityStep + (int)Math.Clamp(soft, -MaxSoftScore, MaxSoftScore);
    }

    private static int SceneScore(ReleaseInfo release, ScoreContext context)
    {
        if (context.AnnouncedSceneTitle is not string announced)
            return 0;

        return TitleMatcher.Normalize(release.Title) == TitleMatcher.Normalize(announced)
            ? SceneMatchBonus
            : 0;
    }

    private static int GroupScore(ParsedRelease parsed, ReleaseProfile profile)
    {
        if (parsed.ReleaseGroup is not string group)
            return 0;

        int total = 0;
        foreach (GroupPreference preference in profile.PreferredGroups)
        {
            if (string.Equals(preference.Group, group, StringComparison.OrdinalIgnoreCase))
                total += preference.Score * GroupScoreScale;
        }

        return total;
    }

    private static int TermScore(string title, ReleaseProfile profile)
    {
        int total = 0;
        foreach (TermRule term in profile.Terms)
        {
            if (term.Kind != TermKind.Preferred)
                continue;

            if (Regex.IsMatch(title, term.Pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
                total += term.Score * TermScoreScale;
        }

        return total;
    }

    private static int FlagScore(ParsedRelease parsed) =>
        (parsed.IsProper ? ProperBonus : 0) + (parsed.IsRepack ? RepackBonus : 0);

    private static int LanguageScore(ParsedRelease parsed, ReleaseProfile profile)
    {
        int total = 0;

        if (profile.Language.RequireDualAudio && parsed.IsDualAudio)
            total += DualAudioBonus;

        foreach (string preferred in profile.Language.Preferred)
        {
            if (parsed.Languages.Contains(preferred, StringComparer.OrdinalIgnoreCase))
                total += PreferredLanguageBonus;
        }

        return total;
    }

    private static int CodecScore(ParsedRelease parsed, ReleaseProfile profile) =>
        profile.Codec != VideoCodec.Unknown && parsed.Codec == profile.Codec ? CodecMatchBonus : 0;
}
```

- [ ] **Step 5: Run tests to verify they pass**

Run: `dotnet test tests/NoMercy.Plugin.TorrentDownloader.Core.Tests --filter ReleaseScorerTests`
Expected: PASS, 10 cases (suite total 152).

- [ ] **Step 6: Commit**

```bash
git add src tests
git commit -m "feat(core): add ReleaseScorer with quality-dominant weighting"
```

---

## Task 10:10 The decision, end to end

**Files:**
- Create: `src/NoMercy.Plugin.TorrentDownloader.Core/Profiles/ReleaseDecider.cs`
- Test: `tests/NoMercy.Plugin.TorrentDownloader.Core.Tests/Profiles/DecisionTests.cs`

**Interfaces:**
- Consumes: `ReleaseFilter` (Task 8), `ReleaseScorer` (Task 9), `ReleaseNameParser` (Tasks 2–5).
- Produces:
  - `record CandidateVerdict(ReleaseInfo Release, ParsedRelease Parsed, FilterVerdict Verdict, int Score)`
  - `ReleaseDecider.Evaluate(IEnumerable<ReleaseInfo> releases, FilterContext filter, ScoreContext score) : IReadOnlyList<CandidateVerdict>` — **every** candidate, accepted or not, ordered accepted-first then by descending score. The interactive-search view renders this list directly, which is why rejected candidates stay in it.
  - `ReleaseDecider.PickBest(IEnumerable<ReleaseInfo> releases, FilterContext filter, ScoreContext score) : CandidateVerdict?` — the single winner, or `null` when nothing passes.

This task is what Stage 0b's search orchestrator calls. It is also the integration test for everything above it.

- [ ] **Step 1: Write the failing test**

Create `tests/NoMercy.Plugin.TorrentDownloader.Core.Tests/Profiles/DecisionTests.cs`:

```csharp
using FluentAssertions;
using NoMercy.Plugin.TorrentDownloader.Core.Profiles;
using NoMercy.Plugin.TorrentDownloader.Core.Releases;
using Xunit;

namespace NoMercy.Plugin.TorrentDownloader.Core.Tests.Profiles;

public class DecisionTests
{
    private static ReleaseProfile Profile() =>
        new()
        {
            Name = "default",
            Quality = new QualityLadder(
                [
                    new QualityDefinition("WEB-720p", Resolution.Hd720, ReleaseSource.Unknown),
                    new QualityDefinition("WEB-1080p", Resolution.Fhd1080, ReleaseSource.Unknown),
                ],
                "WEB-1080p"
            ),
            MinSeeders = 2,
        };

    private static ReleaseInfo Release(string title, int seeders) =>
        new()
        {
            IndexerName = "test",
            TorrentId = title,
            Title = title,
            Seeders = seeders,
        };

    private static FilterContext Filter() =>
        new(
            "Silo",
            new EpisodeSlot(3, 4),
            Profile(),
            new HashSet<string>(),
            new HashSet<string>()
        );

    private static readonly ReleaseInfo[] Candidates =
    [
        Release("Silo.S03E04.720p.WEB.H264-HUGE", 9000),
        Release("Silo.S03E04.1080p.WEB.H264-CAKES", 12),
        Release("Silo.S03E05.1080p.WEB.H264-CAKES", 400),
        Release("Lucky.Hank.S03E04.1080p.WEB.H264-CAKES", 800),
        Release("Silo.S03E04.1080p.WEB.H264-LOWSEED", 1),
    ];

    [Fact]
    public void PickBest_ChoosesQualityOverSeeders()
    {
        CandidateVerdict? winner = new ReleaseDecider().PickBest(
            Candidates,
            Filter(),
            new ScoreContext(Profile(), null)
        );

        winner.Should().NotBeNull();
        winner!.Release.Title.Should().Be("Silo.S03E04.1080p.WEB.H264-CAKES");
    }

    [Fact]
    public void Evaluate_KeepsRejectedCandidatesWithTheirReasons()
    {
        IReadOnlyList<CandidateVerdict> verdicts = new ReleaseDecider().Evaluate(
            Candidates,
            Filter(),
            new ScoreContext(Profile(), null)
        );

        verdicts.Should().HaveCount(5);

        CandidateVerdict wrongShow = verdicts.Single(v =>
            v.Release.Title.StartsWith("Lucky.Hank", StringComparison.Ordinal)
        );
        wrongShow.Verdict.Accepted.Should().BeFalse();
        wrongShow.Verdict.Reason.Should().Contain("show name");

        CandidateVerdict wrongEpisode = verdicts.Single(v =>
            v.Release.Title.Contains("S03E05", StringComparison.Ordinal)
        );
        wrongEpisode.Verdict.Accepted.Should().BeFalse();
        wrongEpisode.Verdict.Reason.Should().Contain("S03E04");

        CandidateVerdict lowSeed = verdicts.Single(v =>
            v.Release.Title.Contains("LOWSEED", StringComparison.Ordinal)
        );
        lowSeed.Verdict.Accepted.Should().BeFalse();
        lowSeed.Verdict.Reason.Should().Contain("seeders");
    }

    [Fact]
    public void Evaluate_OrdersAcceptedCandidatesFirstThenByDescendingScore()
    {
        IReadOnlyList<CandidateVerdict> verdicts = new ReleaseDecider().Evaluate(
            Candidates,
            Filter(),
            new ScoreContext(Profile(), null)
        );

        verdicts[0].Verdict.Accepted.Should().BeTrue();
        verdicts[0].Release.Title.Should().Be("Silo.S03E04.1080p.WEB.H264-CAKES");
        verdicts[1].Verdict.Accepted.Should().BeTrue();
        verdicts[1].Release.Title.Should().Be("Silo.S03E04.720p.WEB.H264-HUGE");
        verdicts.Skip(2).Should().OnlyContain(v => !v.Verdict.Accepted);
    }

    [Fact]
    public void PickBest_ReturnsNullWhenNothingPasses()
    {
        ReleaseInfo[] hopeless =
        [
            Release("Lucky.Hank.S03E04.1080p.WEB.H264-CAKES", 800),
            Release("Silo.S03E05.1080p.WEB.H264-CAKES", 400),
        ];

        new ReleaseDecider()
            .PickBest(hopeless, Filter(), new ScoreContext(Profile(), null))
            .Should()
            .BeNull();
    }

    [Fact]
    public void PickBest_PrefersTheAnnouncedSceneReleaseAmongEqualQualities()
    {
        ReleaseInfo[] equals =
        [
            Release("Silo.S03E04.1080p.WEB.H264-OTHER", 5000),
            Release("Silo.S03E04.1080p.WEB.H264-CAKES", 3),
        ];

        CandidateVerdict? winner = new ReleaseDecider().PickBest(
            equals,
            Filter(),
            new ScoreContext(Profile(), "Silo.S03E04.1080p.WEB.H264-CAKES")
        );

        winner!.Release.Title.Should().Be("Silo.S03E04.1080p.WEB.H264-CAKES");
    }

    [Fact]
    public void Evaluate_ReturnsAnEmptyListForNoCandidates()
    {
        new ReleaseDecider()
            .Evaluate([], Filter(), new ScoreContext(Profile(), null))
            .Should()
            .BeEmpty();
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/NoMercy.Plugin.TorrentDownloader.Core.Tests --filter DecisionTests`
Expected: FAIL — `ReleaseDecider` and `CandidateVerdict` do not exist.

- [ ] **Step 3: Write the decider**

Create `src/NoMercy.Plugin.TorrentDownloader.Core/Profiles/ReleaseDecider.cs`:

```csharp
using NoMercy.Plugin.TorrentDownloader.Core.Releases;

namespace NoMercy.Plugin.TorrentDownloader.Core.Profiles;

public record CandidateVerdict(
    ReleaseInfo Release,
    ParsedRelease Parsed,
    FilterVerdict Verdict,
    int Score
);

public class ReleaseDecider
{
    private readonly ReleaseFilter _filter = new();
    private readonly ReleaseScorer _scorer = new();

    public IReadOnlyList<CandidateVerdict> Evaluate(
        IEnumerable<ReleaseInfo> releases,
        FilterContext filter,
        ScoreContext score
    )
    {
        List<CandidateVerdict> verdicts = [];

        foreach (ReleaseInfo release in releases)
        {
            ParsedRelease parsed = ReleaseNameParser.Parse(release.Title);
            FilterVerdict verdict = _filter.Evaluate(release, parsed, filter);
            int value = verdict.Accepted ? _scorer.Score(release, parsed, score) : 0;
            verdicts.Add(new CandidateVerdict(release, parsed, verdict, value));
        }

        return verdicts
            .OrderByDescending(candidate => candidate.Verdict.Accepted)
            .ThenByDescending(candidate => candidate.Score)
            .ToList();
    }

    public CandidateVerdict? PickBest(
        IEnumerable<ReleaseInfo> releases,
        FilterContext filter,
        ScoreContext score
    )
    {
        CandidateVerdict? best = Evaluate(releases, filter, score).FirstOrDefault();
        return best is { Verdict.Accepted: true } ? best : null;
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/NoMercy.Plugin.TorrentDownloader.Core.Tests --filter DecisionTests`
Expected: PASS, 6 cases (suite total 158).

- [ ] **Step 5: Run the whole suite**

Run: `dotnet test tests/NoMercy.Plugin.TorrentDownloader.Core.Tests`
Expected: PASS, all cases across all 10 test classes.

- [ ] **Step 6: Commit**

```bash
git add src tests
git commit -m "feat(core): add ReleaseDecider selecting one winner and keeping rejection reasons"
```

---

## Plan Self-Review

**Spec coverage.** §5.2's `Releases/` module (`ReleaseInfo`, `ReleaseNameParser`, `TitleMatcher`) is Tasks 1–7. §5.2's `Profiles/` module (`ReleaseProfile`, `QualityLadder`, `LanguageProfile`, `GroupPreference`, `ReleaseFilter`, `ReleaseScorer`) is Tasks 7–9. §8.1's twelve hard filters are all in Task 8, one test each. §8.2's eight scoring signals are all in Task 9. §8.3's language model is Task 5, including the deliberate omission of `IT`/`ES`/`DE` and the promotion from reject to extraction. §8.5's season-pack allowance is covered by the slot check in Task 8. §15's named regression cases — "Lucky" vs "Lucky Hank" vs "We.Were.the.Lucky.Ones", "Special Ops Lioness", "Big Brother US", glued search-highlight tokens, `ITA.ENG` multi-audio, `[Cap.101]` foreign numbering — each appear as a test in Task 5 or Task 6.

**Deliberately out of scope, and covered by later Stage 0 plans:** indexers and the rate-limiting aggregator (0b), download clients and the completion handoff (0c), the SQLite store and cycle scheduler (0d). §8.4's upgrade-replace is not implemented anywhere by design — it is blocked on upstream #19.

**Placeholder scan.** No "TBD", no "add error handling", no "similar to Task N". Every code step carries the full text of the file or the exact block to append. `LanguageTags` is referenced in Task 4's Interfaces block before Task 5 creates it; Task 4's `ParsedRelease` gives `Languages` and `IsDualAudio` defaults so it compiles standalone, and Task 5 Step 5 wires them. That ordering is intentional and stated in both tasks.

**Type consistency.** `EpisodeSlot` is a `readonly record struct` throughout, so `==` in `CheckSlot` is value equality. `ReleaseNameParser.EpisodeMarkerIndex` is defined in Task 2 and consumed in Task 6. `TitleMatcher.Normalize` is defined in Task 6 and consumed by Task 8's blacklist and Task 9's scene match, with the blacklist test normalising on the way in so both sides agree. `ReleaseInfo` is created in Task 7 but first used in Task 8, which is why Task 7 rather than Task 8 owns it. `ReleaseProfile.Quality` is a `QualityLadder`, and `ReleaseProfile.Codec` is a `VideoCodec` — two different meanings of "quality" kept in separate properties on purpose.

**One thing an implementer should watch.** Task 3's `TelesyncPattern` uses `\b(telesync|\bts\b)\b`, and a bare `TS` is a real token in some release names that are not telesyncs. If a false positive shows up in Stage 0b against real fixtures, tighten it there rather than loosening a test here.

---

## Execution Handoff

Plan complete and saved to `docs/superpowers/plans/2026-07-29-stage-0a-release-domain.md`. Two execution options:

1. **Subagent-Driven (recommended)** — a fresh subagent per task, review between tasks, fast iteration.
2. **Inline Execution** — execute tasks in this session using executing-plans, batch execution with checkpoints.

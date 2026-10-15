using Xunit;

namespace NoMercy.Plugin.TorrentDownloader.Tests.Scan;

/// <summary>
/// The scan's rules, each on a small assembly compiled here from source.
/// </summary>
/// <remarks>
/// The check over the plugin's own build output is only worth something if the
/// scan finds what the marketplace's scan finds. These pin that one rule at a
/// time, both ways: what it must refuse, and what the compiler writes into safe
/// code that it must not.
/// </remarks>
public sealed class MarketplaceScanTests
{
    [Fact]
    public void APInvokeIsFound()
    {
        IReadOnlyList<string> findings = MarketplaceScan.Image(
            CompiledAssembly.From(
                """
                using System.Runtime.InteropServices;

                public static class Native
                {
                    [DllImport("kernel32.dll")]
                    public static extern bool CloseHandle(nint handle);
                }
                """),
            "Sample.dll",
            entry: true);

        Assert.Contains("P/Invoke or native method Native.CloseHandle", findings);
    }

    [Fact]
    public void ProcessIsFound()
    {
        IReadOnlyList<string> findings = MarketplaceScan.Image(
            CompiledAssembly.From(
                """
                public static class Starts
                {
                    public static int Current() => System.Diagnostics.Process.GetCurrentProcess().Id;
                }
                """),
            "Sample.dll",
            entry: true);

        Assert.Contains("references banned type System.Diagnostics.Process", findings);
    }

    [Fact]
    public void AnAssemblyReachedThroughATypeIsFound()
    {
        IReadOnlyList<string> findings = MarketplaceScan.Image(
            CompiledAssembly.From(
                """
                public static class Reaches
                {
                    public static string Where() => typeof(Reaches).Assembly.Location;
                }
                """),
            "Sample.dll",
            entry: true);

        Assert.Contains("references banned type System.Reflection.Assembly", findings);
    }

    [Fact]
    public void APointerLocalIsFound()
    {
        IReadOnlyList<string> findings = MarketplaceScan.Image(
            CompiledAssembly.From(
                """
                public static class Pointers
                {
                    // A fixed buffer walked in a loop, so the optimiser has to keep
                    // the pointer in a local rather than folding it away.
                    public static unsafe int Read(int[] values)
                    {
                        int sum = 0;
                        fixed (int* start = values)
                        {
                            for (int* at = start; at < start + values.Length; at++)
                            {
                                sum += *at;
                            }
                        }

                        return sum;
                    }
                }
                """,
                allowUnsafe: true),
            "Sample.dll",
            entry: true);

        Assert.Contains("Pointer local in Pointers.Read", findings);
    }

    [Fact]
    public void ATypeNameInALogLineIsNotAFinding()
    {
        // ex.GetType().Name binds to MemberInfo.get_Name, which reads a name and
        // acts on nothing. Refusing it refused every plugin that logs an
        // exception's type (media-server #475).
        IReadOnlyList<string> findings = MarketplaceScan.Image(
            CompiledAssembly.From(
                """
                public static class Names
                {
                    public static string Of(System.Exception failure) => failure.GetType().Name;
                }
                """),
            "Sample.dll",
            entry: true);

        Assert.Empty(findings);
    }

    [Fact]
    public void ACollectionExpressionWithASpreadIsNotAFinding()
    {
        // The compiler builds this List<T> through CollectionsMarshal, which is
        // in a banned namespace. Only the members it emits pass.
        IReadOnlyList<string> findings = MarketplaceScan.Image(
            CompiledAssembly.From(
                """
                using System.Collections.Generic;

                public static class Spreads
                {
                    public static List<int> Joined(List<int> first, List<int> second) => [.. first, .. second];
                }
                """),
            "Sample.dll",
            entry: true);

        Assert.Empty(findings);
    }

    [Fact]
    public void WhatOneTypeReachesIsToldApartFromItsNeighbours()
    {
        // The folder's findings are per assembly, so they cannot say which class
        // still reaches out. This can, which is what lets a slice prove it took
        // one use away while another class keeps the same finding alive.
        MemoryStream image = CompiledAssembly.From(
            """
            using System.Linq;

            namespace Sample;

            public static class Reaches
            {
                public static string Where() => typeof(Reaches).Assembly.Location;

                public static int Names() => typeof(Reaches).Assembly.GetManifestResourceNames().Count(name => name.Length > 0);
            }

            public static class Clean
            {
                public static string Name(System.Exception failure) => failure.GetType().Name;
            }
            """);

        Assert.Equal(
            ["System.Reflection.Assembly::GetManifestResourceNames", "System.Reflection.Assembly::get_Location"],
            MarketplaceScan.ReachedFrom(image, "Sample.Reaches"));

        image.Position = 0;
        Assert.Empty(MarketplaceScan.ReachedFrom(image, "Sample.Clean"));
    }

    [Fact]
    public void WhatALambdaOrAnAsyncMethodReachesCountsForItsType()
    {
        // The compiler moves a lambda's body and an async method's body into
        // nested types it makes up, so those count as the type's own.
        MemoryStream image = CompiledAssembly.From(
            """
            using System;
            using System.Threading.Tasks;

            namespace Sample;

            public static class Hidden
            {
                public static Func<string> Later() => () => typeof(Hidden).Assembly.Location;

                public static async Task<int> Soon()
                {
                    await Task.Yield();
                    return System.Diagnostics.Process.GetCurrentProcess().Id;
                }
            }
            """);

        Assert.Equal(
            ["System.Diagnostics.Process::GetCurrentProcess", "System.Diagnostics.Process::get_Id", "System.Reflection.Assembly::get_Location"],
            MarketplaceScan.ReachedFrom(image, "Sample.Hidden"));
    }

    [Fact]
    public void AFileThatIsNotILIsFound()
    {
        IReadOnlyList<string> findings = MarketplaceScan.Image(
            new MemoryStream("MZ and nothing a PE reader can use"u8.ToArray()),
            "native.dll",
            entry: false);

        Assert.Equal(["not pure IL"], findings);
    }
}

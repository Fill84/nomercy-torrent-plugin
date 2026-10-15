using NoMercy.Plugin.TorrentDownloader.Configuration;
using NoMercy.Plugin.TorrentDownloader.Storage;
using Xunit;

namespace NoMercy.Plugin.TorrentDownloader.Tests.Scan;

/// <summary>
/// The migrations and the source catalogue are read without leaving the SDK.
/// </summary>
/// <remarks>
/// Media-server #476 refuses <c>System.Reflection.Assembly</c>. Both were read
/// through it: the migrations as embedded resources, the catalogue from the
/// folder the assembly says it is in. Both now travel inside the assembly as
/// code the build writes (S16-03).
/// </remarks>
public sealed class TheShippedFilesTests
{
    [Theory]
    [InlineData(typeof(Store))]
    [InlineData(typeof(CatalogueLoader))]
    public void ReadingThemReachesNothingTheScanRefuses(Type reader)
    {
        using FileStream image = File.OpenRead(Path.Combine(AppContext.BaseDirectory, "NoMercy.Plugin.TorrentDownloader.dll"));

        Assert.Empty(MarketplaceScan.ReachedFrom(image, reader.FullName!));
    }
}

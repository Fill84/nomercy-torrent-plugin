namespace NoMercy.Plugin.TorrentDownloader.Core.Tests.TestSupport;

public static class Fixtures
{
    public static string Text(string name) => File.ReadAllText(Path(name));

    public static byte[] Bytes(string name) => File.ReadAllBytes(Path(name));

    private static string Path(string name) =>
        System.IO.Path.Combine(AppContext.BaseDirectory, "fixtures", name);
}

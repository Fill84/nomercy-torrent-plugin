namespace NoMercy.Plugin.TorrentDownloader;

/// <summary>
/// Who this plugin is, in one place, so nothing has to be written twice.
/// </summary>
/// <remarks>
/// <c>plugin.json</c> is the exception: the server reads it before any of this
/// code has run, so it carries the same facts independently and a test holds
/// the two together.
/// </remarks>
public static class PluginIdentity
{
    /// <summary>
    /// 0.3.4's id, deliberately unchanged.
    /// </summary>
    /// <remarks>
    /// It is the plugin's identity on every server that already has it: the
    /// data folder, the owner's grants and the settings all hang off it. A new
    /// id would install a second plugin beside the old one, inheriting none of
    /// that, while the old one carried on downloading.
    /// </remarks>
    public const string IdText = "1SBQT26FHF98EBRPYVRGD92CZF";

    public const string Name = "Torrent Downloader";

    public const string Description =
        "Downloads every episode missing from a TV or anime library and hands it to the encoder.";

    /// <summary>The file the server loads, and the name the manifest points at.</summary>
    public const string AssemblyFileName = "NoMercy.Plugin.TorrentDownloader.dll";

    /// <summary>The manifest, read from the assembly's own folder.</summary>
    public const string ManifestFileName = "plugin.json";

    public static Ulid Id { get; } = Ulid.Parse(IdText);

    public static Version Version { get; } = new(0, 3, 12);
}

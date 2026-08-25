using NoMercy.Plugin.TorrentDownloader.Core.Pipeline;

namespace NoMercy.Plugin.TorrentDownloader.Storage;

/// <summary>
/// One page of refusals, and enough to say where it sits in the rest.
/// </summary>
/// <remarks>
/// The total is carried because the page cannot be drawn without it: "showing
/// 1 to 50" of an unknown number says nothing, and whether there is a page
/// after this one cannot be answered from the rows alone.
/// </remarks>
/// <param name="Rows">The refusals on this page, newest first.</param>
/// <param name="Total">How many refusals there are in all.</param>
/// <param name="Page">Which page this is, counting from one.</param>
/// <param name="Size">How many rows a page holds.</param>
public sealed record SkippedPage(
    IReadOnlyList<SkippedRelease> Rows,
    int Total,
    int Page,
    int Size)
{
    /// <summary>How many pages there are, and never fewer than one.</summary>
    /// <remarks>
    /// One page when there is nothing to show: a page that reports "page 1 of
    /// 0" is arithmetic leaking onto a screen.
    /// </remarks>
    public int Pages => Math.Max(1, (Total + Size - 1) / Size);

    public bool HasPrevious => Page > 1;

    public bool HasNext => Page < Pages;

    /// <summary>The first row on this page, counting from one, or nought when it is empty.</summary>
    public int First => Rows.Count == 0 ? 0 : ((Page - 1) * Size) + 1;

    /// <summary>The last row on this page, counting from one.</summary>
    public int Last => ((Page - 1) * Size) + Rows.Count;
}

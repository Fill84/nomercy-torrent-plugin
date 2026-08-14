namespace NoMercy.Plugin.TorrentDownloader.Core.Domain;

/// <summary>
/// One show in a television or anime library, in scope for downloading.
/// </summary>
/// <remarks>
/// <para>
/// Neither of the library's episode counts is here, deliberately.
/// <c>HaveEpisodeCount</c> is the <c>Tv.HaveEpisodes</c> column and on a real
/// server it is nought for shows with hundreds of episodes on disk, so a show
/// with everything looks like a show with nothing. Two numbers that can
/// disagree must never both be trusted, and the surest way for the wrong one
/// never to be read is for it not to be here to read: presence comes from each
/// episode's own <c>HasFile</c>.
/// </para>
/// <para>
/// There is no status either. The contract does not project one, and this
/// plugin does not want one: an ended show is exactly the kind with gaps to
/// fill, so skipping it is the opposite of what backfill means.
/// </para>
/// </remarks>
/// <param name="Id">The provider's show id, which the whole contract keys on.</param>
/// <param name="Title">The library's title for it, which is not always a scene title.</param>
/// <param name="Year">
/// The year of its first air date, or null. It is what makes a show with a
/// common word for a title searchable — <c>Sugar</c> and <c>Sugar 2024</c>.
/// </param>
/// <param name="LibraryId">
/// The library it came from. Kept because a downloaded episode goes back to it,
/// so an anime episode lands in the anime library — this plugin never picks one.
/// </param>
/// <param name="Kind">Its media type, being the type of that library.</param>
/// <param name="Folder">
/// Its folder, relative to the library root. Never null or blank here: a show
/// with nowhere to download to is not in scope and never becomes a
/// <see cref="Show"/> at all.
/// </param>
public sealed record Show(
    int Id,
    string Title,
    int? Year,
    string LibraryId,
    LibraryKind Kind,
    string Folder);

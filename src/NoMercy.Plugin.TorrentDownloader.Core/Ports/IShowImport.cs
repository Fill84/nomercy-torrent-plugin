using NoMercy.Plugin.TorrentDownloader.Core.Domain;

namespace NoMercy.Plugin.TorrentDownloader.Core.Ports;

/// <summary>
/// Asking the server to put a show in a library, the way <em>Add content</em>
/// does.
/// </summary>
/// <remarks>
/// <para>
/// A torrent for a show that is in no library cannot be dispatched at all: an
/// encode is asked for by the server's own episode id, and a show with no row
/// has no episodes and no ids. Handing the files over without one asks the
/// server to guess from the file name, and that guess resolves nothing — the
/// job finishes having written nothing.
/// </para>
/// <para>
/// So the show is added first, and then it is an ordinary grab: the next tick
/// finds it in a library, matches the files to its episodes, stages them and
/// dispatches each by its own id.
/// </para>
/// <para>
/// <strong>It is one call and it is the server's own.</strong> The dashboard's
/// Add content searches the metadata providers, the owner picks one, and the
/// assignment ends in <c>DispatchJob&lt;ShowImportJob&gt;(id, libraryId)</c>.
/// Nothing here invents a route: it asks the same providers and dispatches the
/// same job.
/// </para>
/// <para>
/// A port so the cadence can be tested against an outcome. The implementation
/// reaches the server by name because the plugin contract offers no way to ask
/// a provider anything or to queue one of the server's own jobs.
/// </para>
/// </remarks>
public interface IShowImport
{
    /// <summary>
    /// Looks a show up with the server's own providers and asks for it to be
    /// imported.
    /// </summary>
    /// <param name="title">The show's title, as the release name spells it.</param>
    /// <param name="year">Its year where the release name carries one.</param>
    /// <param name="into">Which library it goes in.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>
    /// What it asked for, spelled the way the provider spells it, or null when
    /// nothing could be asked — no provider knows the show, or this server does
    /// not offer the parts.
    /// </returns>
    Task<string?> AddAsync(string title, int? year, Library into, CancellationToken ct);
}

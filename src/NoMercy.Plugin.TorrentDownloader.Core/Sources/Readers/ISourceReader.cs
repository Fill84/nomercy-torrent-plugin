namespace NoMercy.Plugin.TorrentDownloader.Core.Sources.Readers;

/// <summary>
/// Turning a page a source answered into rows.
/// </summary>
/// <remarks>
/// A reader reads and judges nothing. Whether a row is worth having is the
/// profile's business and happens later, against the copy rather than the page.
/// </remarks>
public interface ISourceReader
{
    /// <summary>The name the catalogue refers to it by.</summary>
    string Name { get; }

    /// <summary>
    /// The rows in <paramref name="body"/>.
    /// </summary>
    /// <param name="body">Exactly what the host sent.</param>
    /// <param name="from">
    /// Where it came from, so a relative address in the page can be made
    /// absolute. A detail address that is only ever relative is one nothing can
    /// follow.
    /// </param>
    IReadOnlyList<SourceRow> Read(string body, Uri from);
}

/// <summary>The readers this plugin has, by the name the catalogue uses.</summary>
/// <remarks>
/// <strong>C4.</strong> Two readers were missing from 0.3.4's registry and both
/// fell through to the generic one, which returns rows on some pages — so
/// nothing looked broken. A name that resolves to the generic reader by
/// accident is the failure; a source that asks for the generic reader on
/// purpose says so by naming no reader at all.
/// </remarks>
public sealed class Readers
{
    private readonly Dictionary<string, ISourceReader> _byName;

    public Readers(params ISourceReader[] readers)
    {
        _byName = readers.ToDictionary(reader => reader.Name, StringComparer.OrdinalIgnoreCase);
        Generic = readers.OfType<GenericReader>().FirstOrDefault() ?? new GenericReader();
    }

    /// <summary>What a source with no reader named gets.</summary>
    public GenericReader Generic { get; }

    /// <summary>Every reader by name, for the test that keeps the catalogue honest.</summary>
    public IReadOnlyDictionary<string, ISourceReader> ByName => _byName;

    /// <summary>
    /// The reader for <paramref name="name"/>, or null when nothing answers to
    /// it.
    /// </summary>
    /// <remarks>
    /// Null rather than the generic reader. Falling back silently is how two
    /// missing readers went unnoticed for a release: the generic one answers
    /// <em>something</em> on many pages, and something is indistinguishable
    /// from working.
    /// </remarks>
    public ISourceReader? Named(string? name)
    {
        return name is null ? null : _byName.GetValueOrDefault(name);
    }

    /// <summary>The reader a source should be read with.</summary>
    public ISourceReader? For(SourceDefinition source)
    {
        return source.Reader is null ? Generic : Named(source.Reader);
    }
}

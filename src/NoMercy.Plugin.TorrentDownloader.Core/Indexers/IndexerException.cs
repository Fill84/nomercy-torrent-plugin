namespace NoMercy.Plugin.TorrentDownloader.Core.Indexers;

public class IndexerException : Exception
{
    public IndexerException(string message)
        : base(message) { }

    public IndexerException(string message, Exception inner)
        : base(message, inner) { }
}

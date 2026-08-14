namespace NoMercy.Plugin.TorrentDownloader.Core.Activity;

/// <summary>
/// The stages of the chain, in the order an episode passes through them.
/// </summary>
/// <remarks>
/// Every stage reports here, and a stage that cannot be seen does not ship: an
/// episode that stops moving has to be traceable to the step it stopped at,
/// which is exactly what 0.3.4 could not answer.
/// </remarks>
public enum ActivityStage
{
    /// <summary>Reading every feed into the name pool.</summary>
    Harvest,

    /// <summary>Resolving the release name to look for, per show and season.</summary>
    Names,

    /// <summary>Searching every indexer for copies of a name.</summary>
    Find,

    /// <summary>Judging what came back, and choosing one copy or none.</summary>
    Decide,

    /// <summary>Handing the chosen copy to the torrent client.</summary>
    Grab,

    /// <summary>Watching a transfer: progress, completion, failure, stall.</summary>
    Download,

    /// <summary>Staging the finished video and queueing the encode.</summary>
    Dispatch,
}

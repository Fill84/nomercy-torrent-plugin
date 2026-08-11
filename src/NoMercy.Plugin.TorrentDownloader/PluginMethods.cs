// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Phillippe Pelzer - https://github.com/Fill84

namespace NoMercy.Plugin.TorrentDownloader;

/// <summary>
/// Every call a page can make back into the plugin, named once.
///
/// <para>
/// These used to live on the two views that happened to draw the buttons. That was fine
/// while there were two views; it stopped being fine the moment a page split moved a button
/// from one to another, because the controller's route attributes then pointed at a class
/// for reasons that had nothing to do with routing. The REST surface is a property of the
/// plugin, not of whichever page currently draws the button.
/// </para>
///
/// <para>
/// The client interpolates a method straight into the request path
/// (<c>plugins/{pluginId}/{method}</c>) and a form's submit posts its own fields as the
/// body, discarding whatever the action intent carried. So anything identifying which entry
/// is being acted on travels in the method string, which is why the per-entry names below
/// are stems with a route template beside them rather than whole method names.
/// </para>
/// </summary>
public static class PluginMethods
{
    public const string SaveSettings = "SaveSettings";
    public const string SaveIndexer = "SaveIndexer";
    public const string SavePrivateTracker = "SavePrivateTracker";

    // Add carries no index - it targets no existing entry, so there is nothing for the route
    // to parameterise. Remove needs one.
    public const string AddIndexer = "AddIndexer";
    public const string AddPrivateTracker = "AddPrivateTracker";
    public const string RemoveIndexer = "RemoveIndexer";
    public const string RemovePrivateTracker = "RemovePrivateTracker";

    public const string AddSource = "AddSource";
    public const string AddTorrent = "AddTorrent";

    public const string FollowShow = "FollowShow";
    public const string UnfollowShow = "UnfollowShow";

    // By name rather than by id, because this is the one entry point reached without a row
    // to click: the shows it can reach are precisely the ones no page lists.
    public const string FollowByName = "FollowByName";

    public const string PauseDownload = "PauseDownload";
    public const string ResumeDownload = "ResumeDownload";
    public const string CancelDownload = "CancelDownload";
    public const string AllowRelease = "AllowRelease";
    public const string SearchNow = "SearchNow";

    // Built from the constants above at compile time, so the "{method}/{index}" shape a page
    // writes and the "{method}/{index:int}" route the controller listens on cannot drift
    // into two different stems.
    public const string SaveIndexerRoute = SaveIndexer + "/{index:int}";
    public const string SavePrivateTrackerRoute = SavePrivateTracker + "/{index:int}";
    public const string RemoveIndexerRoute = RemoveIndexer + "/{index:int}";
    public const string RemovePrivateTrackerRoute = RemovePrivateTracker + "/{index:int}";

    public const string FollowShowRoute = FollowShow + "/{showId:int}";
    public const string UnfollowShowRoute = UnfollowShow + "/{showId:int}";

    // The info hash rather than a row index: the list reorders itself as downloads progress,
    // so a click on row three has to mean the torrent that was on row three.
    public const string PauseDownloadRoute = PauseDownload + "/{infoHash}";
    public const string ResumeDownloadRoute = ResumeDownload + "/{infoHash}";
    public const string CancelDownloadRoute = CancelDownload + "/{infoHash}";
    public const string AllowReleaseRoute = AllowRelease + "/{handle}";
    public const string SearchNowRoute = SearchNow + "/{showId:int}/{season:int}/{episode:int}";
}

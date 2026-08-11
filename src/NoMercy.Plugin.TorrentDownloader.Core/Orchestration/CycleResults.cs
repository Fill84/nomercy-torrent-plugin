// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Phillippe Pelzer - https://github.com/Fill84

namespace NoMercy.Plugin.TorrentDownloader.Core.Orchestration;

// What each pass came to. Records rather than counts, because every one of these started
// life as a single int and every one of them then had to answer a second question the int
// could not: matched or grabbed, searched or grabbed, imported or put back. A number whose
// meaning depends on who is reading it is a number that gets read wrong.
/// <summary>
/// What one refresh concluded.
///
/// <para>
/// More than a count, because a plugin that decides to want nothing has to be able to say
/// why. A library of sixty-seven shows can produce "0 episodes wanted" for two entirely
/// different reasons, and an owner reading only the zero concludes the thing is broken.
/// </para>
/// </summary>
/// <param name="Shows">How many the plugin is working on.</param>
/// <param name="NotOnTheServer">
/// Shows the library lists with no episode on the server. Rows from a metadata provider,
/// not media anybody has - and not this plugin's business unless it is asked for one by
/// name.
/// </param>
/// <param name="Finished">Shows that are on the server and have ended or been cancelled, so nothing more of them will ever exist.</param>
public sealed record WantedRefresh(int Wanted, int Shows, int NotOnTheServer, int Finished);

/// <summary>
/// What one pass over the feeds came to.
///
/// <para>
/// <paramref name="Matched"/> is reported beside <paramref name="Grabbed"/> because the
/// two failures look identical from outside and have nothing to do with each other. A feed
/// that matched nothing is a feed carrying other people's shows, or an indexer that is not
/// answering. A feed that matched plenty and grabbed none of it is working perfectly and
/// being turned down - by the quality profile, or because the items link to a web page
/// instead of to a torrent, which some sites' feeds do. Only one of those is worth
/// anybody's evening.
/// </para>
/// </summary>
public sealed record FeedCycle(int Matched, int Grabbed);

/// <summary>
/// What one pass over the engine came to.
/// </summary>
/// <param name="Imported">Downloads handed to the intake.</param>
/// <param name="PutBack">
/// Episodes a failed download returned to the queue.
///
/// <para>
/// Reported so the caller can search for them at once instead of at the next cadence. A
/// failed download is not a state an episode rests in - the episode is simply missing
/// again, exactly as it was before anything was grabbed, and the answer is the same answer:
/// look for another release. Waiting six hours to do that is the plugin sitting on work it
/// already knows about.
/// </para>
/// </param>
public sealed record TransfersCycle(int Imported, int PutBack);

/// <summary>
/// What one pass of the search cadence came to.
///
/// <para>
/// <paramref name="Searched"/> is reported beside <paramref name="Grabbed"/> because a
/// cadence that asks nothing and one that asks and is turned down are the same silence from
/// outside, and only one of them is a bug. The plugin spent a day in the first state - a
/// batch of ten filled entirely with unaired episodes, refetched and re-skipped every five
/// minutes - and nothing anywhere said so.
/// </para>
/// </summary>
public sealed record SearchCycle(int Searched, int Grabbed);

/// <summary>Whether a pasted link was taken, and what to tell the person who pasted it.</summary>
public sealed record ManualAdd(bool Added, string Message);

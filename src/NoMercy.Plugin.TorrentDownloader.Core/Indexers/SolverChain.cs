// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Phillippe Pelzer - https://github.com/Fill84

namespace NoMercy.Plugin.TorrentDownloader.Core.Indexers;

/// <summary>
/// Several solvers, cheapest first, and the first clearance wins.
///
/// <para>
/// The order is the point. Sending the request as a browser's identity costs one HTTP call
/// and clears most sites. Actually driving a browser costs a Chromium start-up and several
/// seconds, and clears the rest. Trying the cheap one first means the sites that never
/// needed the expensive one never pay for it.
/// </para>
/// </summary>
public sealed class FirstSolverThatWorks(params IChallengeSolver[] solvers) : IChallengeSolver, IPageSource
{
    /// <summary>
    /// Forwarded to whichever member can do it.
    ///
    /// <para>
    /// Without this the chain hides the capability: the fetch asks "can you hand me the
    /// page" of the wrapper, the wrapper says no, and the browser behind it never gets
    /// asked - so a site that only works when the browser hands over what it loaded falls
    /// straight back to the cookie replay that does not work on it.
    /// </para>
    /// </summary>
    public async Task<string?> GetPageAsync(Uri url, CancellationToken ct)
    {
        foreach (IChallengeSolver solver in solvers)
        {
            if (solver is IPageSource source && await source.GetPageAsync(url, ct) is { } page)
                return page;
        }

        return null;
    }

    public async Task<Clearance?> SolveAsync(Uri url, CancellationToken ct)
    {
        foreach (IChallengeSolver solver in solvers)
        {
            if (await solver.SolveAsync(url, ct) is { } clearance)
                return clearance;
        }

        return null;
    }
}

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
public sealed class FirstSolverThatWorks(params IChallengeSolver[] solvers) : IChallengeSolver
{
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

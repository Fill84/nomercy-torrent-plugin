// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Phillippe Pelzer - https://github.com/Fill84

namespace NoMercy.Plugin.TorrentDownloader.Core.Indexers;

internal static class IndexerHttp
{
    internal static async Task<string> GetStringAsync(
        HttpClient http,
        Uri url,
        string indexerName,
        string what,
        CancellationToken ct
    )
    {
        try
        {
            using HttpResponseMessage response = await http.GetAsync(url, ct);

            if (!response.IsSuccessStatusCode)
                throw new IndexerException(
                    $"{indexerName}: {what} returned HTTP {(int)response.StatusCode}",
                    (int)response.StatusCode
                );

            return await response.Content.ReadAsStringAsync(ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (IndexerException)
        {
            throw;
        }
        catch (Exception error)
        {
            throw new IndexerException($"{indexerName}: {what} failed: {error.Message}", error);
        }
    }
}

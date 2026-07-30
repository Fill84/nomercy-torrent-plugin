// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Phillippe Pelzer - https://github.com/Fill84

namespace NoMercy.Plugin.TorrentDownloader.Tests.TestSupport;

// A settable double for the handler behind IPluginContext.HttpClient. Records every
// request it sees so a test can assert the network was never touched - Initialize has
// nowhere to await a response, so any request from it would either block the load or be
// fired and abandoned, and neither is acceptable.
public sealed class FakeHttpMessageHandler : HttpMessageHandler
{
    public List<HttpRequestMessage> Requests { get; } = [];

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken
    )
    {
        Requests.Add(request);
        return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK));
    }
}

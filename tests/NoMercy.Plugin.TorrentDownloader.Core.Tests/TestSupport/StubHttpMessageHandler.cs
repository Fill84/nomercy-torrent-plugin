// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Phillippe Pelzer - https://github.com/Fill84

using System.Net;
using System.Net.Http.Headers;

namespace NoMercy.Plugin.TorrentDownloader.Core.Tests.TestSupport;

public sealed class StubHttpMessageHandler(
    Func<HttpRequestMessage, HttpResponseMessage> respond
) : HttpMessageHandler
{
    public List<Uri> Requests { get; } = [];

    public static StubHttpMessageHandler Returning(
        string body,
        HttpStatusCode status = HttpStatusCode.OK,
        string? contentType = null
    ) =>
        new(_ =>
        {
            HttpResponseMessage response = new(status) { Content = new StringContent(body) };
            if (contentType is not null)
                response.Content.Headers.ContentType = MediaTypeHeaderValue.Parse(contentType);

            return response;
        });

    public static StubHttpMessageHandler Throwing(Exception error) =>
        new(_ => throw error);

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken
    )
    {
        Requests.Add(request.RequestUri!);
        return Task.FromResult(respond(request));
    }

    public HttpClient Client() => new(this);
}

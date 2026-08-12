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

    /// <summary>
    /// What was sent, for the handful of callers that POST. Captured here rather than by
    /// each test, because reading a request's content after the fact is not possible - the
    /// stream is consumed by the time anyone could ask.
    /// </summary>
    public List<string> Bodies { get; } = [];

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

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken
    )
    {
        Requests.Add(request.RequestUri!);

        if (request.Content is not null)
            Bodies.Add(await request.Content.ReadAsStringAsync(cancellationToken));

        return respond(request);
    }

    public HttpClient Client() => new(this);
}

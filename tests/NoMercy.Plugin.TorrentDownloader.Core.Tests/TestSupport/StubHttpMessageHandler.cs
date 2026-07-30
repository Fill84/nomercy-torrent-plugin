// -----------------------------------------------------------------------------
//  Copyright (c) 2024-present NoMercy Entertainment. All rights reserved.
//
//  This file is part of NoMercy MediaServer, source-available software (NOT open
//  source). Personal use and contributions are welcome; distribution, resale,
//  relicensing, and commercial exploitation are prohibited without explicit
//  written consent. See LICENSE for full terms. Distributed WITHOUT ANY WARRANTY.
//
//  SPDX-License-Identifier: LicenseRef-NoMercy-Proprietary
//
// NoMercy MediaServer Automated Torrent Plugin 
// Created by Phillippe Pelzer https://github.com/Fill84
// -----------------------------------------------------------------------------

using System.Net;

namespace NoMercy.Plugin.TorrentDownloader.Core.Tests.TestSupport;

public sealed class StubHttpMessageHandler(
    Func<HttpRequestMessage, HttpResponseMessage> respond
) : HttpMessageHandler
{
    public List<Uri> Requests { get; } = [];

    public static StubHttpMessageHandler Returning(string body, HttpStatusCode status = HttpStatusCode.OK) =>
        new(_ => new HttpResponseMessage(status) { Content = new StringContent(body) });

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

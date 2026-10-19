using System.Net;

namespace NoMercy.Plugin.TorrentDownloader.Tests.TestSupport;

/// <summary>
/// An <see cref="HttpMessageHandler"/> that answers from a script and records
/// what it was asked.
/// </summary>
/// <remarks>
/// <para>
/// Recording the attempts is the point of several tests here: "a gated host
/// never makes an HTTP attempt" is a statement about what did <em>not</em>
/// happen, and only the handler can say so.
/// </para>
/// <para>
/// Two ways of answering. A queue, for a test that knows the order its requests
/// come in; and routes by address, for a whole plugin run, whose name sources,
/// indexers and tracker are asked from several threads in an order nobody
/// should have to predict. A route is tried first, then the queue, then
/// <see cref="Otherwise"/>.
/// </para>
/// </remarks>
public sealed class FakeHttp : HttpMessageHandler
{
    private readonly Queue<Func<HttpRequestMessage, HttpResponseMessage>> _answers = new();
    private readonly List<(Func<HttpRequestMessage, bool> When, Func<HttpRequestMessage, HttpResponseMessage> Answer)> _routes = [];
    private Func<HttpRequestMessage, HttpResponseMessage>? _otherwise;

    /// <summary>Every request that reached the wire, in order.</summary>
    public List<HttpRequestMessage> Attempts { get; } = [];

    /// <summary>What each request carried, read as it was sent: a request is disposed by whoever sent it.</summary>
    public List<string?> Bodies { get; } = [];

    public FakeHttp Answers(HttpStatusCode status, string body = "", params (string Name, string Value)[] headers)
    {
        _answers.Enqueue(_ =>
        {
            HttpResponseMessage response = new(status) { Content = new StringContent(body) };

            foreach ((string name, string value) in headers)
            {
                response.Headers.TryAddWithoutValidation(name, value);
            }

            return response;
        });

        return this;
    }

    /// <summary>The host not answering at all.</summary>
    public FakeHttp Throws(Exception exception)
    {
        _answers.Enqueue(_ => throw exception);
        return this;
    }

    /// <summary>Answers every request <paramref name="when"/> picks out with this text, however often it is asked.</summary>
    public FakeHttp Answers(Func<HttpRequestMessage, bool> when, Func<string> body)
    {
        _routes.Add((when, _ => new(HttpStatusCode.OK) { Content = new StringContent(body()) }));

        return this;
    }

    /// <summary>Answers every request <paramref name="when"/> picks out with these bytes: a tracker's bencode, say.</summary>
    public FakeHttp Answers(Func<HttpRequestMessage, bool> when, Func<HttpRequestMessage, byte[]> body)
    {
        _routes.Add((when, request => new(HttpStatusCode.OK) { Content = new ByteArrayContent(body(request)) }));

        return this;
    }

    /// <summary>What a request no route and no queued answer covers gets, instead of a thrown fault.</summary>
    /// <remarks>
    /// A whole run reaches hosts a test did not script — a router's UPnP description, say — and a fault thrown
    /// out of the handler there would end something the test is not about.
    /// </remarks>
    public FakeHttp Otherwise(HttpStatusCode status)
    {
        _otherwise = _ => new(status) { Content = new StringContent(string.Empty) };

        return this;
    }

    public HttpClient Client()
    {
        return new(this);
    }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        lock (Attempts)
        {
            Attempts.Add(request);
            Bodies.Add(request.Content?.ReadAsStringAsync(ct).GetAwaiter().GetResult());
        }

        foreach ((Func<HttpRequestMessage, bool> when, Func<HttpRequestMessage, HttpResponseMessage> answer) in _routes)
        {
            if (when(request))
            {
                return Task.FromResult(answer(request));
            }
        }

        if (_answers.Count > 0)
        {
            return Task.FromResult(_answers.Dequeue()(request));
        }

        if (_otherwise is not null)
        {
            return Task.FromResult(_otherwise(request));
        }

        throw new InvalidOperationException(
            $"Nothing left to answer {request.RequestUri}. The test expected fewer requests than were made.");
    }
}

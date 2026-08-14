using System.Net;

namespace NoMercy.Plugin.TorrentDownloader.Tests.TestSupport;

/// <summary>
/// An <see cref="HttpMessageHandler"/> that answers from a script and records
/// what it was asked.
/// </summary>
/// <remarks>
/// Recording the attempts is the point of several tests here: "a gated host
/// never makes an HTTP attempt" is a statement about what did <em>not</em>
/// happen, and only the handler can say so.
/// </remarks>
public sealed class FakeHttp : HttpMessageHandler
{
    private readonly Queue<Func<HttpRequestMessage, HttpResponseMessage>> _answers = new();

    /// <summary>Every request that reached the wire, in order.</summary>
    public List<HttpRequestMessage> Attempts { get; } = [];

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

    public HttpClient Client()
    {
        return new(this);
    }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        Attempts.Add(request);

        if (_answers.Count == 0)
        {
            throw new InvalidOperationException(
                $"Nothing left to answer {request.RequestUri}. The test expected fewer requests than were made.");
        }

        return Task.FromResult(_answers.Dequeue()(request));
    }
}

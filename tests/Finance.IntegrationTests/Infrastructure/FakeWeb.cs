using System.Net;

namespace ProductivityApp.Finance.IntegrationTests.Infrastructure;

/// <summary>
/// Stands in for the Internet: answers each address with what <paramref name="answer"/> gives for it, and records the
/// addresses asked. Used through a real <see cref="HttpClient"/>. (A connection that fails: <see cref="BrokenWeb"/>.)
/// </summary>
public sealed class FakeWeb(Func<Uri, (HttpStatusCode Code, string Body)> answer) : HttpMessageHandler
{
    private readonly List<string> _asked = [];

    public FakeWeb(HttpStatusCode code, string body) : this(_ => (code, body)) { }

    public int Calls { get { lock (_asked) return _asked.Count; } }
    public IReadOnlyList<string> Asked { get { lock (_asked) return [.. _asked]; } }

    public HttpClient Client() => new(this, disposeHandler: false);

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        lock (_asked) _asked.Add(request.RequestUri!.ToString());
        var (code, body) = answer(request.RequestUri!);
        return Task.FromResult(new HttpResponseMessage(code) { Content = new ByteArrayContent(System.Text.Encoding.UTF8.GetBytes(body)) });
    }
}

/// <summary>A connection to the Internet that fails: every request throws <paramref name="fault"/>.</summary>
public sealed class BrokenWeb(Exception fault) : HttpMessageHandler
{
    public HttpClient Client() => new(this, disposeHandler: false);

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => throw fault;
}

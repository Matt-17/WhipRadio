using System.Net;

namespace WhipRadio.TestSupport;

/// <summary>Captures the outgoing request and returns a canned response.</summary>
public sealed class FakeHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
{
    public List<HttpRequestMessage> Requests { get; } = [];

    public HttpRequestMessage? LastRequest { get; private set; }

    public string? LastRequestBody { get; private set; }

    public List<string?> RequestBodies { get; } = [];

    public static FakeHttpMessageHandler RespondingWith(HttpStatusCode statusCode, HttpContent content)
        => new(_ => new HttpResponseMessage(statusCode) { Content = content });

    public HttpClient CreateClient(string baseAddress = "http://localhost")
        => new(this) { BaseAddress = new Uri(baseAddress) };

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests.Add(request);
        LastRequest = request;
        if (request.Content is not null)
        {
            LastRequestBody = await request.Content.ReadAsStringAsync(cancellationToken);
        }
        else
        {
            LastRequestBody = null;
        }

        RequestBodies.Add(LastRequestBody);

        return responder(request);
    }
}

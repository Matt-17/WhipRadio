using System.Net;

namespace WhipRadio.Infrastructure.Tests;

/// <summary>Captures the outgoing request and returns a canned response.</summary>
public sealed class FakeHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
{
    public HttpRequestMessage? LastRequest { get; private set; }

    public string? LastRequestBody { get; private set; }

    public static FakeHttpMessageHandler RespondingWith(HttpStatusCode statusCode, HttpContent content)
        => new(_ => new HttpResponseMessage(statusCode) { Content = content });

    public HttpClient CreateClient(string baseAddress = "http://localhost")
        => new(this) { BaseAddress = new Uri(baseAddress) };

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        LastRequest = request;
        if (request.Content is not null)
        {
            LastRequestBody = await request.Content.ReadAsStringAsync(cancellationToken);
        }

        return responder(request);
    }
}

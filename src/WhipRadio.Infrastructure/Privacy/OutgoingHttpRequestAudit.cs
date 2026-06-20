using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Http;
using WhipRadio.Core.Api;

namespace WhipRadio.Infrastructure.Privacy;

public sealed record OutgoingHttpRequestEntry(
    DateTime TimestampUtc,
    string Method,
    string Target,
    string Host,
    string Source,
    int? StatusCode,
    bool Succeeded,
    double DurationMs,
    string Classification,
    string? Error);

public class OutgoingHttpRequestAudit
{
    public const int DefaultCapacity = 300;

    private readonly ConcurrentQueue<OutgoingHttpRequestEntry> _entries = new();

    public int Capacity { get; }

    public OutgoingHttpRequestAudit()
        : this(DefaultCapacity)
    {
    }

    public OutgoingHttpRequestAudit(int capacity)
    {
        Capacity = Math.Max(1, capacity);
    }

    public void Record(OutgoingHttpRequestEntry entry)
    {
        _entries.Enqueue(entry);
        while (_entries.Count > Capacity && _entries.TryDequeue(out _))
        {
        }
    }

    public IReadOnlyList<PrivacyRequestDto> Snapshot(int take = DefaultCapacity)
        => _entries
            .Reverse()
            .Take(Math.Min(Math.Max(1, take), Capacity))
            .Select(entry => new PrivacyRequestDto(
                entry.TimestampUtc,
                entry.Method,
                entry.Target,
                entry.Host,
                entry.Source,
                entry.StatusCode,
                entry.Succeeded,
                entry.DurationMs,
                entry.Classification,
                entry.Error))
            .ToList();
}

public sealed class PrivacyAuditHttpMessageHandler(string source, OutgoingHttpRequestAudit audit) : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var uri = request.RequestUri;
        if (uri is null || !PrivacyRequestClassifier.IsExternal(uri))
        {
            return await base.SendAsync(request, cancellationToken);
        }

        var started = Stopwatch.GetTimestamp();
        var timestampUtc = DateTime.UtcNow;
        int? statusCode = null;
        string? error = null;

        try
        {
            var response = await base.SendAsync(request, cancellationToken);
            statusCode = (int)response.StatusCode;
            return response;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or OperationCanceledException)
        {
            error = ex.GetBaseException().Message;
            throw;
        }
        finally
        {
            var durationMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            audit.Record(new OutgoingHttpRequestEntry(
                timestampUtc,
                request.Method.Method,
                PrivacyRequestClassifier.SanitizeTarget(uri),
                uri.Host,
                PrivacyRequestClassifier.DisplaySource(source),
                statusCode,
                statusCode is >= 200 and < 400 && error is null,
                durationMs,
                "external",
                PrivacyRequestClassifier.SanitizeError(error)));
        }
    }
}

public sealed class PrivacyAuditHttpMessageHandlerFilter(OutgoingHttpRequestAudit audit) : IHttpMessageHandlerBuilderFilter
{
    public Action<HttpMessageHandlerBuilder> Configure(Action<HttpMessageHandlerBuilder> next)
        => builder =>
        {
            next(builder);
            builder.AdditionalHandlers.Add(new PrivacyAuditHttpMessageHandler(builder.Name ?? "unknown", audit));
        };
}

public static class PrivacyRequestClassifier
{
    public static bool IsExternal(Uri uri)
    {
        if (!uri.IsAbsoluteUri)
        {
            return false;
        }

        var host = uri.Host.Trim().TrimEnd('.');
        if (string.IsNullOrWhiteSpace(host))
        {
            return false;
        }

        if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
            || host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase)
            || host.EndsWith(".local", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!host.Contains('.') && !IPAddress.TryParse(host, out _))
        {
            return false;
        }

        return !IsPrivateOrLoopbackAddress(host);
    }

    public static string SanitizeTarget(Uri uri)
    {
        var builder = new UriBuilder(uri)
        {
            Query = "",
            Fragment = "",
        };

        return builder.Uri.GetComponents(UriComponents.SchemeAndServer | UriComponents.Path, UriFormat.UriEscaped);
    }

    public static string DisplaySource(string source)
    {
        if (source.Contains("OpenMeteoWeatherSource", StringComparison.Ordinal))
        {
            return "Open-Meteo";
        }

        if (source.Contains("RssNewsFeedReader", StringComparison.Ordinal)
            || source.Contains("HtmlArticleExtractor", StringComparison.Ordinal))
        {
            return "News feeds";
        }

        return source switch
        {
            "llm-openai" or "openai" => "OpenAI",
            "tts-elevenlabs" or "elevenlabs" => "ElevenLabs",
            "news" or "rss-news" => "News feeds",
            "studio" or "studio-probe" => "Studios",
            "audio-analysis" => "Audio analysis",
            "musicgen" => "MusicGen",
            "acestep" => "ACE-Step",
            _ when source.Contains('.') => source[(source.LastIndexOf('.') + 1)..],
            _ => source,
        };
    }

    public static string? SanitizeError(string? error)
    {
        if (string.IsNullOrWhiteSpace(error))
        {
            return null;
        }

        var oneLine = string.Join(' ', error.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return oneLine.Length <= 180 ? oneLine : $"{oneLine[..177]}...";
    }

    private static bool IsPrivateOrLoopbackAddress(string host)
    {
        if (!IPAddress.TryParse(host, out var address))
        {
            return false;
        }

        if (IPAddress.IsLoopback(address))
        {
            return true;
        }

        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            var bytes = address.GetAddressBytes();
            return bytes[0] == 10
                || bytes[0] == 127
                || bytes[0] == 169 && bytes[1] == 254
                || bytes[0] == 172 && bytes[1] is >= 16 and <= 31
                || bytes[0] == 192 && bytes[1] == 168;
        }

        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            return address.IsIPv6LinkLocal
                || address.IsIPv6SiteLocal
                || address.Equals(IPAddress.IPv6Loopback)
                || address.GetAddressBytes()[0] is 0xfc or 0xfd;
        }

        return false;
    }
}

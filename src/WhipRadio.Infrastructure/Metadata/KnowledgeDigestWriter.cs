using System.Text;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using WhipRadio.Core.Abstractions;
using WhipRadio.Core.Json;
using WhipRadio.Infrastructure.Llm;

namespace WhipRadio.Infrastructure.Metadata;

/// <summary>
/// Turns structured Wikidata facts (plus a Wikipedia summary as paraphrase
/// input) into a compact digest of 3–6 fact sentences via the writer room.
/// The digest is stored; the summary never is (Phase 6 copyright rule).
/// </summary>
public sealed class KnowledgeDigestWriter(
    ITextGenerationService llm,
    ILogger<KnowledgeDigestWriter> logger)
{
    internal sealed record KnowledgeDigestDto([property: JsonRequired] IReadOnlyList<string> Facts);

    /// <summary>Returns the digest text, or null when generation failed (failure-soft).</summary>
    public async Task<string?> WriteAsync(
        string artistName,
        IReadOnlyDictionary<string, string> facts,
        string? summary,
        CancellationToken ct)
    {
        var factsBlock = new StringBuilder();
        foreach (var (key, value) in facts)
        {
            factsBlock.AppendLine($"- {key}: {value}");
        }

        if (factsBlock.Length == 0 && string.IsNullOrWhiteSpace(summary))
        {
            return null;
        }

        var prompt = PromptTemplates.Render("KnowledgeDigest", new Dictionary<string, string>
        {
            ["ArtistName"] = artistName,
            ["FactsBlock"] = factsBlock.Length > 0 ? factsBlock.ToString().TrimEnd() : "(none)",
            ["Summary"] = string.IsNullOrWhiteSpace(summary) ? "(none)" : summary,
        });

        try
        {
            var raw = await llm.CompleteAsync(
                new TextGenerationRequest(
                    "You write neutral factual background notes. Return only valid JSON.",
                    prompt,
                    $"Knowledge digest: {artistName}",
                    StructuredJson.SchemaFor<KnowledgeDigestDto>(),
                    "knowledgeDigest"),
                ct);
            var result = StructuredJson.Parse<KnowledgeDigestDto>(raw);
            if (!result.IsValid || result.Value!.Facts.Count == 0)
            {
                logger.LogWarning("Knowledge digest for {Artist} was not valid JSON: {Error}", artistName, result.Error);
                return null;
            }

            var statements = result.Value.Facts
                .Where(fact => !string.IsNullOrWhiteSpace(fact))
                .Take(6)
                .Select(fact => fact.Trim());
            return string.Join(" ", statements);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Knowledge digest generation failed for {Artist}", artistName);
            return null;
        }
    }
}

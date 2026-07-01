using System.Globalization;
using System.Text;

namespace WhipRadio.Core.Slugs;

public static class SlugGenerator
{
    private const int MaxLength = 96;

    public static string FromName(string? name)
    {
        var normalized = (name ?? string.Empty).Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(normalized.Length);
        var lastWasDash = false;

        foreach (var ch in normalized)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(ch) == UnicodeCategory.NonSpacingMark)
            {
                continue;
            }

            var lower = char.ToLowerInvariant(ch);
            if (lower is >= 'a' and <= 'z' or >= '0' and <= '9')
            {
                builder.Append(lower);
                lastWasDash = false;
            }
            else if (!lastWasDash && builder.Length > 0)
            {
                builder.Append('-');
                lastWasDash = true;
            }
        }

        var slug = builder.ToString().Trim('-');
        if (slug.Length == 0)
        {
            slug = "untitled";
        }

        return slug.Length <= MaxLength ? slug : slug[..MaxLength].TrimEnd('-');
    }

    /// <summary>Normalizes an incoming slug (e.g. from a route or API input) for lookup.
    /// Slugs are always stored lowercase (see <see cref="FromName"/>), and Postgres string
    /// equality is case-sensitive, so a read must compare against the lowercased value.</summary>
    public static string Normalize(string? slug) =>
        (slug ?? string.Empty).Trim().ToLowerInvariant();

    public static string UniqueFromName(string? name, IEnumerable<string?> existingSlugs)
    {
        var baseSlug = FromName(name);
        var used = existingSlugs
            .Where(slug => !string.IsNullOrWhiteSpace(slug))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (!used.Contains(baseSlug))
        {
            return baseSlug;
        }

        for (var suffix = 2; suffix < 1000; suffix++)
        {
            var suffixText = $"-{suffix}";
            var prefix = baseSlug.Length + suffixText.Length <= MaxLength
                ? baseSlug
                : baseSlug[..(MaxLength - suffixText.Length)].TrimEnd('-');
            var candidate = $"{prefix}{suffixText}";
            if (!used.Contains(candidate))
            {
                return candidate;
            }
        }

        return $"{baseSlug[..Math.Min(baseSlug.Length, MaxLength - 9)].TrimEnd('-')}-{Guid.NewGuid():N}"[..MaxLength];
    }
}

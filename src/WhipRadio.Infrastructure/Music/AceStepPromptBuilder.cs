using System.Text;
using WhipRadio.Core.Abstractions;

namespace WhipRadio.Infrastructure.Music;

public sealed class AceStepPromptBuilder
{
    private const int ArtistBackstoryLimit = 280;

    public string Build(MusicRequest request)
    {
        if (IsJingleRequest(request))
        {
            return BuildJinglePrompt(request);
        }

        var style = FirstNonEmpty(request.Style, request.ArtistStyleDescription, request.Prompt, request.SubGenre, request.Genre)
            ?? "radio-ready music";
        var songType = request.LyricsMode == LyricsMode.Instrumental ? "instrumental song" : "song";

        var builder = new StringBuilder();
        builder.Append("Create a complete full-length ");
        builder.Append(CleanSentence(style));
        builder.Append(' ');
        builder.Append(songType);
        builder.AppendLine(".");

        if (!string.IsNullOrWhiteSpace(request.ArtistName) || !string.IsNullOrWhiteSpace(request.ArtistBackstory))
        {
            builder.AppendLine();
            builder.Append("Artist identity: ");
            if (!string.IsNullOrWhiteSpace(request.ArtistName))
            {
                builder.Append(CleanSentence(request.ArtistName));
                builder.Append(", a fictional artist");
            }
            else
            {
                builder.Append("A fictional artist");
            }

            if (!string.IsNullOrWhiteSpace(request.ArtistBackstory))
            {
                builder.Append(" with this creative context: ");
                builder.Append(TrimToSentence(request.ArtistBackstory!, ArtistBackstoryLimit));
            }

            builder.AppendLine(".");
        }

        if (!string.IsNullOrWhiteSpace(request.Style) || !string.IsNullOrWhiteSpace(request.ArtistStyleDescription))
        {
            builder.AppendLine();
            builder.Append("Style: ");
            builder.Append(CleanSentence(FirstNonEmpty(request.ArtistStyleDescription, request.Style)!));
            builder.AppendLine(".");
        }

        if (request.LyricsMode != LyricsMode.Instrumental)
        {
            var vocalParts = new List<string>();
            if (request.VocalGender is VocalGender.Male or VocalGender.Female or VocalGender.Mixed)
            {
                vocalParts.Add(request.VocalGender switch
                {
                    VocalGender.Male => "male lead vocals",
                    VocalGender.Female => "female lead vocals",
                    VocalGender.Mixed => "mixed male and female vocals",
                    _ => string.Empty,
                });
            }

            if (!string.IsNullOrWhiteSpace(request.VocalStyle))
            {
                vocalParts.Add(CleanSentence(request.VocalStyle));
            }

            if (vocalParts.Count > 0)
            {
                builder.AppendLine();
                builder.Append("Lead vocals: ");
                builder.Append(string.Join(" with ", vocalParts));
                builder.AppendLine(".");
            }

            if (!string.IsNullOrWhiteSpace(request.Language))
            {
                builder.AppendLine();
                builder.Append("Language: ");
                builder.Append(CleanSentence(request.Language));
                builder.AppendLine(".");
            }
        }

        AppendOptional(builder, "Tempo", request.Bpm is int bpm ? $"approximately {bpm} BPM" : null);
        AppendOptional(builder, "Key", request.KeyScale);
        AppendOptional(builder, "Time signature", request.TimeSignature);

        builder.AppendLine();
        builder.Append("Use a complete song structure with an intro, verses, recurring chorus, bridge and a deliberate outro. ");
        builder.Append("Avoid spoken narration and avoid an abrupt ending.");

        return builder.ToString();
    }

    private static void AppendOptional(StringBuilder builder, string label, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        builder.AppendLine();
        builder.Append(label);
        builder.Append(": ");
        builder.Append(CleanSentence(value));
        builder.AppendLine(".");
    }

    private static string? FirstNonEmpty(params string?[] values)
        => values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v))?.Trim();

    private static bool IsJingleRequest(MusicRequest request)
        => string.Equals(request.Genre, "jingle", StringComparison.OrdinalIgnoreCase)
            || string.Equals(request.SubGenre, "radio identity", StringComparison.OrdinalIgnoreCase);

    private static string BuildJinglePrompt(MusicRequest request)
    {
        var prompt = FirstNonEmpty(request.Prompt, request.Style, request.SubGenre)
            ?? "short instrumental radio jingle";
        return TrimToSentence(prompt, 220);
    }

    private static string CleanSentence(string value)
        => value.Trim().TrimEnd('.', '!', '?');

    private static string TrimToSentence(string value, int maxLength)
    {
        var cleaned = CleanSentence(value);
        if (cleaned.Length <= maxLength)
        {
            return cleaned;
        }

        return cleaned[..maxLength].TrimEnd(' ', ',', ';', ':') + "...";
    }
}

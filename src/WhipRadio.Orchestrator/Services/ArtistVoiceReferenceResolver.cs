using Microsoft.Extensions.Options;
using WhipRadio.Core.Entities;
using WhipRadio.Orchestrator.Configuration;

namespace WhipRadio.Orchestrator.Services;

public sealed record ArtistVoiceReferenceContext(
    string ReferenceAudioPath,
    string ReferenceAudioLabel);

public sealed record MissingArtistMemberVoice(
    Guid MemberId,
    string Reason);

public sealed record ArtistVoiceReferenceResolution(
    ArtistVoiceReferenceContext? Reference,
    MissingArtistMemberVoice? MissingVoice)
{
    public static ArtistVoiceReferenceResolution Ready(ArtistVoiceReferenceContext reference)
        => new(reference, null);

    public static ArtistVoiceReferenceResolution Missing(Guid memberId, string reason)
        => new(null, new MissingArtistMemberVoice(memberId, reason));
}

public sealed class ArtistVoiceReferenceResolver(
    ArtistMemberVoiceQueue voiceQueue,
    IOptions<RadioOptions> radioOptions)
{
    public Task<ArtistVoiceReferenceResolution> ResolveAsync(Artist artist, CancellationToken ct)
    {
        var lead = SelectLeadVocalist(artist.Members);
        if (lead is null)
        {
            throw new InvalidOperationException($"Artist {artist.Id} has no members to use for vocal reference bootstrap.");
        }

        if (string.IsNullOrWhiteSpace(lead.VoiceId)
            || string.IsNullOrWhiteSpace(lead.VoiceReferencePath))
        {
            voiceQueue.EnqueuePriority(lead.Id);
            return Task.FromResult(ArtistVoiceReferenceResolution.Missing(
                lead.Id,
                "lead vocalist has no designed spoken reference"));
        }

        var absolutePath = Path.Combine(radioOptions.Value.DataRoot, lead.VoiceReferencePath);
        if (!File.Exists(absolutePath))
        {
            voiceQueue.EnqueuePriority(lead.Id);
            return Task.FromResult(ArtistVoiceReferenceResolution.Missing(
                lead.Id,
                "lead vocalist spoken reference file is missing"));
        }

        return Task.FromResult(ArtistVoiceReferenceResolution.Ready(new ArtistVoiceReferenceContext(
            absolutePath,
            $"{lead.Name} spoken reference")));
    }

    public static ArtistMember? SelectLeadVocalist(IEnumerable<ArtistMember> members)
        => ArtistMemberRoster.SelectLeadVocalist(members);
}

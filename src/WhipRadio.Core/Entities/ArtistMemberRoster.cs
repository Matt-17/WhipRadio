namespace WhipRadio.Core.Entities;

/// <summary>Shared rules for reading an artist's member roster, e.g. who sings lead.</summary>
public static class ArtistMemberRoster
{
    /// <summary>
    /// The member who fronts the vocals: the first member (by sort order) whose
    /// role reads as a singing role. Instrumental-only acts have no fallback singer.
    /// </summary>
    public static ArtistMember? SelectLeadVocalist(IEnumerable<ArtistMember> members)
        => members
            .OrderBy(member => member.SortOrder)
            .FirstOrDefault(member => IsVocalRole(member.Role));

    /// <summary>
    /// Every member whose role reads as a singing role. Instrumental-only acts
    /// return an empty list.
    /// </summary>
    public static IReadOnlyList<ArtistMember> VocalMembers(IEnumerable<ArtistMember> members)
        => members
            .OrderBy(member => member.SortOrder)
            .Where(member => IsVocalRole(member.Role))
            .ToList();

    public static bool HasVocalMember(IEnumerable<ArtistMember> members)
        => members.Any(member => IsVocalRole(member.Role));

    public static bool IsVocalRole(string role)
        => role.Contains("vocal", StringComparison.OrdinalIgnoreCase)
            || role.Contains("singer", StringComparison.OrdinalIgnoreCase)
            || role.Contains("voice", StringComparison.OrdinalIgnoreCase)
            || role.Contains("front", StringComparison.OrdinalIgnoreCase);
}

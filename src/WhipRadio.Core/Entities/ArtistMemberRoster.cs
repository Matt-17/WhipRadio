namespace WhipRadio.Core.Entities;

/// <summary>Shared rules for reading an artist's member roster, e.g. who sings lead.</summary>
public static class ArtistMemberRoster
{
    /// <summary>
    /// The member who fronts the vocals: the first member (by sort order) whose
    /// role reads as a singing role, falling back to the first member listed.
    /// </summary>
    public static ArtistMember? SelectLeadVocalist(IEnumerable<ArtistMember> members)
    {
        var ordered = members.OrderBy(member => member.SortOrder).ToList();
        return ordered.FirstOrDefault(member => IsVocalRole(member.Role))
            ?? ordered.FirstOrDefault();
    }

    /// <summary>
    /// Every member whose role reads as a singing role, falling back to the full
    /// roster when nobody is explicitly tagged as a vocalist.
    /// </summary>
    public static IReadOnlyList<ArtistMember> VocalMembers(IEnumerable<ArtistMember> members)
    {
        var ordered = members.OrderBy(member => member.SortOrder).ToList();
        var vocalists = ordered.Where(member => IsVocalRole(member.Role)).ToList();
        return vocalists.Count > 0 ? vocalists : ordered;
    }

    public static bool IsVocalRole(string role)
        => role.Contains("vocal", StringComparison.OrdinalIgnoreCase)
            || role.Contains("singer", StringComparison.OrdinalIgnoreCase)
            || role.Contains("voice", StringComparison.OrdinalIgnoreCase)
            || role.Contains("front", StringComparison.OrdinalIgnoreCase);
}

using WhipRadio.Core.Entities;

namespace WhipRadio.Core.Tests;

[TestClass]
public class ArtistMemberRosterTests
{
    [TestMethod]
    public void SelectLeadVocalist_ReturnsNullWhenNoMemberHasVocalRole()
    {
        var members = new[]
        {
            new ArtistMember { SortOrder = 0, Name = "Ira", Role = "modular synths" },
            new ArtistMember { SortOrder = 1, Name = "Tem", Role = "drums" },
        };

        Assert.Null(ArtistMemberRoster.SelectLeadVocalist(members));
        Assert.False(ArtistMemberRoster.HasVocalMember(members));
        Assert.Empty(ArtistMemberRoster.VocalMembers(members));
    }

    [TestMethod]
    public void VocalMembers_ReturnsOnlyExplicitVocalRolesInRosterOrder()
    {
        var members = new[]
        {
            new ArtistMember { SortOrder = 2, Name = "Ada", Role = "bass" },
            new ArtistMember { SortOrder = 1, Name = "Mara", Role = "lead vocals" },
            new ArtistMember { SortOrder = 0, Name = "Nils", Role = "synths" },
        };

        var vocalMembers = ArtistMemberRoster.VocalMembers(members);

        Assert.True(ArtistMemberRoster.HasVocalMember(members));
        Assert.Equal(1, vocalMembers.Count);
        Assert.Equal("Mara", vocalMembers[0].Name);
        Assert.Equal("Mara", ArtistMemberRoster.SelectLeadVocalist(members)?.Name);
    }
}

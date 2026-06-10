namespace WhipRadio.Core.Selection;

/// <summary>Broad genres and their subgenres used for generation and planning.</summary>
public static class GenreCatalog
{
    public static readonly IReadOnlyDictionary<string, string[]> Subgenres =
        new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            ["electronic"] = ["techno", "trance", "dubstep", "drum and bass", "deep house", "ambient", "synthwave"],
            ["indie rock"] = ["garage rock", "dream pop", "post-punk", "shoegaze", "surf rock"],
            ["lofi"] = ["lofi hip hop", "chillhop", "jazzhop", "ambient lofi"],
            ["jazz"] = ["smooth jazz", "bebop", "nu jazz", "cool jazz"],
            ["pop"] = ["synth pop", "indie pop", "electro pop", "city pop"],
        };

    public static IReadOnlyList<string> Genres { get; } = Subgenres.Keys.ToList();

    public static string PickSubgenre(string genre, Random random)
    {
        if (Subgenres.TryGetValue(genre, out var subgenres) && subgenres.Length > 0)
        {
            return subgenres[random.Next(subgenres.Length)];
        }

        return string.Empty;
    }
}

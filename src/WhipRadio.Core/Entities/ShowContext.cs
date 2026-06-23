using WhipRadio.Core.Selection;

namespace WhipRadio.Core.Entities;

/// <summary>
/// Everything the show pipeline needs to know about "now": the musical
/// direction and the host, resolved from the program plan (or fallback rotation).
/// </summary>
public sealed record ShowContext(
    string Genre,
    string Subgenre,
    Moderator Moderator,
    Format? Format = null,
    int? SlotStartMinute = null,
    int? SlotDurationMinutes = null,
    int? RemainingSlotMinutes = null,
    string? NextFormatName = null,
    ShowWindows? ShowWindows = null,
    SelectionSettings? Selection = null);

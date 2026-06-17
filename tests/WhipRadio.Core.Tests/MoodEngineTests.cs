using WhipRadio.Core.Personality;

namespace WhipRadio.Core.Tests;

[TestClass]
public class MoodEngineTests
{
    private static readonly HostPersonalityTraits Baseline = new(
        Energy.Medium,
        Formality.Balanced,
        HumorLevel.Medium,
        Talkativeness.Medium,
        Warmth.Medium);

    [TestMethod]
    public void Current_ChangesAtMostOneStepPerHour()
    {
        var previous = MoodEngine.Current(Baseline, seed: 42, AtHour(0));
        for (var hour = 1; hour < 24; hour++)
        {
            var current = MoodEngine.Current(Baseline, seed: 42, AtHour(hour));

            foreach (var trait in Enum.GetValues<PersonalityTraitKind>())
            {
                Assert.True(
                    MoodEngine.TraitDistance(previous, current, trait) <= 1,
                    $"{trait} changed by more than one step between {hour - 1}:00 and {hour}:00.");
            }

            previous = current;
        }
    }

    [TestMethod]
    public void Current_StaysWithinTwoStepsOfBaseline()
    {
        foreach (var hour in Enumerable.Range(0, 24))
        {
            var current = MoodEngine.Current(Baseline, seed: 42, AtHour(hour));

            foreach (var trait in Enum.GetValues<PersonalityTraitKind>())
            {
                var distance = Math.Abs(MoodEngine.GetOrdinal(current, trait) - MoodEngine.GetOrdinal(Baseline, trait));
                Assert.True(distance <= 2, $"{trait} drifted {distance} steps at {hour}:00.");
            }
        }
    }

    [TestMethod]
    public void Current_BiasesDriveTimeLivelierThanLateNight()
    {
        var lateNight = MoodEngine.Current(Baseline, seed: 42, AtHour(3));
        var driveTime = MoodEngine.Current(Baseline, seed: 42, AtHour(8));

        Assert.True(driveTime.Energy > lateNight.Energy);
        Assert.True(driveTime.Talkativeness > lateNight.Talkativeness);
        Assert.True(driveTime.HumorLevel > lateNight.HumorLevel);
    }

    [TestMethod]
    public void InferBaseline_UsesStyleAndTalkativeness()
    {
        var traits = MoodEngine.InferBaseline("fast energetic and warm", 0.8);

        Assert.Equal(Energy.High, traits.Energy);
        Assert.Equal(Talkativeness.High, traits.Talkativeness);
        Assert.Equal(Warmth.High, traits.Warmth);
    }

    private static DateTimeOffset AtHour(int hour)
        => new(2026, 6, 17, hour, 0, 0, TimeSpan.Zero);
}

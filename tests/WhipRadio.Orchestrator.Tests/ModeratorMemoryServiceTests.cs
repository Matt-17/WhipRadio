using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using WhipRadio.Core.Abstractions;
using WhipRadio.Core.Entities;
using WhipRadio.Core.Prompting;
using WhipRadio.Infrastructure.Persistence;
using WhipRadio.Orchestrator.Services;
using WhipRadio.TestSupport;

namespace WhipRadio.Orchestrator.Tests;

[TestClass]
public class ModeratorMemoryServiceTests
{
    [TestMethod]
    public async Task RememberAsync_TrimsDayMemoryByOldestRowsWithinSameDay()
    {
        await using var fixture = await DbFixture.CreateAsync();
        await AddModeratorAsync(fixture);
        var service = CreateService(fixture);
        var day = new DateOnly(2026, 6, 17);

        await service.RememberAsync(1, ModeratorMemoryLayer.DayMemory, day, new string('a', 900), CancellationToken.None);
        await service.RememberAsync(1, ModeratorMemoryLayer.DayMemory, day, new string('b', 900), CancellationToken.None);
        await service.RememberAsync(1, ModeratorMemoryLayer.DayMemory, day, new string('c', 900), CancellationToken.None);

        await using var db = fixture.CreateDbContext();
        var rows = await db.ModeratorMemories.AsNoTracking()
            .Where(memory => memory.ModeratorId == 1
                && memory.Layer == ModeratorMemoryLayer.DayMemory
                && memory.Date == day)
            .OrderBy(memory => memory.Id)
            .ToListAsync();

        Assert.Equal(2, rows.Count);
        Assert.Equal(1_800, rows.Sum(memory => memory.Content.Length));
        Assert.True(rows[0].Content.All(character => character == 'b'));
        Assert.True(rows[1].Content.All(character => character == 'c'));
    }

    [TestMethod]
    public async Task RememberAsync_TrimsLongTermMemoryByOldestRowsAcrossDates()
    {
        await using var fixture = await DbFixture.CreateAsync();
        await AddModeratorAsync(fixture);
        var service = CreateService(fixture);
        var day = new DateOnly(2026, 6, 15);

        await service.RememberAsync(1, ModeratorMemoryLayer.LongTermMemory, day, new string('a', 1_200), CancellationToken.None);
        await service.RememberAsync(1, ModeratorMemoryLayer.LongTermMemory, day.AddDays(1), new string('b', 1_200), CancellationToken.None);
        await service.RememberAsync(1, ModeratorMemoryLayer.LongTermMemory, day.AddDays(2), new string('c', 1_200), CancellationToken.None);

        await using var db = fixture.CreateDbContext();
        var rows = await db.ModeratorMemories.AsNoTracking()
            .Where(memory => memory.ModeratorId == 1
                && memory.Layer == ModeratorMemoryLayer.LongTermMemory)
            .OrderBy(memory => memory.Id)
            .ToListAsync();

        Assert.Equal(2, rows.Count);
        Assert.Equal(2_400, rows.Sum(memory => memory.Content.Length));
        CollectionAssert.AreEqual(
            new[] { day.AddDays(1), day.AddDays(2) },
            rows.Select(memory => memory.Date).ToArray());
    }

    [TestMethod]
    public async Task DistillDayAsync_WritesLongTermMemoryOnceForEachModeratorDay()
    {
        await using var fixture = await DbFixture.CreateAsync();
        await AddModeratorAsync(fixture);
        var llm = new RecordingTextGenerationService("durable summary about the metronome callback");
        var service = CreateService(fixture, llm);
        var day = new DateOnly(2026, 6, 17);

        await service.RememberAsync(
            1,
            ModeratorMemoryLayer.DayMemory,
            day,
            "Ava joked about the drummer ignoring the metronome.",
            CancellationToken.None);
        await service.RememberAsync(
            1,
            ModeratorMemoryLayer.DayMemory,
            day,
            "Ava promised to bring the metronome callback back before midnight.",
            CancellationToken.None);

        var distilled = await service.DistillDayAsync(day, CancellationToken.None);
        var skipped = await service.DistillDayAsync(day, CancellationToken.None);

        Assert.Equal(1, distilled);
        Assert.Equal(0, skipped);
        Assert.Equal(1, llm.Requests.Count);
        Assert.Contains("metronome", llm.Requests.Single().UserPrompt);

        await using var db = fixture.CreateDbContext();
        var longTerm = await db.ModeratorMemories.AsNoTracking()
            .SingleAsync(memory => memory.ModeratorId == 1
                && memory.Layer == ModeratorMemoryLayer.LongTermMemory
                && memory.Date == day);
        Assert.Equal("durable summary about the metronome callback", longTerm.Content);
    }

    private static ModeratorMemoryService CreateService(
        IDbContextFactory<RadioDbContext> fixture,
        ITextGenerationService? llm = null)
        => new(
            fixture,
            new StaticPromptContextBuilder(),
            llm ?? new RecordingTextGenerationService("unused"),
            NullLogger<ModeratorMemoryService>.Instance);

    private static async Task AddModeratorAsync(DbFixture fixture)
    {
        await using var db = fixture.CreateDbContext();
        db.Moderators.Add(new Moderator
        {
            Id = 1,
            Name = "Ava",
            Language = "en",
            Gender = ModeratorGenders.Female,
            PersonaPrompt = "warm, precise, dry humor",
            Style = "late-night",
            IsActive = true,
        });
        await db.SaveChangesAsync();
    }

    private sealed class StaticPromptContextBuilder : IPromptContextBuilder
    {
        public Task<PromptContext> BuildAsync(PromptContextInput input, CancellationToken ct)
            => Task.FromResult(new PromptContext
            {
                Scope = input.Scope,
                Purpose = input.Purpose ?? string.Empty,
                StationName = "WhipRadio",
                FrequencyMhz = 99.7,
                LocalNow = new DateTimeOffset(2026, 6, 17, 3, 0, 0, TimeSpan.Zero),
                Language = input.Moderator?.Language ?? "en",
                HostName = input.Moderator?.Name,
            });
    }

    private sealed class RecordingTextGenerationService(string response) : ITextGenerationService
    {
        public List<Request> Requests { get; } = [];

        public Task<string> CompleteAsync(string systemPrompt, string userPrompt, CancellationToken ct)
        {
            Requests.Add(new Request(systemPrompt, userPrompt));
            return Task.FromResult(response);
        }

        public sealed record Request(string SystemPrompt, string UserPrompt);
    }
}

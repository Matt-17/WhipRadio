using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using WhipRadio.Core.Api;
using WhipRadio.Core.Entities;
using WhipRadio.Core.Prompting;
using WhipRadio.Infrastructure.Persistence;

namespace WhipRadio.Orchestrator.Services;

public sealed partial class ChatActionExecutor
{
    private async Task<ChatActionRecord> ExecuteStudioStatusAsync(CharacterToolCall call, CancellationToken ct)
    {
        string? kindFilter = Optional(call, "kind")?.ToLowerInvariant();
        await using RadioDbContext db = await dbFactory.CreateDbContextAsync(ct);
        var studios = await db.Studios.AsNoTracking()
            .Select(studio => new { studio.Name, studio.Kind, studio.IsActive })
            .ToListAsync(ct);
        if (kindFilter is not null and not "all")
        {
            studios = studios.Where(s => s.Kind.ToString().ToLowerInvariant() == kindFilter).ToList();
        }

        if (studios.Count == 0)
        {
            return Succeeded(call, "No studios are registered.");
        }

        string summary = string.Join("; ", studios
            .GroupBy(s => s.Kind)
            .Select(group => $"{group.Key}: {group.Count(s => s.IsActive)}/{group.Count()} active"));
        return Succeeded(call, $"Studios — {summary}");
    }

    private async Task<ChatActionRecord> ExecuteServerStatusAsync(CharacterToolCall call, CancellationToken ct)
    {
        using IServiceScope scope = scopeFactory.CreateScope();
        ServerStatsCollector collector = scope.ServiceProvider.GetRequiredService<ServerStatsCollector>();
        ServerStatsDto stats = await collector.CollectAsync(ct);
        string gpu = stats.Gpu is { } g
            ? $"GPU {g.Name} {g.UtilizationPercent:0}% / {g.MemoryTotalMb:0}MB"
            : "no GPU";
        return Succeeded(
            call,
            $"CPU {stats.CpuUsagePercent:0}% over {stats.ProcessorCount} cores; "
            + $"RAM {stats.MemoryUsedMb:0}/{stats.MemoryTotalMb:0}MB (process {stats.ProcessMemoryMb:0}MB); "
            + $"disk {stats.DiskFreeGb:0}/{stats.DiskTotalGb:0}GB free; {gpu}.");
    }

    private async Task<ChatActionRecord> ExecutePrivacyReportAsync(CharacterToolCall call, CancellationToken ct)
    {
        using IServiceScope scope = scopeFactory.CreateScope();
        PrivacyReportService privacy = scope.ServiceProvider.GetRequiredService<PrivacyReportService>();
        PrivacyReportDto report = privacy.BuildReport();
        await Task.CompletedTask;
        string services = report.Services.Count == 0
            ? "no external services contacted"
            : string.Join(", ", report.Services.Select(s => s.Name));
        return Succeeded(
            call,
            $"{report.Requests.Count} recent outbound request(s) across: {services}.");
    }

    private async Task<ChatActionRecord> ExecuteMediaCleanupPreviewAsync(CharacterToolCall call, CancellationToken ct)
    {
        using IServiceScope scope = scopeFactory.CreateScope();
        MediaCleanupService cleanup = scope.ServiceProvider.GetRequiredService<MediaCleanupService>();
        MediaCleanupPlanDto plan = await cleanup.PlanOrphanLibraryFilesAsync(ct);
        int files = plan.AnnouncementFiles + plan.TrackFiles;
        if (files == 0)
        {
            return Succeeded(call, "No unreferenced media files to clean up.");
        }

        string token = cleanup.IssuePreviewToken();
        return Succeeded(
            call,
            $"{files} unreferenced file(s) ({plan.BytesToDelete / (1024 * 1024):0.0} MB) can be removed. "
            + $"Pass previewToken={token} to RunMediaCleanup within 15 minutes to delete them.");
    }

    private async Task<ChatActionRecord> ExecuteRunMediaCleanupAsync(
        CharacterToolCall call,
        ChatActionContext context,
        CancellationToken ct)
    {
        string previewToken = Require(call, "previewToken");
        string reason = Require(call, "reason");

        using IServiceScope scope = scopeFactory.CreateScope();
        MediaCleanupService cleanup = scope.ServiceProvider.GetRequiredService<MediaCleanupService>();
        if (!cleanup.ValidatePreviewToken(previewToken))
        {
            return Failed(call, "That preview token is invalid or expired. Run MediaCleanupPreview again first.");
        }

        if (await GateAsync(call, context, ApprovalRisk.Library, $"Delete unreferenced media files ({reason})", ct)
            is { } queued)
        {
            return queued;
        }

        MediaCleanupStatusDto status = await cleanup.StartDeleteOrphanLibraryFilesAsync(ct);
        return Succeeded(call, $"Media cleanup started ({status.Status}); freed files will drop out of the data root.");
    }
}

using WhipRadio.Core.Helpers;

namespace WhipRadio.Orchestrator.Services;

public sealed class NightlyModeratorMemoryDistillationService(
    IServiceScopeFactory scopeFactory,
    TimeProvider timeProvider,
    ILogger<NightlyModeratorMemoryDistillationService> logger) : BackgroundService
{
    private static readonly TimeSpan CheckInterval = TimeSpan.FromMinutes(30);

    private DateOnly? lastDistilledDay;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await stoppingToken.DelayNoThrow(TimeSpan.FromMinutes(5));

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await DistillIfDueAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Nightly moderator memory distillation failed");
            }

            try
            {
                await Task.Delay(CheckInterval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private async Task DistillIfDueAsync(CancellationToken ct)
    {
        var now = timeProvider.GetLocalNow();
        if (now.Hour != 3)
        {
            return;
        }

        var day = DateOnly.FromDateTime(now.DateTime.AddDays(-1));
        if (lastDistilledDay == day)
        {
            return;
        }

        using var scope = scopeFactory.CreateScope();
        var memoryService = scope.ServiceProvider.GetRequiredService<ModeratorMemoryService>();
        var distilled = await memoryService.DistillDayAsync(day, ct);
        lastDistilledDay = day;
        logger.LogInformation("Nightly moderator memory distillation completed for {Date}; {Count} host(s)",
            day, distilled);
    }
}

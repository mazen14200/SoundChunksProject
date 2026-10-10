using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace SoundChunksWeb.Services;

/// <summary>
/// Background service that runs Quran cache cleanup daily at 3:30 AM Egypt time.
/// </summary>
public class QuranCacheCleanupService : BackgroundService
{
    private readonly IQuranAudioService _quranAudioService;
    private readonly ILogger<QuranCacheCleanupService> _logger;
    private static readonly TimeZoneInfo EgyptTimeZone = TimeZoneInfo.FindSystemTimeZoneById("Egypt Standard Time");

    public QuranCacheCleanupService(
        IQuranAudioService quranAudioService,
        ILogger<QuranCacheCleanupService> logger)
    {
        _quranAudioService = quranAudioService;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Quran Cache Cleanup Service started. Will run daily at 3:30 AM Egypt time.");

        while (!stoppingToken.IsCancellationRequested)
        {
            var nowUtc = DateTime.UtcNow;
            var nowEgypt = TimeZoneInfo.ConvertTimeFromUtc(nowUtc, EgyptTimeZone);

            // Calculate next 3:30 AM Egypt time
            var nextRun = new DateTime(nowEgypt.Year, nowEgypt.Month, nowEgypt.Day, 3, 30, 0, DateTimeKind.Unspecified);
            if (nowEgypt.TimeOfDay >= nextRun.TimeOfDay)
            {
                nextRun = nextRun.AddDays(1);
            }

            // Convert back to UTC for the delay calculation
            var nextRunUtc = TimeZoneInfo.ConvertTimeToUtc(nextRun, EgyptTimeZone);
            var delay = nextRunUtc - nowUtc;

            _logger.LogInformation("Next cache cleanup scheduled for: {NextRunEgypt} Egypt time (in {Delay:hh\\:mm\\:ss})",
                nextRun.ToString("yyyy-MM-dd HH:mm:ss"), delay);

            try
            {
                await Task.Delay(delay, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                // Service is shutting down
                break;
            }

            // Perform cleanup
            if (!stoppingToken.IsCancellationRequested)
            {
                await PerformCleanupAsync(stoppingToken);
            }
        }

        _logger.LogInformation("Quran Cache Cleanup Service stopped.");
    }

    private async Task PerformCleanupAsync(CancellationToken cancellationToken)
    {
        try
        {
            _logger.LogInformation("Starting Quran cache cleanup at {Time}", DateTime.UtcNow);

            await Task.Run(() =>
            {
                _quranAudioService.TrimCache();
            }, cancellationToken);

            _logger.LogInformation("Quran cache cleanup completed successfully at {Time}", DateTime.UtcNow);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during Quran cache cleanup");
        }
    }
}

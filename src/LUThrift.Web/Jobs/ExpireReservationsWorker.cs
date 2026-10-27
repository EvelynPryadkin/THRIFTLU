using LUThrift.Web.Configuration;
using LUThrift.Web.Services;
using Microsoft.Extensions.Options;

namespace LUThrift.Web.Jobs;

public sealed class ExpireReservationsWorker(
    IServiceScopeFactory scopeFactory,
    IOptions<ReservationOptions> options,
    TimeProvider timeProvider,
    ILogger<ExpireReservationsWorker> logger) : BackgroundService
{
    private readonly TimeSpan pollInterval = TimeSpan.FromSeconds(Math.Clamp(
        options.Value.ExpirationPollSeconds, 1, ReservationOptions.MaximumExpirationPollSeconds));
    private readonly int batchSize = Math.Clamp(
        options.Value.ExpirationBatchSize, 1, ReservationOptions.MaximumExpirationBatchSize);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                // Each pass owns a fresh context, including after a failed batch.
                await using var scope = scopeFactory.CreateAsyncScope();
                var service = scope.ServiceProvider.GetRequiredService<ReservationService>();
                var result = await service.ExpireBatchAsync(batchSize, stoppingToken);
                if (result.Failed > 0)
                {
                    logger.LogWarning("Some reservation expirations could not be completed and will be retried.");
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception)
            {
                // Exception details may contain database or account information.
                logger.LogError("The reservation expiration pass could not be completed and will be retried.");
            }

            try
            {
                await Task.Delay(pollInterval, timeProvider, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }
}

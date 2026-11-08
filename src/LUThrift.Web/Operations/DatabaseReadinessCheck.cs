using LUThrift.Web.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace LUThrift.Web.Operations;

public sealed class DatabaseReadinessCheck(
    ApplicationDbContext database,
    IHttpContextAccessor accessor,
    ILogger<DatabaseReadinessCheck> logger) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            if (await database.Database.CanConnectAsync(cancellationToken))
                return HealthCheckResult.Healthy();
        }
        catch (Exception)
        {
            // Do not attach exceptions to HealthCheckResult: health-check framework
            // logs may serialize them even when the HTTP response is minimal.
        }

        logger.LogWarning(new EventId(1003, "DatabaseReadinessFailed"),
            "Database readiness failed. RequestId: {RequestId}",
            accessor.HttpContext?.TraceIdentifier ?? "background");
        return HealthCheckResult.Unhealthy();
    }
}

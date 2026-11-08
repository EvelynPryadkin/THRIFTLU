using System.Diagnostics;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace LUThrift.Web.Operations;

public static class OperationalSafeguards
{
    private static readonly string[] PayloadBearingFrameworkCategories =
    [
        "Microsoft.AspNetCore", "Microsoft.EntityFrameworkCore", "Npgsql", "Microsoft.Extensions.Diagnostics.HealthChecks"
    ];

    public static void AddOperationalSafeguards(this WebApplicationBuilder builder)
    {
        // Framework request/SQL diagnostics can include query strings, headers,
        // form values or exception text. Use the safe application events below.
        // Application/service/expiration-worker logs and host lifetime logs remain.
        builder.Services.PostConfigure<LoggerFilterOptions>(options =>
        {
            // Wrap each rule because a more-specific category or provider rule
            // otherwise overrides AddFilter("Microsoft.AspNetCore", None).
            for (var index = 0; index < options.Rules.Count; index++)
            {
                var rule = options.Rules[index];
                options.Rules[index] = new LoggerFilterRule(rule.ProviderName, rule.CategoryName, rule.LogLevel,
                    (provider, category, level) => IsSafeCategory(category)
                        && (rule.Filter?.Invoke(provider, category, level) ?? true));
            }
            if (!options.Rules.Any(rule => rule.ProviderName is null && rule.CategoryName is null))
                options.Rules.Add(new LoggerFilterRule(null, null, options.MinLevel,
                    (_, category, _) => IsSafeCategory(category)));
        });

        builder.Services.AddExceptionHandler<SafeRequestExceptionHandler>();
        builder.Services.AddHttpContextAccessor();
        builder.Services.AddHealthChecks().AddCheck<DatabaseReadinessCheck>(
            "database", failureStatus: HealthStatus.Unhealthy, tags: ["ready"], timeout: TimeSpan.FromSeconds(5));
    }

    private static bool IsSafeCategory(string? category) => category is null || !PayloadBearingFrameworkCategories
        .Any(prefix => category.Equals(prefix, StringComparison.Ordinal)
        || category.StartsWith(prefix + ".", StringComparison.Ordinal));

    public static IApplicationBuilder UseSafeRequestLogging(this IApplicationBuilder app)
        => app.Use(async (context, next) =>
        {
            var logger = context.RequestServices.GetRequiredService<ILoggerFactory>()
                .CreateLogger("LUThrift.Web.Operations.Requests");
            var started = Stopwatch.GetTimestamp();
            try
            {
                await next(context);
            }
            catch (Exception)
            {
                // A response that has already started cannot be replaced by an
                // error page. Record the failure safely and let the server abort it.
                logger.LogError(new EventId(1002, "RequestAborted"),
                    "Request could not finish. RequestId: {RequestId}", context.TraceIdentifier);
                throw;
            }
            finally
            {
                logger.LogInformation(new EventId(1000, "RequestCompleted"),
                    "Request completed. RequestId: {RequestId}; StatusCode: {StatusCode}; ElapsedMilliseconds: {ElapsedMilliseconds}",
                    context.TraceIdentifier, context.Response.StatusCode, Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            }
        });

    public static void MapOperationalHealthChecks(this WebApplication app)
    {
        // Empty selection checks only that this running process can answer HTTP.
        app.MapHealthChecks("/health/live", Options(_ => false)).AllowAnonymous();
        app.MapHealthChecks("/health/ready", Options(check => check.Tags.Contains("ready"))).AllowAnonymous();
    }

    private static HealthCheckOptions Options(Func<HealthCheckRegistration, bool> predicate) => new()
    {
        Predicate = predicate,
        AllowCachingResponses = false,
        ResultStatusCodes =
        {
            [HealthStatus.Healthy] = StatusCodes.Status200OK,
            [HealthStatus.Degraded] = StatusCodes.Status503ServiceUnavailable,
            [HealthStatus.Unhealthy] = StatusCodes.Status503ServiceUnavailable
        },
        ResponseWriter = async (context, report) =>
        {
            context.Response.ContentType = "text/plain; charset=utf-8";
            // No exception, connection, check descriptions or dependency names.
            await context.Response.WriteAsync(report.Status == HealthStatus.Healthy ? "Healthy\n" : "Unhealthy\n",
                context.RequestAborted);
        }
    };
}

using Microsoft.AspNetCore.Diagnostics;

namespace LUThrift.Web.Operations;

public sealed class SafeRequestExceptionHandler(ILogger<SafeRequestExceptionHandler> logger) : IExceptionHandler
{
    public ValueTask<bool> TryHandleAsync(HttpContext context, Exception exception, CancellationToken cancellationToken)
    {
        // Exception messages, inner exceptions and stack traces can contain account
        // data, SQL or connection strings. Keep only a server request reference.
        logger.LogError(new EventId(1001, "RequestFailed"),
            "Request failed. RequestId: {RequestId}", context.TraceIdentifier);
        // Let the exception middleware re-execute the database-independent /Error page.
        return ValueTask.FromResult(false);
    }
}

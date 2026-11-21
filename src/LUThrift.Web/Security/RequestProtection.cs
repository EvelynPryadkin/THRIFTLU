using System.Globalization;
using System.Security.Claims;
using System.Threading.RateLimiting;
using LUThrift.Web.Configuration;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;

namespace LUThrift.Web.Security;

public static class RequestProtection
{
    public const string LoginPolicy = "login-posts";
    public const string RegistrationPolicy = "registration-posts";
    public const string ResendConfirmationPolicy = "resend-confirmation-posts";
    public const string ReservationPolicy = "reservation-posts";
    public const string PickupPolicy = "pickup-posts";
    public const string ImageUploadPolicy = "image-upload-posts";

    public static IServiceCollection AddRequestProtection(
        this IServiceCollection services, IConfiguration configuration, IHostEnvironment environment)
    {
        services.AddOptions<RequestProtectionOptions>()
            .Bind(configuration.GetSection(RequestProtectionOptions.SectionName))
            .Validate(options => options.IsValid(),
                "RequestProtection limits require PermitLimit between 1 and 1000 and WindowSeconds between 1 and 3600.")
            .ValidateOnStart();

        var securePolicy = environment.IsDevelopment() ? CookieSecurePolicy.SameAsRequest : CookieSecurePolicy.Always;
        foreach (var scheme in new[] { IdentityConstants.ApplicationScheme, IdentityConstants.ExternalScheme,
                     IdentityConstants.TwoFactorRememberMeScheme, IdentityConstants.TwoFactorUserIdScheme })
        {
            services.Configure<CookieAuthenticationOptions>(scheme, options =>
            {
                options.Cookie.SecurePolicy = securePolicy;
                options.Cookie.HttpOnly = true;
                options.Cookie.SameSite = SameSiteMode.Lax;
            });
        }

        services.AddRateLimiter(_ => { });
        services.AddOptions<RateLimiterOptions>().Configure<IOptions<RequestProtectionOptions>>((options, settings) =>
        {
            var limits = settings.Value;
            AddPostPolicy(options, LoginPolicy, limits.Login);
            AddPostPolicy(options, RegistrationPolicy, limits.Registration);
            AddPostPolicy(options, ResendConfirmationPolicy, limits.ResendConfirmation);
            AddPostPolicy(options, ReservationPolicy, limits.Reservations, authenticatedOnly: true);
            AddPostPolicy(options, PickupPolicy, limits.Pickups, authenticatedOnly: true);
            AddPostPolicy(options, ImageUploadPolicy, limits.ImageUploads, authenticatedOnly: true, handler: "UploadPhoto");

            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            options.OnRejected = async (context, cancellationToken) =>
            {
                // Fixed-window leases provide retry metadata. Keep a conservative
                // fallback if a future limiter cannot provide it.
                var seconds = context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retry)
                    ? Math.Max(1, (int)Math.Ceiling(retry.TotalSeconds)) : 3600;
                var response = context.HttpContext.Response;
                response.StatusCode = StatusCodes.Status429TooManyRequests;
                response.Headers.RetryAfter = seconds.ToString(CultureInfo.InvariantCulture);
                response.Headers.CacheControl = "no-store";
                response.ContentType = "text/plain; charset=utf-8";
                await response.WriteAsync(
                    $"Too many requests. Wait {seconds} seconds, then try again. This request was not processed.", cancellationToken);
            };
        });
        return services;
    }

    public static void ConfigurePages(RazorPagesOptions options)
    {
        // Metadata belongs to the Razor Page endpoint, not individual handlers.
        options.Conventions.AddAreaPageApplicationModelConvention("Identity", "/Account/Login",
            model => model.EndpointMetadata.Add(new EnableRateLimitingAttribute(LoginPolicy)));
        options.Conventions.AddAreaPageApplicationModelConvention("Identity", "/Account/Register",
            model => model.EndpointMetadata.Add(new EnableRateLimitingAttribute(RegistrationPolicy)));
        options.Conventions.AddAreaPageApplicationModelConvention("Identity", "/Account/ResendEmailConfirmation",
            model => model.EndpointMetadata.Add(new EnableRateLimitingAttribute(ResendConfirmationPolicy)));
        options.Conventions.AddPageApplicationModelConvention("/Account/Reserve",
            model => model.EndpointMetadata.Add(new EnableRateLimitingAttribute(ReservationPolicy)));
        options.Conventions.AddPageApplicationModelConvention("/Account/Reservations",
            model => model.EndpointMetadata.Add(new EnableRateLimitingAttribute(ReservationPolicy)));
        options.Conventions.AddPageApplicationModelConvention("/Staff/Pickups/Index",
            model => model.EndpointMetadata.Add(new EnableRateLimitingAttribute(PickupPolicy)));
        options.Conventions.AddPageApplicationModelConvention("/Staff/Inventory/Edit",
            model => model.EndpointMetadata.Add(new EnableRateLimitingAttribute(ImageUploadPolicy)));
    }

    private static void AddPostPolicy(RateLimiterOptions options, string name, RateLimitWindowOptions limits,
        bool authenticatedOnly = false, string? handler = null)
    {
        options.AddPolicy(name, context =>
        {
            var authenticated = context.User.Identity?.IsAuthenticated == true;
            if (!HttpMethods.IsPost(context.Request.Method) || (authenticatedOnly && !authenticated)
                || (handler is not null && !string.Equals(HandlerName(context), handler, StringComparison.OrdinalIgnoreCase)))
                return RateLimitPartition.GetNoLimiter(name + ":unlimited");

            var userId = authenticated ? context.User.FindFirstValue(ClaimTypes.NameIdentifier) : null;
            var remote = context.Connection.RemoteIpAddress;
            if (remote?.IsIPv4MappedToIPv6 == true) remote = remote.MapToIPv4();
            // Only the authenticated ID and connection IP are trusted. Never use
            // submitted IDs, email, Host, Forwarded, or X-Forwarded-For headers.
            var identity = !string.IsNullOrWhiteSpace(userId) ? "user:" + userId : "ip:" + (remote?.ToString() ?? "unknown");
            return RateLimitPartition.GetFixedWindowLimiter(name + ":" + identity, _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = limits.PermitLimit,
                Window = TimeSpan.FromSeconds(limits.WindowSeconds),
                QueueLimit = 0,
                QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                AutoReplenishment = true
            });
        });
    }

    private static string? HandlerName(HttpContext context)
    {
        // Match Razor Pages' selector: a nonempty route value, otherwise the first
        // query value. Reading the form here would both buffer uploads and disagree
        // with handler selection. Extra query values cannot bypass the same choice.
        var routeHandler = Convert.ToString(context.Request.RouteValues["handler"], CultureInfo.InvariantCulture);
        return !string.IsNullOrEmpty(routeHandler) ? routeHandler
            : context.Request.Query.TryGetValue("handler", out var values) && values.Count > 0 ? values[0] : null;
    }

    public static IApplicationBuilder UseRequestProtectionHeaders(this IApplicationBuilder app)
        => app.Use(async (context, next) =>
        {
            context.Response.OnStarting(() =>
            {
                context.Response.Headers.XContentTypeOptions = "nosniff";
                context.Response.Headers.TryAdd("Referrer-Policy", "strict-origin-when-cross-origin");
                context.Response.Headers.XFrameOptions = "DENY";
                context.Response.Headers.ContentSecurityPolicy = "frame-ancestors 'none'";
                return Task.CompletedTask;
            });
            await next(context);
        });
}

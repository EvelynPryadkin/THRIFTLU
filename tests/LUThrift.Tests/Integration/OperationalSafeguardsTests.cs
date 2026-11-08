using System.Collections.Concurrent;
using System.Data.Common;
using System.Net;
using System.Text.RegularExpressions;
using LUThrift.Web.Authorization;
using LUThrift.Web.Data;
using LUThrift.Web.Models;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace LUThrift.Tests.Integration;

public sealed class OperationalSafeguardsTests
{
    private const string Login = "/Identity/Account/Login";
    private const string Secret = "synthetic-private-diagnostic-marker";
    private static readonly string[] SensitiveValues =
    [
        Secret, "synthetic-password-marker", "synthetic-cookie-marker", "synthetic-token-marker",
        "synthetic-photo-marker.png", "student-search@example.invalid", "Host=private.invalid"
    ];

    [Theory]
    [InlineData("GET")]
    [InlineData("POST")]
    public async Task UnhandledRequests_RenderSafe500AndCorrelatedLogs(string method)
    {
        await using var test = TestHost.Create();
        using var request = new HttpRequestMessage(new HttpMethod(method),
            Login + "?test-fault=1&email=" + Uri.EscapeDataString(SensitiveValues[5]));
        request.Headers.Add("Cookie", "synthetic=" + SensitiveValues[2]);
        request.Headers.Add("Authorization", "Bearer " + SensitiveValues[3]);
        if (method == "POST")
        {
            var token = Token(await test.Client.GetStringAsync(Login));
            var body = new MultipartFormDataContent();
            body.Add(new StringContent(token), "__RequestVerificationToken");
            body.Add(new StringContent(SensitiveValues[1]), "Input.Password");
            body.Add(new StringContent(SensitiveValues[5]), "Input.Email");
            body.Add(new ByteArrayContent([1, 2, 3]), "Photo", SensitiveValues[4]);
            request.Content = body;
        }
        test.Logs.Entries.Clear();

        using var response = await test.Client.SendAsync(request);
        var html = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Contains("Something went wrong", html);
        Assert.Contains("Browse items", html);
        Assert.Contains("Go to home", html);
        Assert.DoesNotContain("Development Mode", html);
        Assert.True(response.Headers.CacheControl?.NoStore);
        var reference = Reference(html);
        Assert.Contains(test.Logs.Entries, entry => entry.Event.Name == "RequestFailed"
            && entry.Fields.TryGetValue("RequestId", out var value) && value == reference);
        Assert.Contains(test.Logs.Entries, entry => entry.Event.Name == "RequestCompleted"
            && entry.Fields.TryGetValue("RequestId", out var value) && value == reference);
        AssertSafe(html, test.Logs);
    }

    [Theory]
    [InlineData("/missing-operational-page")]
    [InlineData("/Thrift/Details/2147483647")]
    [InlineData("/Thrift/Image/2147483647")]
    public async Task MissingPagesAndItems_ReturnUseful404WithoutEchoingTheUrl(string path)
    {
        await using var test = TestHost.Create();
        using var response = await test.Client.GetAsync(path + "?email=" + Uri.EscapeDataString(SensitiveValues[5]));
        var html = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Null(response.Headers.Location);
        Assert.Contains("Page not found", html);
        Assert.Contains("Browse items", html);
        Assert.NotEmpty(Reference(html));
        Assert.True(response.Headers.CacheControl?.NoStore);
        AssertSafe(html, test.Logs);
    }

    [Fact]
    public async Task AntiforgeryFailureAndAuthenticationRedirects_RetainTheirStatusCodes()
    {
        await using var test = TestHost.Create();
        using (var anonymous = await test.Client.GetAsync("/Staff/Pickups"))
        {
            Assert.Equal(HttpStatusCode.Redirect, anonymous.StatusCode);
            Assert.Contains(Login, anonymous.Headers.Location!.OriginalString);
        }
        await test.SignInStudentAsync();
        using (var student = await test.Client.GetAsync("/Staff/Pickups"))
        {
            Assert.Equal(HttpStatusCode.Redirect, student.StatusCode);
            Assert.Contains("/Identity/Account/AccessDenied", student.Headers.Location!.OriginalString);
        }
        using var missingToken = await test.Client.PostAsync("/Account/Reserve",
            new FormUrlEncodedContent(new Dictionary<string, string> { ["ListingId"] = "1" }));
        Assert.Equal(HttpStatusCode.BadRequest, missingToken.StatusCode);
        Assert.Null(missingToken.Headers.Location);
        Assert.DoesNotContain("Something went wrong", await missingToken.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task DatabaseFailureForSignedInVisitor_StillRendersTheDatabaseIndependentErrorPage()
    {
        await using var test = TestHost.Create();
        await test.SignInStudentAsync();
        test.Faults.FailCommands = true;
        test.Logs.Entries.Clear();

        using var response = await test.Client.GetAsync("/Thrift?search=" + Uri.EscapeDataString(SensitiveValues[5]));
        var html = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Contains("Something went wrong", html);
        Assert.NotEmpty(Reference(html));
        Assert.Equal(1, test.Faults.FailedCommands); // No second DB lookup from the error layout.
        AssertSafe(html, test.Logs);
    }

    [Theory]
    [InlineData("/health/live")]
    [InlineData("/health/ready")]
    public async Task HealthEndpoints_ArePublicMinimalAndNotCached(string path)
    {
        await using var test = TestHost.Create();
        using var response = await test.Client.GetAsync(path);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Healthy\n", await response.Content.ReadAsStringAsync());
        Assert.Equal("text/plain", response.Content.Headers.ContentType!.MediaType);
        Assert.True(response.Headers.CacheControl?.NoStore);
        Assert.Equal("nosniff", Assert.Single(response.Headers.GetValues("X-Content-Type-Options")));
    }

    [Fact]
    public async Task FailedDatabaseConnection_ReturnsMinimal503WhileLivenessStillWorks()
    {
        await using var test = TestHost.Create();
        // Startup and role initialization succeed first. SQLite treats :memory:
        // as existing, so failed probes use a scoped missing-file connection.
        test.Faults.FailConnections = true;
        test.Logs.Entries.Clear();

        using var ready = await test.Client.GetAsync("/health/ready");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, ready.StatusCode);
        Assert.Equal("Unhealthy\n", await ready.Content.ReadAsStringAsync());
        Assert.True(ready.Headers.CacheControl?.NoStore);
        Assert.Equal("text/plain", ready.Content.Headers.ContentType!.MediaType);
        Assert.True(test.Faults.FailedConnections > 0);
        var attempts = test.Faults.FailedConnections;
        using var live = await test.Client.GetAsync("/health/live");
        Assert.Equal(HttpStatusCode.OK, live.StatusCode);
        Assert.Equal("Healthy\n", await live.Content.ReadAsStringAsync());
        Assert.Equal(attempts, test.Faults.FailedConnections);
        Assert.Contains(test.Logs.Entries, entry => entry.Event.Name == "DatabaseReadinessFailed"
            && entry.Fields.TryGetValue("RequestId", out var value) && !string.IsNullOrWhiteSpace(value));
        AssertSafe(await ready.Content.ReadAsStringAsync(), test.Logs);
    }

    [Fact]
    public async Task VerboseFrameworkConfiguration_CannotReenableSensitivePayloadLogs()
    {
        await using var test = TestHost.Create();
        var factory = test.Services.GetRequiredService<ILoggerFactory>();
        test.Logs.Entries.Clear();
        foreach (var category in new[]
        {
            "Microsoft.AspNetCore.Hosting.Diagnostics", "Microsoft.EntityFrameworkCore.Database.Command",
            "Npgsql.Command", "Microsoft.Extensions.Diagnostics.HealthChecks.DefaultHealthCheckService"
        })
            factory.CreateLogger(category).LogError(new InvalidOperationException(Secret), "{PrivateDetail}", Secret);
        factory.CreateLogger("LUThrift.Web.Jobs.ExpireReservationsWorker").LogWarning(
            new EventId(9001, "SyntheticExpirationFailure"), "The reservation expiration pass will be retried.");

        Assert.Single(test.Logs.Entries);
        Assert.Equal("SyntheticExpirationFailure", test.Logs.Entries.Single().Event.Name);
        AssertSafe(string.Empty, test.Logs);
    }

    private static string Token(string html)
    {
        var match = Regex.Match(html, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"");
        Assert.True(match.Success);
        return WebUtility.HtmlDecode(match.Groups[1].Value);
    }

    private static string Reference(string html)
    {
        var match = Regex.Match(html, "Request reference:</strong>\\s*<code[^>]*>([^<]+)</code>");
        Assert.True(match.Success);
        return WebUtility.HtmlDecode(match.Groups[1].Value);
    }

    private static void AssertSafe(string response, LogCapture logs)
    {
        foreach (var marker in SensitiveValues)
        {
            Assert.False(response.Contains(marker, StringComparison.OrdinalIgnoreCase));
            Assert.False(response.Contains(Uri.EscapeDataString(marker), StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain(logs.Entries, entry => entry.Message.Contains(marker, StringComparison.OrdinalIgnoreCase)
                || entry.Fields.Values.Any(value => value.Contains(marker, StringComparison.OrdinalIgnoreCase)));
        }
        Assert.All(logs.Entries, entry => Assert.Null(entry.Exception));
    }

    private sealed class ThrowingPageFilter : IAsyncPageFilter
    {
        public Task OnPageHandlerSelectionAsync(PageHandlerSelectedContext context) => Task.CompletedTask;
        public async Task OnPageHandlerExecutionAsync(PageHandlerExecutingContext context, PageHandlerExecutionDelegate next)
        {
            if (context.ActionDescriptor.ViewEnginePath == "/Account/Login"
                && context.HttpContext.Request.Query.ContainsKey("test-fault"))
                throw new InvalidOperationException(string.Join("; ", SensitiveValues));
            await next();
        }
    }

    private sealed class DatabaseFaults
    {
        public string UnavailableConnection { get; } = new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(Path.GetTempPath(), "luthrift-health-" + Guid.NewGuid().ToString("N"), "probe.db"),
            Mode = SqliteOpenMode.ReadWrite
        }.ToString();
        public bool FailCommands;
        public bool FailConnections;
        public int FailedCommands;
        public int FailedConnections;
    }

    private sealed class CommandFailure(DatabaseFaults faults) : DbCommandInterceptor
    {
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            if (faults.FailCommands)
            {
                faults.FailedCommands++;
                throw new InvalidOperationException(string.Join("; ", SensitiveValues));
            }
            return ValueTask.FromResult(result);
        }
    }

    private sealed class ConnectionFailure(DatabaseFaults faults) : DbConnectionInterceptor
    {
        public override InterceptionResult ConnectionOpening(DbConnection connection,
            ConnectionEventData eventData, InterceptionResult result)
        {
            if (faults.FailConnections)
            {
                faults.FailedConnections++;
                throw new InvalidOperationException(string.Join("; ", SensitiveValues));
            }
            return result;
        }

        public override ValueTask<InterceptionResult> ConnectionOpeningAsync(DbConnection connection,
            ConnectionEventData eventData, InterceptionResult result, CancellationToken cancellationToken = default)
        {
            if (faults.FailConnections)
            {
                faults.FailedConnections++;
                throw new InvalidOperationException(string.Join("; ", SensitiveValues));
            }
            return ValueTask.FromResult(result);
        }
    }

    private sealed record LogEntry(EventId Event, string Message, Exception? Exception, Dictionary<string, string> Fields);
    private sealed class LogCapture : ILoggerProvider
    {
        public ConcurrentQueue<LogEntry> Entries { get; } = new();
        public ILogger CreateLogger(string categoryName) => new CapturedLogger(this);
        public void Dispose() { }
        private sealed class CapturedLogger(LogCapture capture) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                var fields = state is IEnumerable<KeyValuePair<string, object?>> values
                    ? values.ToDictionary(pair => pair.Key, pair => pair.Value?.ToString() ?? string.Empty) : [];
                capture.Entries.Enqueue(new(eventId, formatter(state, exception), exception, fields));
            }
        }
    }

    private sealed class TestHost(AuthorizationWebApplicationFactory original,
        WebApplicationFactory<ApplicationDbContext> factory, LogCapture logs, DatabaseFaults faults) : IAsyncDisposable
    {
        public IServiceProvider Services => factory.Services;
        public LogCapture Logs { get; } = logs;
        public DatabaseFaults Faults { get; } = faults;
        public HttpClient Client { get; } = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://localhost"), AllowAutoRedirect = false
        });

        public static TestHost Create()
        {
            var original = new AuthorizationWebApplicationFactory();
            var logs = new LogCapture();
            var faults = new DatabaseFaults();
            var factory = original.WithWebHostBuilder(builder =>
            {
                builder.UseEnvironment("Production");
                builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Logging:LogLevel:Microsoft.AspNetCore.Hosting.Diagnostics"] = "Trace",
                    ["Logging:LogLevel:Microsoft.EntityFrameworkCore.Database.Command"] = "Trace",
                    ["Logging:LogLevel:Npgsql.Command"] = "Trace",
                    ["Logging:LogLevel:Microsoft.Extensions.Diagnostics.HealthChecks.DefaultHealthCheckService"] = "Trace"
                }));
                builder.ConfigureServices(services =>
                {
                    services.AddSingleton<ILoggerProvider>(logs);
                    services.AddDbContext<ApplicationDbContext>(options =>
                    {
                        if (faults.FailConnections) options.UseSqlite(faults.UnavailableConnection);
                        options.AddInterceptors(new CommandFailure(faults), new ConnectionFailure(faults));
                    });
                    services.Configure<MvcOptions>(options => options.Filters.Add(new ThrowingPageFilter()));
                });
            });
            return new(original, factory, logs, faults);
        }

        public async Task SignInStudentAsync()
        {
            await using var scope = Services.CreateAsyncScope();
            var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            var email = IdentityTestContext.NewEmail();
            var user = new ApplicationUser { DisplayName = "Synthetic operational student", UserName = email, Email = email };
            Assert.True((await users.CreateAsync(user)).Succeeded);
            Assert.True((await users.AddToRoleAsync(user, ApplicationRoles.Student)).Succeeded);
            var principal = await scope.ServiceProvider.GetRequiredService<SignInManager<ApplicationUser>>().CreateUserPrincipalAsync(user);
            var cookie = scope.ServiceProvider.GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>().Get(IdentityConstants.ApplicationScheme);
            var now = DateTimeOffset.UtcNow;
            var ticket = new AuthenticationTicket(principal, new AuthenticationProperties
            {
                IssuedUtc = now, ExpiresUtc = now.AddMinutes(30)
            }, IdentityConstants.ApplicationScheme);
            Client.DefaultRequestHeaders.Add("Cookie", $"{cookie.Cookie.Name}={cookie.TicketDataFormat.Protect(ticket)}");
        }

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await factory.DisposeAsync();
            await original.DisposeAsync();
        }
    }
}

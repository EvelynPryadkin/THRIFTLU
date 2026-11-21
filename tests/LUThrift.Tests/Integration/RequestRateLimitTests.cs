using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;
using LUThrift.Web.Authorization;
using LUThrift.Web.Configuration;
using LUThrift.Web.Data;
using LUThrift.Web.Models;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace LUThrift.Tests.Integration;

public sealed class RequestRateLimitTests
{
    private const string Login = "/Identity/Account/Login";
    private const string Register = "/Identity/Account/Register";
    private const string Resend = "/Identity/Account/ResendEmailConfirmation";
    private const string Reserve = "/Account/Reserve";
    private const string Cancel = "/Account/Reservations?handler=Cancel";
    private const string Confirm = "/Staff/Pickups?handler=ConfirmPickup";
    private const string Missing = "/Staff/Pickups?handler=ReportMissing";
    private const string Upload = "/Staff/Inventory/Edit?id=1&handler=UploadPhoto";

    [Theory]
    [InlineData(Login, null, HttpStatusCode.OK)]
    [InlineData(Register, null, HttpStatusCode.OK)]
    [InlineData(Resend, null, HttpStatusCode.OK)]
    [InlineData(Reserve, ApplicationRoles.Student, HttpStatusCode.Redirect)]
    [InlineData(Cancel, ApplicationRoles.Student, HttpStatusCode.Redirect)]
    [InlineData(Confirm, ApplicationRoles.Staff, HttpStatusCode.Redirect)]
    [InlineData(Missing, ApplicationRoles.Administrator, HttpStatusCode.Redirect)]
    [InlineData(Upload, ApplicationRoles.Staff, HttpStatusCode.OK)]
    public async Task SelectedPostRoutes_AllowOneNormalRequestThenReturnUseful429(
        string path, string? role, HttpStatusCode normalStatus)
    {
        await using var test = await RateTest.CreateAsync();
        var client = role is null ? test.NewClient() : (await test.SignInAsync(role)).Client;
        var token = await TokenAsync(client);

        using var allowed = await PostAsync(client, path, token);
        Assert.Equal(normalStatus, allowed.StatusCode);
        var before = await test.ReservationsAsync();
        using var rejected = await PostAsync(client, path, token);
        await AssertRejectedAsync(rejected);
        Assert.Equal(before, await test.ReservationsAsync());
        if (path == Reserve) Assert.Single(before);
    }

    [Fact]
    public async Task LoginRegistrationAndResend_HaveSeparateBudgetsAndDoNotLimitTheirGetForms()
    {
        await using var test = await RateTest.CreateAsync();
        var client = test.NewClient();
        var token = await TokenAsync(client);
        using (var firstLogin = await PostAsync(client, Login, token)) Assert.Equal(HttpStatusCode.OK, firstLogin.StatusCode);
        using (var limitedLogin = await PostAsync(client, Login, token)) await AssertRejectedAsync(limitedLogin);
        using (var firstRegistration = await PostAsync(client, Register, token)) Assert.Equal(HttpStatusCode.OK, firstRegistration.StatusCode);
        using (var limitedRegistration = await PostAsync(client, Register, token)) await AssertRejectedAsync(limitedRegistration);
        using (var firstResend = await PostAsync(client, Resend, token)) Assert.Equal(HttpStatusCode.OK, firstResend.StatusCode);
        using (var limitedResend = await PostAsync(client, Resend, token)) await AssertRejectedAsync(limitedResend);
        foreach (var path in new[] { Login, Register, Resend })
        {
            using var page = await client.GetAsync(path);
            Assert.Equal(HttpStatusCode.OK, page.StatusCode);
            using var head = await client.SendAsync(new HttpRequestMessage(HttpMethod.Head, path));
            Assert.Equal(HttpStatusCode.OK, head.StatusCode);
        }
    }

    [Theory]
    [InlineData(Login)]
    [InlineData(Resend)]
    public async Task AnonymousPartitions_UseConnectionIpAndIgnoreForwardedHeaders(string path)
    {
        await using var test = await RateTest.CreateAsync();
        var first = test.NewClient("203.0.113.10");
        var sameIp = test.NewClient("203.0.113.10");
        var differentIp = test.NewClient("203.0.113.11");
        var token = await TokenAsync(first);
        using (var allowed = await PostAsync(first, path, token)) Assert.Equal(HttpStatusCode.OK, allowed.StatusCode);
        first.DefaultRequestHeaders.Add("X-Forwarded-For", "198.51.100.50");
        first.DefaultRequestHeaders.Add("Forwarded", "for=198.51.100.51;proto=https");
        using (var forged = await PostAsync(first, path, token)) await AssertRejectedAsync(forged);
        using (var shared = await PostAsync(sameIp, path, await TokenAsync(sameIp))) await AssertRejectedAsync(shared);
        using (var isolated = await PostAsync(differentIp, path, await TokenAsync(differentIp)))
            Assert.Equal(HttpStatusCode.OK, isolated.StatusCode);
    }

    [Fact]
    public async Task AuthenticatedPartitions_UseUserIdEvenWhenIpChangesOrUsersShareAnIp()
    {
        await using var test = await RateTest.CreateAsync();
        var first = await test.SignInAsync(ApplicationRoles.Student);
        var second = await test.SignInAsync(ApplicationRoles.Student);
        var firstToken = await TokenAsync(first.Client);
        using (var created = await PostAsync(first.Client, Reserve, firstToken, listingId: 1))
            Assert.Equal(HttpStatusCode.Redirect, created.StatusCode);

        first.Client.DefaultRequestHeaders.Remove(TestConnectionFilter.AddressHeader);
        first.Client.DefaultRequestHeaders.Add(TestConnectionFilter.AddressHeader, "203.0.113.90");
        first.Client.DefaultRequestHeaders.Add("X-Forwarded-For", "198.51.100.99");
        using (var sameUser = await PostAsync(first.Client, Reserve, firstToken, listingId: 2)) await AssertRejectedAsync(sameUser);
        using (var otherUser = await PostAsync(second.Client, Reserve, await TokenAsync(second.Client), listingId: 2))
            Assert.Equal(HttpStatusCode.Redirect, otherUser.StatusCode);
        var reservations = await test.ReservationsAsync();
        Assert.Equal(2, reservations.Length);
        Assert.Contains(reservations, row => row.UserId == first.User.Id && row.ListingId == 1);
        Assert.Contains(reservations, row => row.UserId == second.User.Id && row.ListingId == 2);
    }

    [Fact]
    public async Task RelatedReservationAndPickupHandlers_ShareBudgetsButPoliciesRemainIndependent()
    {
        await using var test = await RateTest.CreateAsync();
        var account = await test.SignInAsync(ApplicationRoles.Student, ApplicationRoles.Staff);
        var token = await TokenAsync(account.Client);
        using (var reserve = await PostAsync(account.Client, Reserve, token)) Assert.Equal(HttpStatusCode.Redirect, reserve.StatusCode);
        using (var cancel = await PostAsync(account.Client, Cancel, token)) await AssertRejectedAsync(cancel);
        using (var pickup = await PostAsync(account.Client, Confirm, token)) Assert.Equal(HttpStatusCode.Redirect, pickup.StatusCode);
        using (var missing = await PostAsync(account.Client, Missing, token)) await AssertRejectedAsync(missing);
        const string editableUpload = "/Staff/Inventory/Edit?id=2&handler=UploadPhoto";
        using (var upload = await PostAsync(account.Client, editableUpload, token)) Assert.Equal(HttpStatusCode.OK, upload.StatusCode);
        using (var secondUpload = await PostAsync(account.Client, editableUpload, token)) await AssertRejectedAsync(secondUpload);
        Assert.Single(await test.ReservationsAsync());
    }

    [Fact]
    public async Task PickupIndexAlias_CannotBypassTheSharedPagePolicy()
    {
        await using var test = await RateTest.CreateAsync();
        var staff = await test.SignInAsync(ApplicationRoles.Staff);
        var token = await TokenAsync(staff.Client);
        using (var first = await PostAsync(staff.Client, Confirm, token)) Assert.Equal(HttpStatusCode.Redirect, first.StatusCode);
        using var alias = await PostAsync(staff.Client, "/Staff/Pickups/Index?handler=ReportMissing", token);
        await AssertRejectedAsync(alias);
    }

    [Fact]
    public async Task ImagePolicy_MatchesRazorHandlerSelectionWithoutLimitingUnrelatedEditPosts()
    {
        await using var test = await RateTest.CreateAsync();
        var staff = await test.SignInAsync(ApplicationRoles.Staff);
        var token = await TokenAsync(staff.Client);
        using (var first = await PostAsync(staff.Client, Upload, token)) Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        foreach (var path in new[]
        {
            "/Staff/Inventory/Edit?id=1&handler=uPlOaDpHoTo",
            "/Staff/Inventory/Edit?id=1&handler=UploadPhoto&handler=RemovePhoto"
        })
        {
            using var response = await PostAsync(staff.Client, path, token);
            await AssertRejectedAsync(response);
        }

        using (var edit = await PostAsync(staff.Client, "/Staff/Inventory/Edit?id=1", token))
            Assert.Equal(HttpStatusCode.OK, edit.StatusCode); // Ordinary listing validation, not image throttling.
        using (var remove = await PostAsync(staff.Client, "/Staff/Inventory/Edit?id=1&handler=RemovePhoto", token))
            Assert.Equal(HttpStatusCode.OK, remove.StatusCode); // Missing expected ID: normal photo feedback.
        using var wrongBodyHandler = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = token, ["handler"] = "RemovePhoto"
        });
        using var bodyCannotOverride = await staff.Client.PostAsync(Upload, wrongBodyHandler);
        await AssertRejectedAsync(bodyCannotOverride);
    }

    [Fact]
    public async Task MissingAntiforgeryAndWrongRoles_AreStillRejectedNormally()
    {
        await using var test = await RateTest.CreateAsync();
        var student = await test.SignInAsync(ApplicationRoles.Student);
        using (var missingToken = await student.Client.PostAsync(Reserve, new FormUrlEncodedContent(
                   new Dictionary<string, string> { ["ListingId"] = "1" })))
            Assert.Equal(HttpStatusCode.BadRequest, missingToken.StatusCode);
        using (var afterBudget = await PostAsync(student.Client, Reserve, await TokenAsync(student.Client)))
            await AssertRejectedAsync(afterBudget);
        using (var denied = await PostAsync(student.Client, Confirm, await TokenAsync(student.Client)))
        {
            Assert.Equal(HttpStatusCode.Redirect, denied.StatusCode);
            Assert.Contains("/Identity/Account/AccessDenied", denied.Headers.Location!.OriginalString);
        }
        using var anonymous = await test.NewClient().PostAsync(Reserve, new FormUrlEncodedContent([]));
        Assert.Equal(HttpStatusCode.Redirect, anonymous.StatusCode);
        Assert.Contains("/Identity/Account/Login", anonymous.Headers.Location!.OriginalString);
        Assert.Empty(await test.ReservationsAsync());
    }

    [Fact]
    public async Task ExhaustedPostBudget_DoesNotThrottleBrowsingStaticAssetsOrUnselectedEndpoints()
    {
        await using var test = await RateTest.CreateAsync();
        var client = test.NewClient();
        var token = await TokenAsync(client);
        using (var allowed = await PostAsync(client, Login, token)) Assert.Equal(HttpStatusCode.OK, allowed.StatusCode);
        using (var rejected = await PostAsync(client, Login, token)) await AssertRejectedAsync(rejected);
        foreach (var path in new[] { "/", "/Thrift", "/Thrift/Details/1", "/health/live", "/health/ready" })
        {
            using var response = await client.GetAsync(path);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }
        var home = await client.GetStringAsync("/");
        var css = Regex.Matches(home, "<link\\b[^>]*href=\"([^\"]+)\"", RegexOptions.IgnoreCase)
            .Cast<Match>().Select(match => WebUtility.HtmlDecode(match.Groups[1].Value))
            .First(href => href.Contains("site", StringComparison.Ordinal) && href.Contains(".css", StringComparison.Ordinal));
        using var asset = await client.GetAsync(css);
        Assert.Equal(HttpStatusCode.OK, asset.StatusCode);
        Assert.Null(test.Factory.Services.GetRequiredService<IOptions<RateLimiterOptions>>().Value.GlobalLimiter);
        // The two explicit health routes work above; the unmatched parent path
        // retains its normal response rather than acquiring a global limiter.
        using var unmatched = await client.GetAsync("/health");
        Assert.Equal(HttpStatusCode.NotFound, unmatched.StatusCode);
    }

    [Fact]
    public async Task ConfigurationBinding_ControlsTheActualLoginBudgetAndRetryWindow()
    {
        await using var test = await RateTest.CreateAsync(new Dictionary<string, string?>
        {
            ["RequestProtection:Login:PermitLimit"] = "2",
            ["RequestProtection:Login:WindowSeconds"] = "120"
        }, overrideWindows: false);
        var client = test.NewClient();
        var configured = test.Factory.Services.GetRequiredService<IOptions<RequestProtectionOptions>>().Value;
        Assert.Equal(2, configured.Login.PermitLimit);
        Assert.Equal(120, configured.Login.WindowSeconds);
        var token = await TokenAsync(client);
        for (var attempt = 0; attempt < 2; attempt++)
        {
            using var response = await PostAsync(client, Login, token);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        using var limited = await PostAsync(client, Login, token);
        await AssertRejectedAsync(limited);
        Assert.InRange(limited.Headers.RetryAfter!.Delta!.Value.TotalSeconds, 1, 120);
    }

    [Theory]
    [InlineData("Login:PermitLimit", "0")]
    [InlineData("Registration:PermitLimit", "-1")]
    [InlineData("ResendConfirmation:PermitLimit", "0")]
    [InlineData("ResendConfirmation:WindowSeconds", "3601")]
    [InlineData("ImageUploads:PermitLimit", "1001")]
    [InlineData("Registration:WindowSeconds", "0")]
    [InlineData("Pickups:WindowSeconds", "-1")]
    [InlineData("Reservations:WindowSeconds", "3601")]
    public async Task InvalidConfiguredLimits_FailStartup(string key, string value)
    {
        await using var test = await RateTest.CreateAsync(new Dictionary<string, string?>
        {
            [$"RequestProtection:{key}"] = value
        }, overrideWindows: false);

        var error = Assert.Throws<OptionsValidationException>(() => test.NewClient());
        Assert.Contains("RequestProtection", error.Message, StringComparison.Ordinal);
    }

    private static async Task<string> TokenAsync(HttpClient client)
    {
        var html = await client.GetStringAsync(Login);
        var token = Regex.Match(html, "<input\\b[^>]*name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"", RegexOptions.IgnoreCase);
        Assert.True(token.Success);
        return WebUtility.HtmlDecode(token.Groups[1].Value);
    }

    private static Task<HttpResponseMessage> PostAsync(HttpClient client, string path, string token, int listingId = 1)
        => client.PostAsync(path, new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = token,
            ["ListingId"] = listingId.ToString(CultureInfo.InvariantCulture),
            ["id"] = "0", ["reservationId"] = "0"
        }));

    private static async Task AssertRejectedAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
        Assert.NotNull(response.Headers.RetryAfter?.Delta);
        Assert.InRange(response.Headers.RetryAfter!.Delta!.Value.TotalSeconds, 1, 600);
        Assert.True(response.Headers.CacheControl?.NoStore);
        Assert.Equal("text/plain", response.Content.Headers.ContentType!.MediaType);
        var message = await response.Content.ReadAsStringAsync();
        Assert.Contains("try again", message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("seconds", message, StringComparison.OrdinalIgnoreCase);
    }

    private sealed record Session(HttpClient Client, ApplicationUser User);
    private sealed record SavedReservation(int Id, string UserId, int ListingId, ReservationStatus Status, DateTime Deadline);
    private sealed class FixedClock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(2026, 11, 2, 15, 0, 0, TimeSpan.Zero);
    }

    // TestServer has no real TCP peer. This test-only middleware provides a
    // controlled connection property; production never reads this synthetic header.
    private sealed class TestConnectionFilter : IStartupFilter
    {
        public const string AddressHeader = "X-Test-Connection-Address";
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use(async (context, nextMiddleware) =>
            {
                context.Connection.RemoteIpAddress = IPAddress.Parse(context.Request.Headers[AddressHeader].FirstOrDefault() ?? "203.0.113.10");
                await nextMiddleware(context);
            });
            next(app);
        };
    }

    private sealed class RateTest(AuthorizationWebApplicationFactory original,
        WebApplicationFactory<ApplicationDbContext> factory) : IAsyncDisposable
    {
        public WebApplicationFactory<ApplicationDbContext> Factory { get; } = factory;
        private readonly List<HttpClient> clients = [];
        private bool scheduleInitialized;

        public static Task<RateTest> CreateAsync(
            Dictionary<string, string?>? configuration = null, bool overrideWindows = true)
        {
            var original = new AuthorizationWebApplicationFactory();
            var factory = original.WithWebHostBuilder(builder =>
            {
                builder.UseStaticWebAssets();
                if (configuration is not null)
                    builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(configuration));
                builder.ConfigureServices(services =>
                {
                    services.AddSingleton<IStartupFilter, TestConnectionFilter>();
                    services.RemoveAll<TimeProvider>();
                    services.AddSingleton<TimeProvider, FixedClock>();
                    if (overrideWindows)
                        services.PostConfigure<RequestProtectionOptions>(options =>
                        {
                            foreach (var window in new[] { options.Login, options.Registration, options.ResendConfirmation, options.Reservations, options.Pickups, options.ImageUploads })
                            {
                                window.PermitLimit = 1;
                                window.WindowSeconds = 600;
                            }
                        });
                    services.PostConfigure<ReservationOptions>(options =>
                    {
                        options.CampusTimeZone = "America/Chicago";
                        options.WeeklyPickupIntervals = [new() { Day = "Monday", OpensAt = "09:00", ClosesAt = "17:00" }];
                        options.ClosedDates = [];
                    });
                });
            });
            return Task.FromResult(new RateTest(original, factory));
        }

        public HttpClient NewClient(string ip = "203.0.113.10")
        {
            var client = Factory.CreateClient(new WebApplicationFactoryClientOptions
            {
                AllowAutoRedirect = false, BaseAddress = new Uri("https://localhost")
            });
            client.DefaultRequestHeaders.Add(TestConnectionFilter.AddressHeader, ip);
            clients.Add(client);
            return client;
        }

        public async Task<Session> SignInAsync(params string[] roles)
        {
            if (!scheduleInitialized)
            {
                await PickupScheduleTestData.PersistConfiguredAsync(Factory.Services);
                scheduleInitialized = true;
            }
            await using var scope = Factory.Services.CreateAsyncScope();
            var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            var email = IdentityTestContext.NewCampusEmail();
            var user = new ApplicationUser { DisplayName = "Synthetic rate-limit account", UserName = email, Email = email, EmailConfirmed = true };
            Assert.True((await users.CreateAsync(user)).Succeeded);
            Assert.True((await users.AddToRolesAsync(user, roles)).Succeeded);
            var principal = await scope.ServiceProvider.GetRequiredService<SignInManager<ApplicationUser>>().CreateUserPrincipalAsync(user);
            var cookie = scope.ServiceProvider.GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>().Get(IdentityConstants.ApplicationScheme);
            var now = cookie.TimeProvider!.GetUtcNow();
            var ticket = new AuthenticationTicket(principal, new AuthenticationProperties
            {
                IssuedUtc = now, ExpiresUtc = now.AddHours(1)
            }, IdentityConstants.ApplicationScheme);
            var client = NewClient();
            client.DefaultRequestHeaders.Add("Cookie", $"{cookie.Cookie.Name}={cookie.TicketDataFormat.Protect(ticket)}");
            return new(client, user);
        }

        public async Task<SavedReservation[]> ReservationsAsync()
        {
            await using var scope = Factory.Services.CreateAsyncScope();
            return await scope.ServiceProvider.GetRequiredService<ApplicationDbContext>().Reservations.AsNoTracking()
                .OrderBy(row => row.Id).Select(row => new SavedReservation(row.Id, row.UserId, row.ListingId, row.Status, row.ExpiresAtUtc)).ToArrayAsync();
        }

        public async ValueTask DisposeAsync()
        {
            foreach (var client in clients) client.Dispose();
            await Factory.DisposeAsync();
            await original.DisposeAsync();
        }
    }
}

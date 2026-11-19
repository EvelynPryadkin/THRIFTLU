using System.Data.Common;
using System.Globalization;
using System.Net;
using System.Security.Claims;
using System.Text.RegularExpressions;
using LUThrift.Web.Authorization;
using LUThrift.Web.Data;
using LUThrift.Web.InputModels;
using LUThrift.Web.Models;
using LUThrift.Web.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace LUThrift.Tests.Integration;

// All schedules, accounts, and times are synthetic and confined to SQLite test databases.
public sealed class PersistedPickupHoursTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-01-05T16:00:00Z", CultureInfo.InvariantCulture);

    [Theory]
    [InlineData("2026-01-05T15:00:00Z", PickupHoursStatus.Eligible)] // opening
    [InlineData("2026-01-05T17:00:00Z", PickupHoursStatus.Eligible)] // exactly one hour remains
    [InlineData("2026-01-05T17:00:01Z", PickupHoursStatus.InsufficientTime)]
    [InlineData("2026-01-05T18:30:00Z", PickupHoursStatus.Closed)] // break
    [InlineData("2026-01-05T19:00:00Z", PickupHoursStatus.Eligible)] // after break
    [InlineData("2026-01-05T23:00:00Z", PickupHoursStatus.Closed)] // closing
    [InlineData("2026-01-06T16:00:00Z", PickupHoursStatus.Closed)] // unscheduled day
    public async Task PersistedIntervals_EnforceContinuousFullHourAtServerTime(string instant, PickupHoursStatus expected)
    {
        await using var test = await Fixture.CreateAsync();
        await test.SaveAsync(OpenHours());
        test.Clock.Now = DateTimeOffset.Parse(instant, CultureInfo.InvariantCulture);
        await using var scope = test.Factory.Services.CreateAsyncScope();
        var result = await scope.ServiceProvider.GetRequiredService<PickupHoursEligibility>().EvaluateAsync();
        Assert.Equal(expected, result.Status);
        if (result.IsEligible)
        {
            Assert.Equal(test.Clock.Now, result.StartsAtUtc);
            Assert.Equal(test.Clock.Now.AddHours(1), result.ExpiresAtUtc);
        }
        var database = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        Assert.Empty(await database.Reservations.ToArrayAsync());
        Assert.Empty(await database.AuditEvents.ToArrayAsync());
        Assert.Single(await database.AdministrativeAuditEvents.ToArrayAsync());
    }

    [Theory]
    [InlineData("Asia/Tokyo", "Monday", "00:00", "04:00", "2026-01-04T15:30:00Z", PickupHoursStatus.Eligible)]
    [InlineData("America/Chicago", "Sunday", "00:00", "04:00", "2026-03-08T07:00:00Z", PickupHoursStatus.Eligible)]
    [InlineData("America/Chicago", "Sunday", "00:00", "02:30", "2026-03-08T07:00:00Z", PickupHoursStatus.ScheduleUnavailable)]
    [InlineData("America/Chicago", "Sunday", "01:30", "04:00", "2026-11-01T07:30:00Z", PickupHoursStatus.ScheduleUnavailable)]
    public async Task PersistedTimezone_ControlsCampusDayAndDaylightSavingBoundaries(
        string zone, string day, string opens, string closes, string instant, PickupHoursStatus expected)
    {
        await using var test = await Fixture.CreateAsync();
        await test.SaveAsync(new UpdatePickupScheduleInput
        {
            CampusTimeZone = zone, WeeklyPickupIntervals = [new() { Day = day, OpensAt = opens, ClosesAt = closes }]
        });
        test.Clock.Now = DateTimeOffset.Parse(instant, CultureInfo.InvariantCulture);
        await using var scope = test.Factory.Services.CreateAsyncScope();
        var result = await scope.ServiceProvider.GetRequiredService<PickupHoursEligibility>().EvaluateAsync();
        Assert.Equal(expected, result.Status);
        if (result.IsEligible) Assert.Equal(TimeSpan.FromHours(1), result.ExpiresAtUtc - result.StartsAtUtc);
    }

    [Fact]
    public async Task ClosedDateAndTimezoneChange_AffectNextRequestWithoutRestartOrReadWrites()
    {
        await using var test = await Fixture.CreateAsync();
        await test.SaveAsync(OpenHours());
        Assert.Contains("Reserve item", await test.Student.Client.GetStringAsync("/Thrift/Details/1"));
        var closedDate = OpenHours();
        closedDate.ClosedDates = ["2026-01-05"];
        await test.SaveAsync(closedDate);
        var closed = await test.Student.Client.GetStringAsync("/Thrift/Details/1");
        Assert.Contains("Pickup is currently closed", closed);
        Assert.DoesNotContain("action=\"/Account/Reserve\"", closed);

        var differentZone = OpenHours();
        differentZone.CampusTimeZone = "Pacific/Honolulu"; // same instant is before opening here
        await test.SaveAsync(differentZone);
        await using var scope = test.Factory.Services.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var beforeAuditCount = await database.AdministrativeAuditEvents.CountAsync();
        Assert.Equal(PickupHoursStatus.Closed,
            (await scope.ServiceProvider.GetRequiredService<PickupHoursEligibility>().EvaluateAsync()).Status);
        var formatted = await scope.ServiceProvider.GetRequiredService<CampusTimeFormatter>().FormatAsync(Now.UtcDateTime);
        Assert.Contains("2026-01-05 06:00:00", formatted);
        Assert.Contains("Pacific/Honolulu, UTC-10:00", formatted);
        Assert.Equal(beforeAuditCount, await database.AdministrativeAuditEvents.CountAsync());
        Assert.Empty(await database.Reservations.ToArrayAsync());
        Assert.Empty(await database.AuditEvents.ToArrayAsync());
    }

    [Fact]
    public async Task ScheduleClosedAfterViewing_ReservePostUsesCommittedScheduleAndDoesNotHoldItem()
    {
        await using var test = await Fixture.CreateAsync();
        await test.SaveAsync(OpenHours());
        var html = await test.Student.Client.GetStringAsync("/Thrift/Details/1");
        Assert.Contains("Reserve item", html);
        var token = Token(html);
        await test.SaveAsync(new UpdatePickupScheduleInput());

        using var response = await test.Student.Client.PostAsync("/Account/Reserve", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["ListingId"] = "1", ["__RequestVerificationToken"] = token
        }));

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/Account/Reservations", response.Headers.Location?.OriginalString);
        Assert.Contains("valid pickup hours have not been configured", await test.Student.Client.GetStringAsync(response.Headers.Location!));
        await using var scope = test.Factory.Services.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        Assert.Empty(await database.Reservations.ToArrayAsync());
        Assert.Empty(await database.AuditEvents.ToArrayAsync());
        Assert.Equal(ListingStatus.Available, (await database.Listings.SingleAsync(row => row.Id == 1)).Status);
        await test.SaveAsync(OpenHours());
        Assert.Contains("Reserve item", await test.Student.Client.GetStringAsync("/Thrift/Details/1"));
    }

    [Fact]
    public async Task ScheduleClosure_PreservesDeadlinesCancellationPickupAndExpiration()
    {
        await using var test = await Fixture.CreateAsync();
        await test.SaveAsync(OpenHours());
        var held = new List<ReservationCreationResult>();
        foreach (var listingId in new[] { 1, 2, 3 })
        {
            var result = await test.CreateReservationAsync(listingId);
            Assert.True(result.Succeeded);
            held.Add(result);
        }
        await test.SaveAsync(new UpdatePickupScheduleInput { CampusTimeZone = "Pacific/Honolulu" });
        await using var scope = test.Factory.Services.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var before = await database.Reservations.AsNoTracking().OrderBy(row => row.Id).ToArrayAsync();
        Assert.All(before, row =>
        {
            Assert.Equal(ReservationStatus.Active, row.Status);
            Assert.Equal(Now.AddHours(1).UtcDateTime, row.ExpiresAtUtc);
        });
        var service = scope.ServiceProvider.GetRequiredService<ReservationService>();
        Assert.Equal(ReservationOperationStatus.Canceled,
            (await service.CancelAsync(held[0].ReservationId!.Value, test.Student.Principal)).Status);
        Assert.Equal(ReservationOperationStatus.Fulfilled,
            (await service.ConfirmPickupAsync(held[1].ReservationId!.Value, test.Admin.Principal)).Status);
        test.Clock.Now = Now.AddHours(1);
        var batch = await service.ExpireBatchAsync(10);
        Assert.Equal(1, batch.Processed);
        Assert.Equal(0, batch.Failed);
        var after = await database.Reservations.AsNoTracking().OrderBy(row => row.Id).ToArrayAsync();
        Assert.Equal(before.Select(row => row.ExpiresAtUtc), after.Select(row => row.ExpiresAtUtc));
        Assert.Equal(new[] { ReservationStatus.Canceled, ReservationStatus.Fulfilled, ReservationStatus.Expired }, after.Select(row => row.Status));
        Assert.Equal(new[] { ListingStatus.Available, ListingStatus.Collected, ListingStatus.Available },
            await database.Listings.OrderBy(row => row.Id).Take(3).Select(row => row.Status).ToArrayAsync());
        Assert.Equal(6, await database.AuditEvents.CountAsync());
        var history = await test.Student.Client.GetStringAsync("/Account/Reservations");
        Assert.Contains("Active reservations: 0 of 3", history);
        Assert.Contains("Pacific/Honolulu", history);
    }

    [Fact]
    public async Task ScheduleChangeAfterCapture_UsesOneSnapshotForRunningOperationAndNextOperationReloads()
    {
        var change = new ChangeBeforeTransaction();
        await using var test = await Fixture.CreateAsync(change);
        await test.SaveAsync(OpenHours());
        change.BeforeNextTransaction = () => test.SaveAsync(new UpdatePickupScheduleInput());

        var first = await test.CreateReservationAsync(1);

        Assert.True(change.Triggered);
        Assert.True(first.Succeeded);
        Assert.Equal(Now.AddHours(1).UtcDateTime, first.ExpiresAtUtc);
        var next = await test.CreateReservationAsync(2);
        Assert.Equal(ReservationCreationStatus.PickupUnavailable, next.Status);
        await using var scope = test.Factory.Services.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        Assert.Single(await database.Reservations.ToArrayAsync());
        Assert.Single(await database.AuditEvents.ToArrayAsync());
        Assert.Empty((await scope.ServiceProvider.GetRequiredService<PickupScheduleService>().GetAsync()).WeeklyPickupIntervals);
    }

    private static UpdatePickupScheduleInput OpenHours() => new()
    {
        CampusTimeZone = "America/Chicago", WeeklyPickupIntervals =
        [new() { Day = "Monday", OpensAt = "09:00", ClosesAt = "12:00" }, new() { Day = "Monday", OpensAt = "13:00", ClosesAt = "17:00" }]
    };
    private static string Token(string html)
    {
        var matches = Regex.Matches(html, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"");
        Assert.NotEmpty(matches);
        return WebUtility.HtmlDecode(matches[0].Groups[1].Value);
    }
    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = PersistedPickupHoursTests.Now;
        public override DateTimeOffset GetUtcNow() => Now;
    }
    private sealed record Session(HttpClient Client, ClaimsPrincipal Principal);
    private sealed class ChangeBeforeTransaction : DbTransactionInterceptor
    {
        public Func<Task>? BeforeNextTransaction { get; set; }
        public bool Triggered { get; private set; }
        public override async ValueTask<InterceptionResult<DbTransaction>> TransactionStartingAsync(
            DbConnection connection, TransactionStartingEventData eventData, InterceptionResult<DbTransaction> result,
            CancellationToken cancellationToken = default)
        {
            if (BeforeNextTransaction is { } change)
            {
                BeforeNextTransaction = null;
                Triggered = true;
                await change();
            }
            return result;
        }
    }
    private sealed class Fixture(AuthorizationWebApplicationFactory original, WebApplicationFactory<ApplicationDbContext> factory,
        Session admin, Session student, Clock clock) : IAsyncDisposable
    {
        public WebApplicationFactory<ApplicationDbContext> Factory { get; } = factory;
        public Session Admin { get; } = admin;
        public Session Student { get; } = student;
        public Clock Clock { get; } = clock;
        public static async Task<Fixture> CreateAsync(ChangeBeforeTransaction? change = null)
        {
            var original = new AuthorizationWebApplicationFactory();
            var clock = new Clock();
            var factory = original.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
            {
                services.RemoveAll<TimeProvider>();
                services.AddSingleton<TimeProvider>(clock);
                if (change is not null) services.AddDbContext<ApplicationDbContext>(options => options.AddInterceptors(change));
            }));
            return new(original, factory, await SignInAsync(factory, ApplicationRoles.Administrator),
                await SignInAsync(factory, ApplicationRoles.Student), clock);
        }
        public async Task SaveAsync(UpdatePickupScheduleInput input)
        {
            await using var scope = Factory.Services.CreateAsyncScope();
            var schedules = scope.ServiceProvider.GetRequiredService<PickupScheduleService>();
            input.ExpectedVersion = (await schedules.GetAsync()).Version;
            var result = await schedules.SaveAsync(input, Admin.Principal);
            Assert.Equal(PickupScheduleOperationStatus.Saved, result.Status);
        }
        public async Task<ReservationCreationResult> CreateReservationAsync(int listingId)
        {
            await using var scope = Factory.Services.CreateAsyncScope();
            return await scope.ServiceProvider.GetRequiredService<ReservationService>()
                .CreateAsync(new() { ListingId = listingId }, Student.Principal);
        }
        private static async Task<Session> SignInAsync(WebApplicationFactory<ApplicationDbContext> factory, string role)
        {
            var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, BaseAddress = new Uri("https://localhost") });
            await using var scope = factory.Services.CreateAsyncScope();
            var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            var email = IdentityTestContext.NewEmail();
            var user = new ApplicationUser { Email = email, UserName = email, DisplayName = "Synthetic schedule test account" };
            Assert.True((await users.CreateAsync(user)).Succeeded);
            Assert.True((await users.AddToRoleAsync(user, role)).Succeeded);
            var principal = await scope.ServiceProvider.GetRequiredService<SignInManager<ApplicationUser>>().CreateUserPrincipalAsync(user);
            var options = scope.ServiceProvider.GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>().Get(IdentityConstants.ApplicationScheme);
            var now = options.TimeProvider!.GetUtcNow();
            var ticket = new AuthenticationTicket(principal, new AuthenticationProperties { IssuedUtc = now, ExpiresUtc = now.AddHours(2) }, IdentityConstants.ApplicationScheme);
            client.DefaultRequestHeaders.Add("Cookie", $"{options.Cookie.Name}={options.TicketDataFormat.Protect(ticket)}");
            return new(client, principal);
        }
        public async ValueTask DisposeAsync()
        {
            Admin.Client.Dispose();
            Student.Client.Dispose();
            await Factory.DisposeAsync();
            await original.DisposeAsync();
        }
    }
}

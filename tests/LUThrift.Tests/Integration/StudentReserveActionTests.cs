using System.Data.Common;
using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;
using LUThrift.Web.Authorization;
using LUThrift.Web.Configuration;
using LUThrift.Web.Data;
using LUThrift.Web.Models;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;

namespace LUThrift.Tests.Integration;

public class StudentReserveActionTests
{
    private const string ReservePath = "/Account/Reserve";
    private const string AccountPath = "/Account/Reservations";
    private const string DetailsPath = "/Thrift/Details/1";
    private static readonly DateTimeOffset InitialNow = new(2026, 10, 26, 15, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Reserve_UsesFormListingTrustedOwnerAndCommittedDeadlineDespiteForgedFields()
    {
        await using var test = await ReserveTest.CreateAsync();
        var form = ReserveForm(await test.Client.GetStringAsync(DetailsPath));
        Assert.Equal("post", Attribute(form, "method"));
        Assert.Equal(ReservePath, Attribute(form, "action"));
        Assert.Equal(new[] { "ListingId", "__RequestVerificationToken" },
            Inputs(form).Select(input => Attribute(input, "name")).OrderBy(name => name, StringComparer.Ordinal));
        Assert.Equal("1", Attribute(Assert.Single(Inputs(form), input => Attribute(input, "name") == "ListingId"), "value"));
        var fields = new Dictionary<string, string>
        {
            ["ListingId"] = "1", ["__RequestVerificationToken"] = Token(form),
            ["Id"] = "999999", ["UserId"] = "forged-owner", ["Role"] = ApplicationRoles.Administrator,
            ["Status"] = "Fulfilled", ["DurationMinutes"] = "999", ["MaximumActiveReservations"] = "99",
            ["CreatedAtUtc"] = "2000-01-01T00:00:00Z", ["ExpiresAtUtc"] = "2099-01-01T00:00:00Z",
            ["FulfilledAtUtc"] = "2099-01-01T00:00:00Z", ["ActorUserId"] = "forged-audit-actor"
        };
        // A displayed estimate must not become the saved deadline after the user waits.
        test.Clock.AdvanceTo(InitialNow.AddMinutes(7));

        using var response = await test.Client.PostAsync(ReservePath + "?ListingId=2", new FormUrlEncodedContent(fields));

        AssertPrg(response);
        var state = await test.SnapshotAsync();
        var reservation = Assert.Single(state.Reservations);
        Assert.Equal(1, reservation.ListingId);
        Assert.Equal(test.UserId, reservation.UserId);
        Assert.NotEqual(999999, reservation.Id);
        Assert.Equal(ReservationStatus.Active, reservation.Status);
        Assert.Equal(InitialNow.AddMinutes(7).UtcDateTime, reservation.CreatedAtUtc);
        Assert.Equal(InitialNow.AddHours(1).AddMinutes(7).UtcDateTime, reservation.ExpiresAtUtc);
        Assert.Null(reservation.FulfilledAtUtc);
        Assert.Null(reservation.CancellationReason);
        Assert.Equal(ListingStatus.Held, state.Listings.Single(row => row.Id == 1).Status);
        Assert.Equal(ListingStatus.Available, state.Listings.Single(row => row.Id == 2).Status);
        var audit = Assert.Single(state.Audits);
        Assert.Equal(1, audit.ListingId);
        Assert.Equal(test.UserId, audit.ActorUserId);
        Assert.Equal(InventoryAuditActions.Reserved, audit.Action);
        Assert.Equal(reservation.CreatedAtUtc, audit.OccurredAtUtc);
        var message = Message(await test.Client.GetStringAsync(AccountPath));
        Assert.Equal("Reservation confirmed. Collect your item by 2026-10-26 11:07:00 (America/Chicago, UTC-05:00).", message);
        var heldDetails = await test.Client.GetStringAsync(DetailsPath);
        Assert.Empty(ReserveForms(heldDetails));
        Assert.Contains("Reserved", MainContent(heldDetails));
    }

    [Theory]
    [InlineData(null)]
    [InlineData(ApplicationRoles.Staff)]
    [InlineData(ApplicationRoles.Administrator)]
    public async Task Reserve_RequiresStudentAndAnonymousSignInReturnsToDetails(string? role)
    {
        await using var test = await ReserveTest.CreateAsync(role);
        var detailsUrl = QueryHelpers.AddQueryString(DetailsPath, new Dictionary<string, string?>
        {
            ["Filter.Search"] = "coat & scarf", ["Filter.CategoryId"] = "2", ["pageNumber"] = "3"
        });
        var details = await test.Client.GetStringAsync(detailsUrl);
        Assert.Empty(ReserveForms(details));
        if (role is null)
        {
            var signIn = Assert.Single(Regex.Matches(MainContent(details), @"<a\b[^>]*>[\s\S]*?</a>", RegexOptions.IgnoreCase)
                .Cast<Match>(), match => Attribute(match.Value, "href").Contains("/Identity/Account/Login", StringComparison.Ordinal));
            var url = new Uri(test.Client.BaseAddress!, Attribute(signIn.Value, "href"));
            var returnUrl = QueryHelpers.ParseQuery(url.Query)["returnUrl"].ToString();
            var returnUri = new Uri(test.Client.BaseAddress!, returnUrl);
            Assert.Equal(DetailsPath, returnUri.AbsolutePath);
            var returnQuery = QueryHelpers.ParseQuery(returnUri.Query);
            Assert.Equal("coat & scarf", returnQuery["Filter.Search"].ToString());
            Assert.Equal("2", returnQuery["Filter.CategoryId"].ToString());
            Assert.Equal("3", returnQuery["pageNumber"].ToString());
            Assert.DoesNotContain("handler=", returnUrl, StringComparison.OrdinalIgnoreCase);
        }
        var token = role is null
            ? Token(await test.Client.GetStringAsync("/Identity/Account/Login")) : Token(details);

        using var response = await test.PostAsync(1, token);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.NotNull(response.Headers.Location);
        Assert.Equal(role is null ? "/Identity/Account/Login" : "/Identity/Account/AccessDenied",
            new Uri(test.Client.BaseAddress!, response.Headers.Location).AbsolutePath);
        await test.AssertNoReservationWritesAsync();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Reserve_RechecksDatabaseEligibilityInsteadOfTrustingExistingStudentCookie(bool revokeMembership)
    {
        await using var test = await ReserveTest.CreateAsync();
        var token = Token(ReserveForm(await test.Client.GetStringAsync(DetailsPath)));
        await test.RevokeEligibilityAsync(revokeMembership);
        Assert.Empty(ReserveForms(await test.Client.GetStringAsync(DetailsPath)));

        using var response = await test.PostAsync(1, token);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.NotNull(response.Headers.Location);
        Assert.Equal("/Identity/Account/AccessDenied", new Uri(test.Client.BaseAddress!, response.Headers.Location).AbsolutePath);
        await test.AssertNoReservationWritesAsync();
    }

    [Fact]
    public async Task Reserve_MissingAntiforgeryTokenIsRejectedWithoutWrites()
    {
        await using var test = await ReserveTest.CreateAsync();
        await test.Client.GetStringAsync(DetailsPath);

        using var response = await test.Client.PostAsync(ReservePath,
            new FormUrlEncodedContent(new Dictionary<string, string> { ["ListingId"] = "1" }));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await test.AssertNoReservationWritesAsync();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("not-an-id")]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("2147483648")]
    public async Task Reserve_InvalidOrMissingFormIdCannotFallBackToQueryString(string? submittedId)
    {
        await using var test = await ReserveTest.CreateAsync();
        var fields = new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = Token(ReserveForm(await test.Client.GetStringAsync(DetailsPath)))
        };
        if (submittedId is not null) fields["ListingId"] = submittedId;

        using var response = await test.Client.PostAsync(ReservePath + "?ListingId=1", new FormUrlEncodedContent(fields));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await test.AssertNoReservationWritesAsync();
    }

    [Theory]
    [InlineData(ListingStatus.Draft)]
    [InlineData(ListingStatus.Held)]
    [InlineData(ListingStatus.Missing)]
    [InlineData(ListingStatus.Collected)]
    [InlineData(ListingStatus.Archived)]
    public async Task Reserve_StaleDetailsFormCannotReserveANowUnavailableListing(ListingStatus status)
    {
        await using var test = await ReserveTest.CreateAsync();
        var token = Token(ReserveForm(await test.Client.GetStringAsync(DetailsPath)));
        await test.SetListingStatusAsync(1, status);

        using var response = await test.PostAsync(1, token);

        AssertPrg(response);
        Assert.Contains("not available", Message(await test.Client.GetStringAsync(AccountPath)));
        await test.AssertNoReservationWritesAsync(status);
    }

    [Fact]
    public async Task Reserve_UnknownListingReturnsSafeFeedbackWithoutWrites()
    {
        await using var test = await ReserveTest.CreateAsync();
        var token = Token(ReserveForm(await test.Client.GetStringAsync(DetailsPath)));

        using var response = await test.PostAsync(int.MaxValue, token);

        AssertPrg(response);
        Assert.Contains("could not be found", Message(await test.Client.GetStringAsync(AccountPath)));
        await test.AssertNoReservationWritesAsync();
    }

    [Theory]
    [InlineData("unconfigured")]
    [InlineData("closed")]
    [InlineData("insufficient")]
    public async Task Reserve_UnavailablePickupHoursHideTheFormAndRejectDirectPosts(string scenario)
    {
        await using var test = await ReserveTest.CreateAsync(configuredHours: scenario != "unconfigured");
        if (scenario == "closed") test.Clock.AdvanceTo(InitialNow.AddHours(7));
        if (scenario == "insufficient") test.Clock.AdvanceTo(InitialNow.AddHours(6).AddSeconds(1));
        var details = await test.Client.GetStringAsync(DetailsPath);
        Assert.Empty(ReserveForms(details));
        var expected = scenario switch
        {
            "unconfigured" => "valid pickup hours have not been configured",
            "closed" => "pickup hours are closed",
            _ => "a full hour must fit"
        };
        var detailsMessage = MainContent(details);
        Assert.Contains("pickup", detailsMessage, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(scenario switch
        {
            "unconfigured" => "configured",
            "closed" => "closed",
            _ => "full"
        }, detailsMessage, StringComparison.OrdinalIgnoreCase);

        using var response = await test.PostAsync(1, Token(details));

        AssertPrg(response);
        Assert.Contains(expected, Message(await test.Client.GetStringAsync(AccountPath)));
        await test.AssertNoReservationWritesAsync();
    }

    [Fact]
    public async Task Reserve_RechecksPickupTimeAfterTheDetailsFormWasRendered()
    {
        await using var test = await ReserveTest.CreateAsync();
        var token = Token(ReserveForm(await test.Client.GetStringAsync(DetailsPath)));
        test.Clock.AdvanceTo(InitialNow.AddHours(6).AddSeconds(1));

        using var response = await test.PostAsync(1, token);

        AssertPrg(response);
        Assert.Contains("a full hour must fit", Message(await test.Client.GetStringAsync(AccountPath)));
        await test.AssertNoReservationWritesAsync();
    }

    [Fact]
    public async Task Reserve_ThreeActiveReservationsPreventAFourth()
    {
        await using var test = await ReserveTest.CreateAsync();
        foreach (var listingId in new[] { 2, 3, 4 })
            await test.SeedReservationAsync(listingId, InitialNow.AddMinutes(40));
        var token = Token(ReserveForm(await test.Client.GetStringAsync(DetailsPath)));

        using var response = await test.PostAsync(1, token);

        AssertPrg(response);
        Assert.Contains("three active reservations", Message(await test.Client.GetStringAsync(AccountPath)));
        var state = await test.SnapshotAsync();
        Assert.Equal(3, state.Reservations.Length);
        Assert.DoesNotContain(state.Reservations, row => row.ListingId == 1);
        Assert.Equal(ListingStatus.Available, state.Listings.Single(row => row.Id == 1).Status);
        Assert.Empty(state.Audits);
    }

    [Fact]
    public async Task Reserve_DuplicateSubmissionKeepsTheOriginalDeadlineAndSingleAudit()
    {
        await using var test = await ReserveTest.CreateAsync();
        var token = Token(ReserveForm(await test.Client.GetStringAsync(DetailsPath)));
        using var first = await test.PostAsync(1, token);
        AssertPrg(first);
        var original = Assert.Single((await test.SnapshotAsync()).Reservations);
        await test.Client.GetStringAsync(AccountPath);
        test.Clock.AdvanceTo(InitialNow.AddMinutes(10));

        using var duplicate = await test.PostAsync(1, token);

        AssertPrg(duplicate);
        Assert.Contains("already have a reservation", Message(await test.Client.GetStringAsync(AccountPath)));
        var state = await test.SnapshotAsync();
        var reservation = Assert.Single(state.Reservations);
        Assert.Equal(original.Id, reservation.Id);
        Assert.Equal(original.CreatedAtUtc, reservation.CreatedAtUtc);
        Assert.Equal(original.ExpiresAtUtc, reservation.ExpiresAtUtc);
        Assert.Equal(ListingStatus.Held, state.Listings.Single(row => row.Id == 1).Status);
        Assert.Single(state.Audits);
    }

    [Fact]
    public async Task Reserve_GetRoutesAndCraftedPublicGetHandlerNeverWriteOrExpireData()
    {
        await using var test = await ReserveTest.CreateAsync();
        await test.SeedReservationAsync(1, InitialNow);
        var before = await test.SnapshotAsync();

        using var reserveGet = await test.Client.GetAsync(ReservePath + "?ListingId=2");
        Assert.Equal(HttpStatusCode.Redirect, reserveGet.StatusCode);
        Assert.NotNull(reserveGet.Headers.Location);
        Assert.Contains(reserveGet.Headers.Location.OriginalString, new[] { "/Thrift", "/Thrift/Index" });
        using var publicGet = await test.Client.GetAsync(DetailsPath + "?handler=Reserve&ListingId=2");
        Assert.Equal(HttpStatusCode.OK, publicGet.StatusCode);
        using var accountGet = await test.Client.GetAsync(AccountPath);
        Assert.Equal(HttpStatusCode.OK, accountGet.StatusCode);

        var after = await test.SnapshotAsync();
        Assert.Equal(before.Listings.Select(row => (row.Id, row.Status)), after.Listings.Select(row => (row.Id, row.Status)));
        Assert.Equal(before.Reservations.Select(ReservationSnapshot), after.Reservations.Select(ReservationSnapshot));
        Assert.Empty(after.Audits);
        Assert.Equal(ReservationStatus.Active, Assert.Single(after.Reservations).Status);
    }

    [Fact]
    public async Task Reserve_DatabaseConflictUsesSafeFeedbackAndDoesNotLeaveAHold()
    {
        var fault = new ConflictOnHoldInterceptor();
        await using var test = await ReserveTest.CreateAsync(fault: fault);
        var token = Token(ReserveForm(await test.Client.GetStringAsync(DetailsPath)));
        fault.Armed = true;

        using var response = await test.PostAsync(1, token);

        AssertPrg(response);
        Assert.True(fault.Triggered);
        var message = Message(await test.Client.GetStringAsync(AccountPath));
        Assert.Contains("changed", message);
        Assert.Contains("Refresh", message);
        Assert.DoesNotContain("Synthetic", message);
        Assert.DoesNotContain("40001", message);
        Assert.DoesNotContain("Postgres", message, StringComparison.OrdinalIgnoreCase);
        await test.AssertNoReservationWritesAsync();
    }

    private static object ReservationSnapshot(Reservation row)
        => (row.Id, row.UserId, row.ListingId, row.Status, row.CreatedAtUtc, row.ExpiresAtUtc, row.FulfilledAtUtc, row.CancellationReason);

    private static void AssertPrg(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal(AccountPath, response.Headers.Location?.OriginalString);
    }

    private static string MainContent(string html)
        => Regex.Match(html, @"<main\b[^>]*>([\s\S]*?)</main>", RegexOptions.IgnoreCase).Groups[1].Value;

    private static string[] ReserveForms(string html)
        => Regex.Matches(MainContent(html), @"<form\b[^>]*>[\s\S]*?</form>", RegexOptions.IgnoreCase).Cast<Match>()
            .Select(match => match.Value).Where(form => Attribute(form, "action") == ReservePath).ToArray();

    private static string ReserveForm(string html) => Assert.Single(ReserveForms(html));

    private static IEnumerable<string> Inputs(string html)
        => Regex.Matches(html, @"<input\b[^>]*>", RegexOptions.IgnoreCase).Cast<Match>().Select(match => match.Value);

    private static string Attribute(string html, string name)
        => WebUtility.HtmlDecode(Regex.Match(html, "\\b" + name + "=\"([^\"]*)\"", RegexOptions.IgnoreCase).Groups[1].Value);

    private static string Token(string html)
    {
        var input = Assert.Single(Inputs(html).Where(input => Attribute(input, "name") == "__RequestVerificationToken").Distinct());
        var token = Attribute(input, "value");
        Assert.NotEmpty(token);
        return token;
    }

    private static string Message(string html)
    {
        var alert = Regex.Match(MainContent(html), "<div\\b[^>]*role=\"status\"[^>]*>([\\s\\S]*?)</div>", RegexOptions.IgnoreCase);
        Assert.True(alert.Success);
        return WebUtility.HtmlDecode(Regex.Replace(alert.Groups[1].Value, "<[^>]+>", string.Empty)).Trim();
    }

    private sealed class MutableClock : TimeProvider
    {
        private long ticks = InitialNow.UtcTicks;
        public void AdvanceTo(DateTimeOffset instant) => Interlocked.Exchange(ref ticks, instant.UtcTicks);
        public override DateTimeOffset GetUtcNow() => new(Interlocked.Read(ref ticks), TimeSpan.Zero);
    }

    private sealed class ConflictOnHoldInterceptor : DbCommandInterceptor
    {
        public bool Armed { get; set; }
        public bool Triggered { get; private set; }

        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            var setsHeld = command.CommandText.Contains("'Held'", StringComparison.Ordinal)
                || command.Parameters.Cast<DbParameter>().Any(parameter => Equals(parameter.Value, "Held"));
            if (Armed && command.CommandText.StartsWith("UPDATE \"Listings\"", StringComparison.Ordinal) && setsHeld)
            {
                Armed = false;
                Triggered = true;
                throw new PostgresException("Synthetic reservation conflict", "ERROR", "ERROR", PostgresErrorCodes.SerializationFailure);
            }
            return ValueTask.FromResult(result);
        }
    }

    private sealed record DatabaseState(Listing[] Listings, Reservation[] Reservations, AuditEvent[] Audits);

    private sealed class ReserveTest(
        AuthorizationWebApplicationFactory original, WebApplicationFactory<ApplicationDbContext> factory,
        HttpClient client, string userId, MutableClock clock) : IAsyncDisposable
    {
        public HttpClient Client { get; } = client;
        public string UserId { get; } = userId;
        public MutableClock Clock { get; } = clock;

        public static async Task<ReserveTest> CreateAsync(string? role = ApplicationRoles.Student,
            bool configuredHours = true, ConflictOnHoldInterceptor? fault = null)
        {
            var original = new AuthorizationWebApplicationFactory();
            var clock = new MutableClock();
            var factory = original.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
            {
                services.RemoveAll<TimeProvider>();
                services.AddSingleton<TimeProvider>(clock);
                // These are test fixtures, not official thrift-room opening hours.
                services.PostConfigure<ReservationOptions>(options =>
                {
                    options.CampusTimeZone = "America/Chicago";
                    options.WeeklyPickupIntervals = configuredHours
                        ? [new() { Day = "Monday", OpensAt = "09:00", ClosesAt = "17:00" }] : [];
                    options.ClosedDates = [];
                });
                if (fault is not null)
                    services.AddDbContext<ApplicationDbContext>(options => options.AddInterceptors(fault));
            }));
            var client = factory.CreateClient(new WebApplicationFactoryClientOptions
            {
                AllowAutoRedirect = false, BaseAddress = new Uri("https://localhost")
            });
            var userId = string.Empty;
            if (role is not null)
            {
                await using var scope = factory.Services.CreateAsyncScope();
                var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
                var email = IdentityTestContext.NewEmail();
                var password = IdentityTestContext.NewPassword();
                var user = new ApplicationUser { UserName = email, Email = email, DisplayName = "Student reserve HTTP test" };
                Assert.True((await users.CreateAsync(user, password)).Succeeded);
                Assert.True((await users.AddToRoleAsync(user, role)).Succeeded);
                userId = user.Id;
                var token = Token(await client.GetStringAsync("/Identity/Account/Login"));
                using var login = await client.PostAsync("/Identity/Account/Login", new FormUrlEncodedContent(
                    new Dictionary<string, string>
                    {
                        ["Input.Email"] = email, ["Input.Password"] = password, ["__RequestVerificationToken"] = token
                    }));
                Assert.Equal(HttpStatusCode.Redirect, login.StatusCode);
            }
            return new(original, factory, client, userId, clock);
        }

        public Task<HttpResponseMessage> PostAsync(int listingId, string token)
            => Client.PostAsync(ReservePath, new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["ListingId"] = listingId.ToString(CultureInfo.InvariantCulture), ["__RequestVerificationToken"] = token
            }));

        public async Task RevokeEligibilityAsync(bool membership)
        {
            await using var scope = factory.Services.CreateAsyncScope();
            var database = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            if (membership)
                await database.UserRoles.Where(row => row.UserId == UserId).ExecuteDeleteAsync();
            else
                await database.Users.Where(row => row.Id == UserId)
                    .ExecuteUpdateAsync(setters => setters.SetProperty(row => row.IsActive, false));
        }

        public async Task SetListingStatusAsync(int id, ListingStatus status)
        {
            await using var scope = factory.Services.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<ApplicationDbContext>().Listings.Where(row => row.Id == id)
                .ExecuteUpdateAsync(setters => setters.SetProperty(row => row.Status, status));
        }

        public async Task SeedReservationAsync(int listingId, DateTimeOffset deadline)
        {
            await using var scope = factory.Services.CreateAsyncScope();
            var database = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            await database.Listings.Where(row => row.Id == listingId)
                .ExecuteUpdateAsync(setters => setters.SetProperty(row => row.Status, ListingStatus.Held));
            database.Reservations.Add(new Reservation
            {
                ListingId = listingId, UserId = UserId, Status = ReservationStatus.Active,
                CreatedAtUtc = deadline.AddHours(-1).UtcDateTime, ExpiresAtUtc = deadline.UtcDateTime
            });
            await database.SaveChangesAsync();
        }

        public async Task<DatabaseState> SnapshotAsync()
        {
            await using var scope = factory.Services.CreateAsyncScope();
            var database = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            return new(await database.Listings.AsNoTracking().OrderBy(row => row.Id).ToArrayAsync(),
                await database.Reservations.AsNoTracking().OrderBy(row => row.Id).ToArrayAsync(),
                await database.AuditEvents.AsNoTracking().OrderBy(row => row.Id).ToArrayAsync());
        }

        public async Task AssertNoReservationWritesAsync(ListingStatus expectedStatus = ListingStatus.Available)
        {
            var state = await SnapshotAsync();
            Assert.Empty(state.Reservations);
            Assert.Empty(state.Audits);
            Assert.Equal(expectedStatus, state.Listings.Single(row => row.Id == 1).Status);
        }

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await factory.DisposeAsync();
            await original.DisposeAsync();
        }
    }
}

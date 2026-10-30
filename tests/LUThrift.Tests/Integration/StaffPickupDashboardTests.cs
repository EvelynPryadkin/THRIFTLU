using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using LUThrift.Web.Authorization;
using LUThrift.Web.Configuration;
using LUThrift.Web.Data;
using LUThrift.Web.Models;
using LUThrift.Web.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace LUThrift.Tests.Integration;

public sealed class StaffPickupDashboardTests
{
    private const string PagePath = "/Staff/Pickups";
    private static readonly DateTimeOffset Now = new(2026, 10, 29, 15, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(null, "/Identity/Account/Login")]
    [InlineData(ApplicationRoles.Student, "/Identity/Account/AccessDenied")]
    [InlineData(ApplicationRoles.Staff, null)]
    [InlineData(ApplicationRoles.Administrator, null)]
    public async Task Dashboard_EnforcesFolderPolicyAndStaffNavigation(string? role, string? redirect)
    {
        await using var test = await DashboardTest.CreateAsync(role);
        var reservation = await test.SeedAsync("Private pickup title");
        var before = await test.SnapshotAsync();

        using var response = await test.Client.GetAsync(PagePath);
        var html = await response.Content.ReadAsStringAsync();
        if (redirect is not null)
        {
            AssertRedirect(response, redirect);
            Assert.DoesNotContain(reservation.Listing.Title, html);
            Assert.DoesNotContain(test.Owner.Email!, html);
        }
        else
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Contains(reservation.Listing.Title, html);
            Assert.Contains(test.Owner.Email!, html);
            using var landing = await test.Client.GetAsync("/Staff");
            Assert.Equal(HttpStatusCode.OK, landing.StatusCode);
            Assert.Contains(Tags(await landing.Content.ReadAsStringAsync(), "a"),
                tag => Attribute(tag, "href") == PagePath);
        }
        Assert.Equal(before, await test.SnapshotAsync());
    }

    [Theory]
    [InlineData(ApplicationRoles.Staff, false)]
    [InlineData(ApplicationRoles.Staff, true)]
    [InlineData(ApplicationRoles.Administrator, false)]
    [InlineData(ApplicationRoles.Administrator, true)]
    public async Task StaleCookie_RevokedOrInactiveAccountCannotSeeStudentDataEvenWithInvalidSearch(string role, bool inactive)
    {
        await using var test = await DashboardTest.CreateAsync(role);
        var reservation = await test.SeedAsync("Private stale-cookie pickup");
        await using (var scope = test.Factory.Services.CreateAsyncScope())
        {
            var database = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            if (inactive)
                await database.Users.Where(row => row.Id == test.ActorId).ExecuteUpdateAsync(setters => setters.SetProperty(row => row.IsActive, false));
            else
                await database.UserRoles.Where(row => row.UserId == test.ActorId).ExecuteDeleteAsync();
        }
        var before = await test.SnapshotAsync();

        foreach (var query in new[] { string.Empty, "?Filter.ReservationId=not-an-id" })
        {
            using var response = await test.Client.GetAsync(PagePath + query);
            AssertRedirect(response, "/Identity/Account/AccessDenied");
            var html = await response.Content.ReadAsStringAsync();
            Assert.DoesNotContain(test.Owner.Email!, html);
            Assert.DoesNotContain(reservation.Listing.Title, html);
        }
        Assert.Equal(before, await test.SnapshotAsync());
    }

    [Fact]
    public async Task Dashboard_GetRendersEncodedFieldsCampusDeadlineAndAccessibleControlsWithoutWrites()
    {
        await using var test = await DashboardTest.CreateAsync(ApplicationRoles.Staff);
        var reservation = await test.SeedAsync("<script>alert('item')</script> " + new string('x', 100));
        var before = await test.SnapshotAsync();

        using var response = await test.Client.GetAsync(PagePath);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore);
        var html = await response.Content.ReadAsStringAsync();
        var card = Assert.Single(Cards(html));
        Assert.Contains(reservation.Listing.Title, WebUtility.HtmlDecode(card));
        Assert.DoesNotContain(reservation.Listing.Title, card);
        Assert.Contains(test.Owner.DisplayName, WebUtility.HtmlDecode(card));
        Assert.DoesNotContain(test.Owner.DisplayName, card);
        Assert.Contains(test.Owner.Email!, card);
        Assert.Contains("Reservation ID", card);
        Assert.Contains("Reservation status", card);
        Assert.Contains("Listing status", card);
        Assert.Contains("Active", card);
        Assert.Contains("Held", card);
        Assert.DoesNotContain(test.Owner.Id, html);
        Assert.DoesNotContain("private audit details", html);
        Assert.DoesNotContain("private cancellation reason", html);
        var time = Regex.Match(card, @"<time\b[^>]*>(?<text>[\s\S]*?)</time>").Value;
        Assert.Equal(reservation.ExpiresAtUtc, DateTimeOffset.Parse(Attribute(time, "datetime"), CultureInfo.InvariantCulture).UtcDateTime);
        var formatter = test.Factory.Services.GetRequiredService<CampusTimeFormatter>();
        Assert.Contains(formatter.Format(reservation.ExpiresAtUtc), WebUtility.HtmlDecode(time));
        Assert.Contains("America/Chicago, UTC-05:00", WebUtility.HtmlDecode(time));
        Assert.Contains("text-break", card);
        var confirmation = Assert.Single(Tags(card, "form"));
        Assert.Equal("post", Attribute(confirmation, "method"));
        Assert.Contains("handler=ConfirmPickup", Attribute(confirmation, "action"));
        Assert.Equal("submit", Attribute(Assert.Single(Tags(card, "button")), "type"));

        var form = SearchForm(html);
        Assert.Equal("get", Attribute(form, "method"));
        Assert.Equal(PagePath, Attribute(form, "action"));
        foreach (var field in new[] { "Filter.ReservationId", "Filter.StudentEmail" })
        {
            var input = Assert.Single(Tags(form, "input"), tag => Attribute(tag, "name") == field);
            Assert.Contains(Tags(form, "label"), label => Attribute(label, "for") == Attribute(input, "id"));
            Assert.NotEmpty(Attribute(input, "aria-describedby"));
            Assert.NotEqual("-1", Attribute(input, "tabindex"));
        }
        Assert.Contains("aria-label=\"Pickup pagination\"", html);
        Assert.Contains("aria-current=\"page\"", html);
        Assert.DoesNotContain(Tags(form, "input"), input => Attribute(input, "name") == "pageNumber");
        Assert.Equal(before, await test.SnapshotAsync());
    }

    [Theory]
    [InlineData(ReservationStatus.Active, 0, ListingStatus.Held, "Expired")]
    [InlineData(ReservationStatus.Expired, -1, ListingStatus.Available, "Expired")]
    [InlineData(ReservationStatus.Fulfilled, -1, ListingStatus.Collected, "Completed")]
    [InlineData(ReservationStatus.Canceled, -1, ListingStatus.Archived, "Canceled")]
    public async Task ExactLookup_ExplainsHistoryWithoutReleasingOrExposingPrivateFields(
        ReservationStatus status, int remainingMinutes, ListingStatus listingStatus, string explanation)
    {
        await using var test = await DashboardTest.CreateAsync(ApplicationRoles.Staff);
        var reservation = await test.SeedAsync("Historical pickup", status, remainingMinutes, listingStatus);
        var before = await test.SnapshotAsync();

        using var pending = await test.Client.GetAsync(PagePath);
        Assert.Empty(Cards(await pending.Content.ReadAsStringAsync()));
        using var response = await test.Client.GetAsync(Search(reservation.Id, test.Owner.Email));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var html = await response.Content.ReadAsStringAsync();
        var card = Assert.Single(Cards(html));
        Assert.Contains(explanation, card);
        Assert.Contains(status == ReservationStatus.Canceled
            ? "no longer eligible for pickup." : "not eligible for pickup.", card);
        Assert.Contains(listingStatus.ToString(), card);
        Assert.Equal(reservation.Id.ToString(CultureInfo.InvariantCulture), InputValue(html, "Filter.ReservationId"));
        Assert.Equal(test.Owner.Email, InputValue(html, "Filter.StudentEmail"));
        Assert.DoesNotContain("private cancellation reason", html);
        Assert.Equal(before, await test.SnapshotAsync());
    }

    [Fact]
    public async Task SearchAndPagination_PreserveNormalizedSelectionsAndStableOrderWithoutWrites()
    {
        await using var test = await DashboardTest.CreateAsync(ApplicationRoles.Administrator);
        var reservations = new List<Reservation>();
        for (var index = 24; index >= 0; index--)
            reservations.Add(await test.SeedAsync($"Pending pickup {index}", remainingMinutes: 5 + index / 3));
        await test.SeedAsync("Expired hidden from pending", remainingMinutes: 0);
        await test.SeedAsync("Canceled hidden from pending", ReservationStatus.Canceled);
        var before = await test.SnapshotAsync();
        const string search = "%_!MATCH@EXAMPLE.INVALID";

        using var first = await test.Client.GetAsync(Search(email: "  " + search + "  "));
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        var firstHtml = await first.Content.ReadAsStringAsync();
        Assert.Equal(20, Cards(firstHtml).Length);
        Assert.Contains("25 reservations found. Page 1 of 2.", firstHtml);
        Assert.Equal(search, InputValue(firstHtml, "Filter.StudentEmail"));
        var next = Assert.Single(Tags(firstHtml, "a"), tag => Attribute(tag, "rel") == "next");
        var nextUri = new Uri(test.Client.BaseAddress!, Attribute(next, "href"));
        var nextQuery = QueryHelpers.ParseQuery(nextUri.Query);
        Assert.Equal(search, nextQuery["Filter.StudentEmail"].ToString());
        Assert.Equal("2", nextQuery["pageNumber"].ToString());

        using var second = await test.Client.GetAsync(nextUri);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        var secondHtml = await second.Content.ReadAsStringAsync();
        Assert.Equal(5, Cards(secondHtml).Length);
        Assert.Contains("25 reservations found. Page 2 of 2.", secondHtml);
        Assert.Equal(search, InputValue(secondHtml, "Filter.StudentEmail"));
        var previous = Assert.Single(Tags(secondHtml, "a"), tag => Attribute(tag, "rel") == "prev");
        var previousQuery = QueryHelpers.ParseQuery(new Uri(test.Client.BaseAddress!, Attribute(previous, "href")).Query);
        Assert.Equal(search, previousQuery["Filter.StudentEmail"].ToString());
        Assert.Equal("1", previousQuery["pageNumber"].ToString());
        Assert.Equal(PagePath, Attribute(SearchForm(secondHtml), "action"));
        Assert.DoesNotContain(Tags(SearchForm(secondHtml), "input"), input => Attribute(input, "name") == "pageNumber");
        Assert.DoesNotContain(Tags(secondHtml, "a"), tag => Attribute(tag, "rel") == "next");
        Assert.Equal(reservations.OrderBy(row => row.ExpiresAtUtc).ThenBy(row => row.Id).Select(row => row.Id),
            CardIds(firstHtml).Concat(CardIds(secondHtml)));
        Assert.Equal(25, CardIds(firstHtml).Concat(CardIds(secondHtml)).Distinct().Count());
        Assert.Equal(before, await test.SnapshotAsync());
    }

    [Fact]
    public async Task EmptyAndUnmatchedSearchesHaveDifferentMessagesAndCombinedFiltersMustBothMatch()
    {
        await using var test = await DashboardTest.CreateAsync(ApplicationRoles.Staff);
        using (var empty = await test.Client.GetAsync(PagePath))
        {
            Assert.Equal(HttpStatusCode.OK, empty.StatusCode);
            Assert.Contains("No pending pickups right now.", await empty.Content.ReadAsStringAsync());
        }
        var reservation = await test.SeedAsync("Search match");
        var before = await test.SnapshotAsync();
        foreach (var query in new[] { Search(int.MaxValue), Search(reservation.Id, "different@example.invalid"), Search(email: "no-match") })
        {
            using var response = await test.Client.GetAsync(query);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var html = await response.Content.ReadAsStringAsync();
            Assert.Contains("No reservations match this search.", html);
            Assert.Empty(Cards(html));
        }
        using var blank = await test.Client.GetAsync(Search(email: "   "));
        Assert.Equal(HttpStatusCode.OK, blank.StatusCode);
        Assert.Single(Cards(await blank.Content.ReadAsStringAsync()));
        Assert.Equal(before, await test.SnapshotAsync());
    }

    [Theory]
    [InlineData("Filter.ReservationId", "not-an-id")]
    [InlineData("Filter.ReservationId", "2147483648")]
    [InlineData("Filter.ReservationId", "0")]
    [InlineData("Filter.ReservationId", "-1")]
    [InlineData("Filter.StudentEmail", "long")]
    [InlineData("Filter.StudentEmail", "before\0after")]
    public async Task InvalidSearch_ReturnsValidationWithoutBroadeningToAllStudents(string field, string value)
    {
        await using var test = await DashboardTest.CreateAsync(ApplicationRoles.Staff);
        var reservation = await test.SeedAsync("Must not appear for malformed searches");
        var before = await test.SnapshotAsync();
        if (value == "long") value = new string('a', 257);

        using var response = await test.Client.GetAsync(QueryHelpers.AddQueryString(PagePath, field, value));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var html = await response.Content.ReadAsStringAsync();
        Assert.Contains("Check the search fields and try again.", html);
        Assert.Contains("role=\"alert\"", html);
        Assert.Empty(Cards(html));
        Assert.DoesNotContain(reservation.Listing.Title, html);
        Assert.DoesNotContain(test.Owner.Email!, html);
        Assert.Equal(before, await test.SnapshotAsync());
    }

    [Theory]
    [InlineData("not-a-page")]
    [InlineData("999999999999999999999999")]
    [InlineData("-5")]
    [InlineData("2147483647")]
    public async Task PageBoundariesAreSafeAndDoNotLoseTheSearch(string page)
    {
        await using var test = await DashboardTest.CreateAsync(ApplicationRoles.Staff);
        await test.SeedAsync("Only page");
        using var response = await test.Client.GetAsync(QueryHelpers.AddQueryString(Search(email: test.Owner.Email), "pageNumber", page));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var html = await response.Content.ReadAsStringAsync();
        Assert.Contains("Page 1 of 1.", html);
        Assert.Equal(test.Owner.Email, InputValue(html, "Filter.StudentEmail"));
        Assert.Single(Cards(html));
    }

    [Fact]
    public async Task MissingEmailAndInconsistentListingRemainUnderstandable()
    {
        await using var test = await DashboardTest.CreateAsync(ApplicationRoles.Staff);
        var reservation = await test.SeedAsync("Missing inventory", listingStatus: ListingStatus.Missing);
        await using (var scope = test.Factory.Services.CreateAsyncScope())
        {
            var database = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            await database.Users.Where(row => row.Id == test.Owner.Id).ExecuteUpdateAsync(setters => setters
                .SetProperty(row => row.Email, (string?)null).SetProperty(row => row.NormalizedEmail, (string?)null));
        }
        var before = await test.SnapshotAsync();
        using var response = await test.Client.GetAsync(Search(reservation.Id));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var card = Assert.Single(Cards(await response.Content.ReadAsStringAsync()));
        Assert.Contains("Not available", card);
        Assert.Contains("The item is not held for pickup.", card);
        Assert.Contains("Missing", card);
        Assert.Equal(before, await test.SnapshotAsync());
    }

    private static string Search(int? id = null, string? email = null)
        => QueryHelpers.AddQueryString(PagePath, new Dictionary<string, string?>
        {
            ["Filter.ReservationId"] = id?.ToString(CultureInfo.InvariantCulture), ["Filter.StudentEmail"] = email
        });

    private static void AssertRedirect(HttpResponseMessage response, string path)
    {
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.NotNull(response.Headers.Location);
        Assert.Equal(path, new Uri(new Uri("https://localhost"), response.Headers.Location).AbsolutePath);
    }

    private static string[] Tags(string html, string tag)
        => Regex.Matches(html, $@"<{tag}\b[^>]*>", RegexOptions.IgnoreCase).Cast<Match>().Select(match => match.Value).ToArray();

    private static string Attribute(string tag, string name)
        => WebUtility.HtmlDecode(Regex.Match(tag, "\\b" + name + "=\"([^\"]*)\"", RegexOptions.IgnoreCase).Groups[1].Value);

    private static string[] Cards(string html)
        => Regex.Matches(html, @"<article\b[^>]*>[\s\S]*?</article>", RegexOptions.IgnoreCase).Cast<Match>().Select(match => match.Value).ToArray();

    private static IEnumerable<int> CardIds(string html)
        => Cards(html).Select(card => int.Parse(Attribute(card, "aria-labelledby")["pickup-title-".Length..], CultureInfo.InvariantCulture));

    private static string SearchForm(string html)
        => Assert.Single(Regex.Matches(html, @"<form\b[^>]*>[\s\S]*?</form>", RegexOptions.IgnoreCase).Cast<Match>(),
            form => Attribute(form.Value, "method") == "get").Value;

    private static string InputValue(string html, string name)
        => Attribute(Assert.Single(Tags(SearchForm(html), "input"), input => Attribute(input, "name") == name), "value");

    private sealed class FixedClock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class DashboardTest(
        AuthorizationWebApplicationFactory original, WebApplicationFactory<ApplicationDbContext> factory,
        HttpClient client, string? actorId, ApplicationUser owner) : IAsyncDisposable
    {
        public WebApplicationFactory<ApplicationDbContext> Factory { get; } = factory;
        public HttpClient Client { get; } = client;
        public string? ActorId { get; } = actorId;
        public ApplicationUser Owner { get; } = owner;

        public static async Task<DashboardTest> CreateAsync(string? role)
        {
            var original = new AuthorizationWebApplicationFactory();
            var factory = original.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
            {
                services.RemoveAll<TimeProvider>();
                services.AddSingleton<TimeProvider>(new FixedClock());
                services.PostConfigure<ReservationOptions>(options => options.CampusTimeZone = "America/Chicago");
            }));
            var client = factory.CreateClient(new WebApplicationFactoryClientOptions
            {
                AllowAutoRedirect = false, BaseAddress = new Uri("https://localhost")
            });
            await using var scope = factory.Services.CreateAsyncScope();
            var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            var owner = new ApplicationUser
            {
                UserName = IdentityTestContext.NewEmail(), Email = "pickup%_!match@example.invalid",
                DisplayName = "<b>Pickup student</b>"
            };
            Assert.True((await users.CreateAsync(owner)).Succeeded);
            Assert.True((await users.AddToRoleAsync(owner, ApplicationRoles.Student)).Succeeded);
            string? actorId = null;
            if (role is not null)
            {
                var email = IdentityTestContext.NewEmail();
                var actor = new ApplicationUser { UserName = email, Email = email, DisplayName = "Dashboard test viewer" };
                Assert.True((await users.CreateAsync(actor)).Succeeded);
                Assert.True((await users.AddToRoleAsync(actor, role)).Succeeded);
                actorId = actor.Id;
                var signIns = scope.ServiceProvider.GetRequiredService<SignInManager<ApplicationUser>>();
                var principal = await signIns.CreateUserPrincipalAsync(actor);
                var cookie = scope.ServiceProvider.GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>()
                    .Get(IdentityConstants.ApplicationScheme);
                // Authentication and the reservation query share the controlled test clock.
                var issuedAt = cookie.TimeProvider!.GetUtcNow();
                var ticket = new AuthenticationTicket(principal, new AuthenticationProperties
                {
                    IssuedUtc = issuedAt, ExpiresUtc = issuedAt.AddMinutes(30)
                }, IdentityConstants.ApplicationScheme);
                client.DefaultRequestHeaders.Add("Cookie", $"{cookie.Cookie.Name}={cookie.TicketDataFormat.Protect(ticket)}");
            }
            return new(original, factory, client, actorId, owner);
        }

        public async Task<Reservation> SeedAsync(string title, ReservationStatus status = ReservationStatus.Active,
            int remainingMinutes = 30, ListingStatus listingStatus = ListingStatus.Held)
        {
            await using var scope = Factory.Services.CreateAsyncScope();
            var database = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var reservation = new Reservation
            {
                UserId = Owner.Id, Status = status, CreatedAtUtc = Now.AddMinutes(-30).UtcDateTime,
                ExpiresAtUtc = Now.AddMinutes(remainingMinutes).UtcDateTime,
                FulfilledAtUtc = status == ReservationStatus.Fulfilled ? Now.AddMinutes(-1).UtcDateTime : null,
                CancellationReason = status == ReservationStatus.Canceled ? "private cancellation reason" : null,
                Listing = new Listing
                {
                    Title = title, Description = "Synthetic dashboard fixture", Condition = "Good", CategoryId = 1,
                    Status = listingStatus, CreatedAtUtc = Now.AddDays(-1).UtcDateTime
                }
            };
            database.Reservations.Add(reservation);
            database.AuditEvents.Add(new AuditEvent
            {
                Listing = reservation.Listing, ActorUserId = Owner.Id, Action = "Reserved",
                OccurredAtUtc = reservation.CreatedAtUtc, Details = "private audit details"
            });
            await database.SaveChangesAsync();
            return reservation;
        }

        public async Task<string> SnapshotAsync()
        {
            await using var scope = Factory.Services.CreateAsyncScope();
            var database = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            return JsonSerializer.Serialize(new
            {
                Listings = await database.Listings.AsNoTracking().OrderBy(row => row.Id).Select(row => new
                {
                    row.Id, row.Title, row.Description, row.Condition, row.Size, row.CategoryId, row.Status, row.CreatedAtUtc
                }).ToArrayAsync(),
                Reservations = await database.Reservations.AsNoTracking().OrderBy(row => row.Id).Select(row => new
                {
                    row.Id, row.UserId, row.ListingId, row.Status, row.CreatedAtUtc, row.ExpiresAtUtc, row.FulfilledAtUtc, row.CancellationReason
                }).ToArrayAsync(),
                Audits = await database.AuditEvents.AsNoTracking().OrderBy(row => row.Id).Select(row => new
                {
                    row.Id, row.ListingId, row.ActorUserId, row.Action, row.OccurredAtUtc, row.Details
                }).ToArrayAsync()
            });
        }

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await Factory.DisposeAsync();
            await original.DisposeAsync();
        }
    }
}

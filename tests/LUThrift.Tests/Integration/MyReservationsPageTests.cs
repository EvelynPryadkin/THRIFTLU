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
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace LUThrift.Tests.Integration;

public class MyReservationsPageTests
{
    private const string PagePath = "/Account/Reservations";
    private static readonly DateTimeOffset Now = new(2026, 10, 26, 15, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task TwoStudents_SeeOnlyTheirOwnCurrentAndHistoryDespiteOwnerQueries()
    {
        await using var test = await TwoStudentTest.CreateAsync();
        var firstCurrent = await test.SeedAsync(test.First, "<b>First student's coat</b>", ReservationStatus.Active);
        var firstHistory = await test.SeedAsync(test.First, "First student's old lamp", ReservationStatus.Canceled,
            listingStatus: ListingStatus.Archived);
        var secondCurrent = await test.SeedAsync(test.Second, "Second student's desk", ReservationStatus.Active);
        var secondHistory = await test.SeedAsync(test.Second, "Second student's old book", ReservationStatus.Fulfilled,
            listingStatus: ListingStatus.Collected);
        var before = await test.SnapshotAsync();

        foreach (var (student, current, history, other, otherCurrent, otherHistory) in new[]
        {
            (test.First, firstCurrent, firstHistory, test.Second, secondCurrent, secondHistory),
            (test.Second, secondCurrent, secondHistory, test.First, firstCurrent, firstHistory)
        })
        {
            var query = QueryHelpers.AddQueryString(PagePath, new Dictionary<string, string?>
            {
                ["UserId"] = other.Id, ["owner"] = other.Id,
                ["reservationId"] = otherCurrent.Id.ToString(CultureInfo.InvariantCulture)
            });
            foreach (var path in new[] { PagePath, query })
            {
                var html = await GetPageAsync(student, path);
                AssertCount(html, 1);
                Assert.Equal(new[] { current.Id }, CardIds(Section(html, "current-reservations")));
                Assert.Equal(new[] { history.Id }, CardIds(Section(html, "reservation-history")));
                var main = MainContent(html);
                Assert.Contains(current.Listing.Title, WebUtility.HtmlDecode(main));
                Assert.Contains(history.Listing.Title, WebUtility.HtmlDecode(main));
                Assert.DoesNotContain(otherCurrent.Listing.Title, WebUtility.HtmlDecode(main));
                Assert.DoesNotContain(otherHistory.Listing.Title, WebUtility.HtmlDecode(main));
            }
        }

        var firstPage = await GetPageAsync(test.First);
        Assert.DoesNotContain(firstCurrent.Listing.Title, MainContent(firstPage));
        Assert.Contains("Current reservations", Section(firstPage, "current-reservations"));
        Assert.Contains("Recent history", Section(firstPage, "reservation-history"));
        AssertUnchanged(before, await test.SnapshotAsync());
    }

    [Fact]
    public async Task ForgedCancellation_CannotChooseTheOtherLoggedInStudentOrRevealTheirReservation()
    {
        await using var test = await TwoStudentTest.CreateAsync();
        var first = await test.SeedAsync(test.First, "First private hold", ReservationStatus.Active);
        var second = await test.SeedAsync(test.Second, "Second private hold", ReservationStatus.Active);
        var firstToken = Token(await GetPageAsync(test.First));
        var secondToken = Token(await GetPageAsync(test.Second));
        var before = await test.SnapshotAsync();

        using var missingToken = await test.First.Client.PostAsync(PagePath + "?handler=Cancel",
            new FormUrlEncodedContent(new Dictionary<string, string> { ["id"] = first.Id.ToString(CultureInfo.InvariantCulture) }));
        Assert.Equal(HttpStatusCode.BadRequest, missingToken.StatusCode);
        using var otherStudentsToken = await PostCancelAsync(test.First, second.Id, secondToken);
        Assert.Equal(HttpStatusCode.BadRequest, otherStudentsToken.StatusCode);

        var messages = new List<string>();
        foreach (var id in new[] { second.Id, int.MaxValue })
        {
            using var response = await test.First.Client.PostAsync(PagePath + "?handler=Cancel", new FormUrlEncodedContent(
                new Dictionary<string, string>
                {
                    ["id"] = id.ToString(CultureInfo.InvariantCulture), ["__RequestVerificationToken"] = firstToken,
                    ["UserId"] = test.Second.Id, ["owner"] = test.Second.Id,
                    ["ListingId"] = second.ListingId.ToString(CultureInfo.InvariantCulture)
                }));
            AssertPrg(response);
            var html = await GetPageAsync(test.First);
            messages.Add(Message(html));
            Assert.DoesNotContain(second.Listing.Title, MainContent(html));
            AssertCount(html, 1);
        }

        Assert.Equal(messages[0], messages[1]);
        AssertCount(await GetPageAsync(test.Second), 1);
        AssertUnchanged(before, await test.SnapshotAsync());
    }

    [Fact]
    public async Task Cancel_UpdatesOnlyOwnersCountAndHistoryAndRepeatedSubmissionIsSafe()
    {
        await using var test = await TwoStudentTest.CreateAsync();
        var canceled = await test.SeedAsync(test.First, "Cancelable coat", ReservationStatus.Active);
        var retained = await test.SeedAsync(test.First, "Retained lamp", ReservationStatus.Active);
        var third = await test.SeedAsync(test.First, "Retained book", ReservationStatus.Active);
        var other = await test.SeedAsync(test.Second, "Other student's hold", ReservationStatus.Active);
        var initial = await GetPageAsync(test.First);
        AssertCount(initial, 3);
        var token = Token(Card(initial, canceled.Id));

        using var response = await PostCancelAsync(test.First, canceled.Id, token);
        AssertPrg(response);
        var updated = await GetPageAsync(test.First);
        AssertCount(updated, 2);
        Assert.Equal(new[] { retained.Id, third.Id }.Order(), CardIds(Section(updated, "current-reservations")).Order());
        Assert.Equal(new[] { canceled.Id }, CardIds(Section(updated, "reservation-history")));
        Assert.Contains("Canceled", Card(updated, canceled.Id));
        Assert.DoesNotContain("<form", Card(updated, canceled.Id), StringComparison.OrdinalIgnoreCase);
        AssertCount(await GetPageAsync(test.Second), 1);

        var afterCancel = await test.ReadStateAsync();
        Assert.Equal(ReservationStatus.Canceled, afterCancel.Reservations.Single(row => row.Id == canceled.Id).Status);
        Assert.Equal(ListingStatus.Available, afterCancel.Listings.Single(row => row.Id == canceled.ListingId).Status);
        Assert.Equal(ReservationStatus.Active, afterCancel.Reservations.Single(row => row.Id == other.Id).Status);
        Assert.Equal(ListingStatus.Held, afterCancel.Listings.Single(row => row.Id == other.ListingId).Status);
        var audit = Assert.Single(afterCancel.Audits);
        Assert.Equal(test.First.Id, audit.ActorUserId);
        Assert.Equal(canceled.ListingId, audit.ListingId);
        var beforeRepeat = await test.SnapshotAsync();

        using var repeated = await PostCancelAsync(test.First, canceled.Id, token);
        AssertPrg(repeated);
        var repeatedPage = await GetPageAsync(test.First);
        AssertCount(repeatedPage, 2);
        Assert.Contains("no longer active", Message(repeatedPage));
        AssertUnchanged(beforeRepeat, await test.SnapshotAsync());
    }

    [Fact]
    public async Task Deadline_BeforeAtAndAfterMovesTheDisplayWithoutMutatingStoredActiveHistory()
    {
        await using var test = await TwoStudentTest.CreateAsync();
        var deadline = Now.AddMinutes(1);
        var ending = await test.SeedAsync(test.First, "Reservation ending at the boundary", ReservationStatus.Active,
            expiresAt: deadline);
        await test.SeedAsync(test.Second, "Other student's later deadline", ReservationStatus.Active,
            expiresAt: Now.AddMinutes(20));
        var before = await test.SnapshotAsync();

        foreach (var (instant, activeCount) in new[]
        {
            (deadline.AddTicks(-1), 1), (deadline, 0), (deadline.AddTicks(1), 0)
        })
        {
            test.Clock.AdvanceTo(instant);
            var html = await GetPageAsync(test.First);
            AssertCount(html, activeCount);
            var current = Section(html, "current-reservations");
            var history = Section(html, "reservation-history");
            Assert.Equal(activeCount == 1 ? new[] { ending.Id } : [], CardIds(current));
            Assert.Equal(activeCount == 0 ? new[] { ending.Id } : [], CardIds(history));
            var card = Card(html, ending.Id);
            Assert.Contains("2026-10-26 10:01:00 (America/Chicago, UTC-05:00)", WebUtility.HtmlDecode(card));
            Assert.Contains(deadline.UtcDateTime.ToString("O", CultureInfo.InvariantCulture), card);
            Assert.Equal(activeCount == 1, card.Contains("Cancel reservation", StringComparison.Ordinal));
            if (activeCount == 0) Assert.Contains("Expired", card);
            AssertCount(await GetPageAsync(test.Second), 1);
        }

        AssertUnchanged(before, await test.SnapshotAsync());
        var state = await test.ReadStateAsync();
        Assert.Equal(ReservationStatus.Active, state.Reservations.Single(row => row.Id == ending.Id).Status);
        Assert.Equal(ListingStatus.Held, state.Listings.Single(row => row.Id == ending.ListingId).Status);
        Assert.Empty(state.Audits);
    }

    [Fact]
    public async Task History_ShowsAllTerminalStatesAndNonpublicTitlesWithoutMakingThoseListingsPublic()
    {
        await using var test = await TwoStudentTest.CreateAsync();
        var canceled = await test.SeedAsync(test.First, "<script>archived winter coat</script>", ReservationStatus.Canceled,
            listingStatus: ListingStatus.Archived);
        var expired = await test.SeedAsync(test.First, "Returned desk lamp", ReservationStatus.Expired,
            listingStatus: ListingStatus.Available);
        var fulfilled = await test.SeedAsync(test.First, "Collected textbook", ReservationStatus.Fulfilled,
            listingStatus: ListingStatus.Collected);
        var overdue = await test.SeedAsync(test.First, "Overdue desk", ReservationStatus.Active, expiresAt: Now.AddTicks(-1));
        var current = await test.SeedAsync(test.First, "Current small rug", ReservationStatus.Active);
        var other = await test.SeedAsync(test.Second, "Private second student's old chair", ReservationStatus.Canceled);
        var before = await test.SnapshotAsync();

        var html = await GetPageAsync(test.First);
        AssertCount(html, 1);
        Assert.Equal(new[] { current.Id }, CardIds(Section(html, "current-reservations")));
        var history = Section(html, "reservation-history");
        Assert.Equal(new[] { canceled.Id, expired.Id, fulfilled.Id, overdue.Id }.Order(), CardIds(history).Order());
        foreach (var (reservation, status) in new[]
        {
            (canceled, "Canceled"), (expired, "Expired"), (fulfilled, "Fulfilled"), (overdue, "Expired")
        })
        {
            var card = Card(history, reservation.Id);
            Assert.Contains(reservation.Listing.Title, WebUtility.HtmlDecode(card));
            Assert.Contains(status, card);
            Assert.DoesNotContain("<form", card, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("/Thrift/Details/", card, StringComparison.OrdinalIgnoreCase);
        }
        Assert.DoesNotContain(canceled.Listing.Title, history);
        Assert.DoesNotContain(other.Listing.Title, WebUtility.HtmlDecode(html));
        foreach (var listingId in new[] { canceled.ListingId, fulfilled.ListingId })
        {
            using var details = await test.First.Client.GetAsync("/Thrift/Details/" + listingId);
            Assert.Equal(HttpStatusCode.NotFound, details.StatusCode);
        }

        AssertUnchanged(before, await test.SnapshotAsync());
    }

    [Fact]
    public async Task History_IsLimitedToTwentyNewestOwnedRowsWithStableTiesAndDoesNotTruncateCurrentReservations()
    {
        await using var test = await TwoStudentTest.CreateAsync();
        var firstCurrent = await test.SeedAsync(test.First, "Old-created current reservation one", ReservationStatus.Active,
            createdAt: Now.AddDays(-60));
        var secondCurrent = await test.SeedAsync(test.First, "Old-created current reservation two", ReservationStatus.Active,
            createdAt: Now.AddDays(-60));
        int[] daysAgo = [5, 1, 3, 2, 3, 8, 4, 6, 7, 10, 9, 12, 11, 13, 14, 15, 17, 16, 18, 19, 20, 21, 22, 23, 24];
        var history = new List<Reservation>();
        for (var index = 0; index < daysAgo.Length; index++)
        {
            history.Add(await test.SeedAsync(test.First, $"Owned history row {index:D2}", ReservationStatus.Canceled,
                createdAt: Now.AddDays(-daysAgo[index]), expiresAt: Now.AddDays(-daysAgo[index]).AddHours(1)));
        }
        for (var index = 0; index < 3; index++)
            await test.SeedAsync(test.Second, $"Other student's newer history {index}", ReservationStatus.Fulfilled,
                createdAt: Now.AddMinutes(-index));
        var before = await test.SnapshotAsync();

        var html = await GetPageAsync(test.First);

        AssertCount(html, 2);
        Assert.Equal(new[] { firstCurrent.Id, secondCurrent.Id }.Order(), CardIds(Section(html, "current-reservations")).Order());
        int[] expectedIndexes = [1, 3, 4, 2, 6, 0, 7, 8, 5, 10, 9, 12, 11, 13, 14, 15, 17, 16, 18, 19];
        Assert.Equal(expectedIndexes.Select(index => history[index].Id), CardIds(Section(html, "reservation-history")));
        Assert.Equal(20, CardIds(Section(html, "reservation-history")).Length);
        AssertUnchanged(before, await test.SnapshotAsync());
    }

    private static async Task<string> GetPageAsync(Student student, string path = PagePath)
    {
        using var response = await student.Client.GetAsync(path);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadAsStringAsync();
    }

    private static Task<HttpResponseMessage> PostCancelAsync(Student student, int id, string token)
        => student.Client.PostAsync(PagePath + "?handler=Cancel", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["id"] = id.ToString(CultureInfo.InvariantCulture), ["__RequestVerificationToken"] = token
        }));

    private static void AssertPrg(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal(PagePath, response.Headers.Location?.OriginalString);
    }

    private static string MainContent(string html)
        => Regex.Match(html, @"<main\b[^>]*>([\s\S]*?)</main>", RegexOptions.IgnoreCase).Groups[1].Value;

    private static string Section(string html, string id)
    {
        var section = Regex.Match(html, "<section\\b[^>]*id=\"" + Regex.Escape(id) + "\"[^>]*>([\\s\\S]*?)</section>",
            RegexOptions.IgnoreCase);
        Assert.True(section.Success, $"Expected the {id} section.");
        return section.Groups[1].Value;
    }

    private static int[] CardIds(string html)
        => Regex.Matches(html, "<article\\b[^>]*aria-labelledby=\"reservation-title-(\\d+)\"", RegexOptions.IgnoreCase)
            .Cast<Match>().Select(match => int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture)).ToArray();

    private static string Card(string html, int id)
        => Assert.Single(Regex.Matches(html, @"<article\b[^>]*>[\s\S]*?</article>", RegexOptions.IgnoreCase).Cast<Match>(),
            match => match.Value.Contains($"aria-labelledby=\"reservation-title-{id}\"", StringComparison.Ordinal)).Value;

    private static void AssertCount(string html, int expected)
    {
        var count = Regex.Match(html,
            "<(?<tag>p|div|span)\\b[^>]*id=\"active-reservation-count\"[^>]*>(?<text>[\\s\\S]*?)</\\k<tag>>",
            RegexOptions.IgnoreCase);
        Assert.True(count.Success, "Expected an active reservation count.");
        Assert.Equal($"Active reservations: {expected} of 3", Text(count.Groups["text"].Value));
    }

    private static string Token(string html)
    {
        var token = Regex.Match(html, "<input\\b[^>]*name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"",
            RegexOptions.IgnoreCase);
        Assert.True(token.Success);
        return WebUtility.HtmlDecode(token.Groups[1].Value);
    }

    private static string Message(string html)
    {
        var alert = Regex.Match(MainContent(html), "<div\\b[^>]*role=\"status\"[^>]*>([\\s\\S]*?)</div>", RegexOptions.IgnoreCase);
        Assert.True(alert.Success);
        return Text(alert.Groups[1].Value);
    }

    private static string Text(string html)
        => Regex.Replace(WebUtility.HtmlDecode(Regex.Replace(html, "<[^>]+>", string.Empty)), @"\s+", " ").Trim();

    private static void AssertUnchanged(Snapshot before, Snapshot after)
    {
        Assert.Equal(before.Listings, after.Listings);
        Assert.Equal(before.Reservations, after.Reservations);
        Assert.Equal(before.Audits, after.Audits);
    }

    private sealed record Student(HttpClient Client, string Id);
    private sealed record State(Listing[] Listings, Reservation[] Reservations, AuditEvent[] Audits);
    private sealed record Snapshot(object[] Listings, object[] Reservations, object[] Audits);

    private sealed class MutableClock : TimeProvider
    {
        private long ticks = Now.UtcTicks;
        public void AdvanceTo(DateTimeOffset instant) => Interlocked.Exchange(ref ticks, instant.UtcTicks);
        public override DateTimeOffset GetUtcNow() => new(Interlocked.Read(ref ticks), TimeSpan.Zero);
    }

    private sealed class TwoStudentTest(
        AuthorizationWebApplicationFactory original, WebApplicationFactory<ApplicationDbContext> factory,
        Student first, Student second, MutableClock clock) : IAsyncDisposable
    {
        public Student First { get; } = first;
        public Student Second { get; } = second;
        public MutableClock Clock { get; } = clock;

        public static async Task<TwoStudentTest> CreateAsync()
        {
            var original = new AuthorizationWebApplicationFactory();
            var clock = new MutableClock();
            var factory = original.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
            {
                services.RemoveAll<TimeProvider>();
                services.AddSingleton<TimeProvider>(clock);
                services.PostConfigure<ReservationOptions>(options => options.CampusTimeZone = "America/Chicago");
            }));
            var first = await LoginStudentAsync(factory, "First reservation student");
            var second = await LoginStudentAsync(factory, "Second reservation student");
            Assert.NotEqual(first.Id, second.Id);
            return new(original, factory, first, second, clock);
        }

        private static async Task<Student> LoginStudentAsync(WebApplicationFactory<ApplicationDbContext> factory, string displayName)
        {
            var client = factory.CreateClient(new WebApplicationFactoryClientOptions
            {
                AllowAutoRedirect = false, BaseAddress = new Uri("https://localhost")
            });
            await using var scope = factory.Services.CreateAsyncScope();
            var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            var email = IdentityTestContext.NewEmail();
            var password = IdentityTestContext.NewPassword();
            var user = new ApplicationUser { UserName = email, Email = email, DisplayName = displayName };
            Assert.True((await users.CreateAsync(user, password)).Succeeded);
            Assert.True((await users.AddToRoleAsync(user, ApplicationRoles.Student)).Succeeded);
            var token = Token(await client.GetStringAsync("/Identity/Account/Login"));
            using var login = await client.PostAsync("/Identity/Account/Login", new FormUrlEncodedContent(
                new Dictionary<string, string>
                {
                    ["Input.Email"] = email, ["Input.Password"] = password, ["__RequestVerificationToken"] = token
                }));
            Assert.Equal(HttpStatusCode.Redirect, login.StatusCode);
            return new(client, user.Id);
        }

        public async Task<Reservation> SeedAsync(Student owner, string title, ReservationStatus status,
            ListingStatus? listingStatus = null, DateTimeOffset? createdAt = null, DateTimeOffset? expiresAt = null)
        {
            var creation = createdAt ?? Now.AddMinutes(-30);
            var expiration = expiresAt ?? (status == ReservationStatus.Active ? Now.AddMinutes(30) : Now.AddMinutes(-10));
            await using var scope = factory.Services.CreateAsyncScope();
            var database = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var reservation = new Reservation
            {
                UserId = owner.Id, Status = status, CreatedAtUtc = creation.UtcDateTime, ExpiresAtUtc = expiration.UtcDateTime,
                FulfilledAtUtc = status == ReservationStatus.Fulfilled ? expiration.UtcDateTime : null,
                CancellationReason = status == ReservationStatus.Canceled ? "Synthetic historical cancellation." : null,
                Listing = new Listing
                {
                    Title = title, Description = "My reservations HTTP test item.", Condition = "Good", CategoryId = 1,
                    Status = listingStatus ?? (status == ReservationStatus.Active ? ListingStatus.Held : ListingStatus.Available),
                    CreatedAtUtc = creation.UtcDateTime
                }
            };
            database.Reservations.Add(reservation);
            await database.SaveChangesAsync();
            return reservation;
        }

        public async Task<State> ReadStateAsync()
        {
            await using var scope = factory.Services.CreateAsyncScope();
            var database = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            return new(await database.Listings.AsNoTracking().OrderBy(row => row.Id).ToArrayAsync(),
                await database.Reservations.AsNoTracking().OrderBy(row => row.Id).ToArrayAsync(),
                await database.AuditEvents.AsNoTracking().OrderBy(row => row.Id).ToArrayAsync());
        }

        public async Task<Snapshot> SnapshotAsync()
        {
            var state = await ReadStateAsync();
            return new(
                state.Listings.Select(row => (object)(row.Id, row.Title, row.Description, row.Condition, row.Size,
                    row.CategoryId, row.Status, row.CreatedAtUtc)).ToArray(),
                state.Reservations.Select(row => (object)(row.Id, row.UserId, row.ListingId, row.Status,
                    row.CreatedAtUtc, row.ExpiresAtUtc, row.FulfilledAtUtc, row.CancellationReason)).ToArray(),
                state.Audits.Select(row => (object)(row.Id, row.ListingId, row.ActorUserId, row.Action,
                    row.OccurredAtUtc, row.Details)).ToArray());
        }

        public async ValueTask DisposeAsync()
        {
            First.Client.Dispose();
            Second.Client.Dispose();
            await factory.DisposeAsync();
            await original.DisposeAsync();
        }
    }
}

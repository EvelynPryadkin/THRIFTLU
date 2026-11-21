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

public sealed class StaffPickupWorkflowTests
{
    private const string StaffPath = "/Staff/Pickups";
    private const string AccountPath = "/Account/Reservations";
    private static readonly DateTimeOffset Start = new(2026, 11, 2, 15, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(false, ApplicationRoles.Staff)]
    [InlineData(true, ApplicationRoles.Administrator)]
    public async Task StudentReserveToStaffAction_PreservesPrivacyHistoryCountsAndPublicAvailability(bool missing, string staffRole)
    {
        await using var test = await Workflow.CreateAsync(staffRole);
        var selected = await test.AddListingAsync("Workflow winter coat");
        var otherItem = await test.AddListingAsync("Other student's workflow lamp");
        Assert.Empty((await test.ReadAsync()).Reservations);

        // These holds are created through the real student forms and service, not inserted as fixtures.
        await ReserveThroughPageAsync(test.Student, selected.Id);
        await ReserveThroughPageAsync(test.OtherStudent, otherItem.Id);
        var held = await test.ReadAsync();
        var reservation = Assert.Single(held.Reservations, row => row.ListingId == selected.Id);
        var otherReservation = Assert.Single(held.Reservations, row => row.ListingId == otherItem.Id);
        Assert.Equal(test.Student.User.Id, reservation.UserId);
        Assert.Equal(Start.UtcDateTime, reservation.CreatedAtUtc);
        Assert.Equal(Start.AddHours(1).UtcDateTime, reservation.ExpiresAtUtc);
        Assert.Equal(ReservationStatus.Active, reservation.Status);
        Assert.Equal(ListingStatus.Held, held.Listings.Single(row => row.Id == selected.Id).Status);
        Assert.Equal(2, held.Audits.Length);
        Assert.All(held.Audits, row => Assert.Equal(InventoryAuditActions.Reserved, row.Action));
        Assert.Equal(new[] { test.Student.User.Id, test.OtherStudent.User.Id }.OrderBy(id => id),
            held.Audits.Select(row => row.ActorUserId).OrderBy(id => id));
        var heldSnapshot = Snapshot(held);

        var accountBefore = await test.Student.Client.GetStringAsync(AccountPath);
        Assert.Contains("Active reservations: 1 of 3", accountBefore);
        var staleCancel = Form(Card(accountBefore, "reservation-title-" + reservation.Id),
            action => action.Contains("handler=Cancel", StringComparison.Ordinal));
        var emailLookup = QueryHelpers.AddQueryString(StaffPath, "Filter.StudentEmail", "  " + test.Student.User.Email!.ToUpperInvariant() + "  ");
        var emailPage = await test.Staff.Client.GetStringAsync(emailLookup);
        Assert.Equal(new[] { reservation.Id }, PickupIds(emailPage));
        Assert.Contains(test.Student.User.Email!, emailPage);
        Assert.DoesNotContain(test.OtherStudent.User.Email!, emailPage);
        var idLookup = QueryHelpers.AddQueryString(StaffPath, new Dictionary<string, string?>
        {
            ["Filter.ReservationId"] = reservation.Id.ToString(CultureInfo.InvariantCulture),
            ["Filter.StudentEmail"] = test.Student.User.Email
        });
        var idPage = await test.Staff.Client.GetStringAsync(idLookup);
        Assert.Equal(new[] { reservation.Id }, PickupIds(idPage));
        var staffCard = Card(idPage, "pickup-title-" + reservation.Id);
        var handler = missing ? "ReportMissing" : "ConfirmPickup";
        var staffForm = Form(staffCard, action => action.Contains("handler=" + handler, StringComparison.Ordinal));
        foreach (var availability in new[] { "Reserved", "All" })
        {
            var publicPage = await test.Public.GetStringAsync("/Thrift?Filter.Availability=" + availability);
            Assert.Contains(selected.Title, WebUtility.HtmlDecode(publicPage));
            Assert.DoesNotContain(test.Student.User.Email!, publicPage);
            Assert.DoesNotContain(test.OtherStudent.User.Email!, publicPage);
        }
        Assert.Equal(heldSnapshot, Snapshot(await test.ReadAsync()));

        test.Clock.AdvanceTo(Start.AddMinutes(10));
        using var action = await test.Staff.Client.PostAsync(Attribute(staffForm, "action"), new FormUrlEncodedContent(Fields(staffForm)));
        AssertRedirect(action, StaffPath);
        var redirectQuery = QueryHelpers.ParseQuery(new Uri(test.Staff.Client.BaseAddress!, action.Headers.Location!).Query);
        Assert.Equal(reservation.Id.ToString(CultureInfo.InvariantCulture), redirectQuery["Filter.ReservationId"].ToString());

        var finished = await test.ReadAsync();
        var saved = Assert.Single(finished.Reservations, row => row.Id == reservation.Id);
        var expectedReservation = missing ? ReservationStatus.Canceled : ReservationStatus.Fulfilled;
        var expectedListing = missing ? ListingStatus.Missing : ListingStatus.Collected;
        Assert.Equal(expectedReservation, saved.Status);
        Assert.Equal(expectedListing, finished.Listings.Single(row => row.Id == selected.Id).Status);
        Assert.Equal(reservation.CreatedAtUtc, saved.CreatedAtUtc);
        Assert.Equal(reservation.ExpiresAtUtc, saved.ExpiresAtUtc);
        Assert.Equal(reservation.UserId, saved.UserId);
        Assert.Equal(missing ? null : Start.AddMinutes(10).UtcDateTime, saved.FulfilledAtUtc);
        Assert.Equal(missing ? ReservationCancellationReasons.ItemNotFound : null, saved.CancellationReason);
        Assert.Equal(ReservationSnapshot(otherReservation),
            ReservationSnapshot(finished.Reservations.Single(row => row.Id == otherReservation.Id)));
        Assert.Equal(ListingStatus.Held, finished.Listings.Single(row => row.Id == otherItem.Id).Status);
        var audit = Assert.Single(finished.Audits, row => row.Action != InventoryAuditActions.Reserved);
        Assert.Equal(3, finished.Audits.Length);
        Assert.Equal(missing ? InventoryAuditActions.ReservationItemMissing : InventoryAuditActions.ReservationFulfilled, audit.Action);
        Assert.Equal(test.Staff.User.Id, audit.ActorUserId);
        Assert.Equal(selected.Id, audit.ListingId);
        Assert.Equal(Start.AddMinutes(10).UtcDateTime, audit.OccurredAtUtc);
        Assert.Contains($"Reservation {reservation.Id}", audit.Details);
        var finishedSnapshot = Snapshot(finished);

        var staffResult = await test.Staff.Client.GetStringAsync(action.Headers.Location!);
        Assert.Contains(missing ? "Item reported missing." : "Pickup confirmed.", WebUtility.HtmlDecode(staffResult));
        Assert.Empty(Forms(Card(staffResult, "pickup-title-" + reservation.Id)));
        var ownerPage = await test.Student.Client.GetStringAsync(AccountPath);
        Assert.Contains("Active reservations: 0 of 3", ownerPage);
        var history = Card(ownerPage, "reservation-title-" + reservation.Id);
        Assert.Contains(expectedReservation.ToString(), history);
        Assert.Empty(Forms(history));
        if (missing)
        {
            Assert.Contains(ReservationCancellationReasons.ItemNotFound, WebUtility.HtmlDecode(history));
            Assert.DoesNotContain("<dt>Collected</dt>", history);
        }
        else
        {
            Assert.Contains("<dt>Collected</dt>", history);
            await using var formatScope = test.Factory.Services.CreateAsyncScope();
            var formatter = formatScope.ServiceProvider.GetRequiredService<CampusTimeFormatter>();
            Assert.Contains(await formatter.FormatAsync(saved.FulfilledAtUtc!.Value), WebUtility.HtmlDecode(history));
            Assert.Contains("America/Chicago, UTC-06:00", WebUtility.HtmlDecode(history));
        }
        var otherPage = await test.OtherStudent.Client.GetStringAsync(AccountPath + "?UserId=" + test.Student.User.Id);
        Assert.Contains("Active reservations: 1 of 3", otherPage);
        Assert.DoesNotContain(selected.Title, WebUtility.HtmlDecode(otherPage));
        Assert.Contains(otherItem.Title, WebUtility.HtmlDecode(otherPage));
        foreach (var availability in new[] { "Available", "Reserved", "All" })
        {
            var publicPage = await test.Public.GetStringAsync("/Thrift?Filter.Availability=" + availability);
            Assert.DoesNotContain(selected.Title, WebUtility.HtmlDecode(publicPage));
            Assert.DoesNotContain(test.Student.User.Email!, publicPage);
        }
        using var unavailableDetails = await test.Public.GetAsync("/Thrift/Details/" + selected.Id);
        Assert.Equal(HttpStatusCode.NotFound, unavailableDetails.StatusCode);
        using var otherDetails = await test.Public.GetAsync("/Thrift/Details/" + otherItem.Id);
        Assert.Equal(HttpStatusCode.OK, otherDetails.StatusCode);
        Assert.Equal(finishedSnapshot, Snapshot(await test.ReadAsync()));

        // A form loaded while the hold was active cannot undo the staff's completed transition.
        using var stale = await test.Student.Client.PostAsync(Attribute(staleCancel, "action"), new FormUrlEncodedContent(Fields(staleCancel)));
        AssertRedirect(stale, AccountPath);
        Assert.Equal(finishedSnapshot, Snapshot(await test.ReadAsync()));
    }

    private static async Task ReserveThroughPageAsync(Session student, int listingId)
    {
        var details = await student.Client.GetStringAsync("/Thrift/Details/" + listingId);
        var form = Form(details, action => action == "/Account/Reserve");
        Assert.Equal("post", Attribute(form, "method"));
        Assert.Equal(listingId.ToString(CultureInfo.InvariantCulture), Fields(form)["ListingId"]);
        using var reserve = await student.Client.PostAsync(Attribute(form, "action"), new FormUrlEncodedContent(Fields(form)));
        AssertRedirect(reserve, AccountPath);
        var account = await student.Client.GetStringAsync(reserve.Headers.Location!);
        Assert.Contains("Reservation confirmed.", WebUtility.HtmlDecode(account));
    }

    private static string[] Forms(string html)
        => Regex.Matches(html, @"<form\b[^>]*>[\s\S]*?</form>", RegexOptions.IgnoreCase).Cast<Match>().Select(match => match.Value).ToArray();
    private static string Form(string html, Func<string, bool> matchesAction)
        => Assert.Single(Forms(html), form => matchesAction(Attribute(form, "action")));
    private static string Attribute(string html, string name)
        => WebUtility.HtmlDecode(Regex.Match(html, "\\b" + name + "=\"([^\"]*)\"", RegexOptions.IgnoreCase).Groups[1].Value);
    private static Dictionary<string, string> Fields(string form)
        => Regex.Matches(form, @"<input\b[^>]*>", RegexOptions.IgnoreCase).Cast<Match>()
            .ToDictionary(input => Attribute(input.Value, "name"), input => Attribute(input.Value, "value"));
    private static string Card(string html, string label)
        => Assert.Single(Regex.Matches(html, @"<article\b[^>]*>[\s\S]*?</article>", RegexOptions.IgnoreCase).Cast<Match>(),
            card => Attribute(card.Value, "aria-labelledby") == label).Value;
    private static int[] PickupIds(string html)
        => Regex.Matches(html, "<article\\b[^>]*aria-labelledby=\"pickup-title-(\\d+)\"", RegexOptions.IgnoreCase).Cast<Match>()
            .Select(card => int.Parse(card.Groups[1].Value, CultureInfo.InvariantCulture)).ToArray();
    private static void AssertRedirect(HttpResponseMessage response, string path)
    {
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.NotNull(response.Headers.Location);
        Assert.Equal(path, new Uri(new Uri("https://localhost"), response.Headers.Location).AbsolutePath);
    }
    private static object ReservationSnapshot(Reservation row)
        => new { row.Id, row.UserId, row.ListingId, row.Status, row.CreatedAtUtc, row.ExpiresAtUtc, row.FulfilledAtUtc, row.CancellationReason };
    private static string Snapshot(State state)
        => JsonSerializer.Serialize(new
        {
            Listings = state.Listings.Select(row => new { row.Id, row.Title, row.Description, row.Condition, row.Size, row.CategoryId, row.Status, row.CreatedAtUtc }),
            Reservations = state.Reservations.Select(ReservationSnapshot),
            Audits = state.Audits.Select(row => new { row.Id, row.ListingId, row.ActorUserId, row.Action, row.OccurredAtUtc, row.Details })
        });

    private sealed record Session(HttpClient Client, ApplicationUser User);
    private sealed record State(Listing[] Listings, Reservation[] Reservations, AuditEvent[] Audits);
    private sealed class Clock : TimeProvider
    {
        private long ticks = Start.UtcTicks;
        public void AdvanceTo(DateTimeOffset now) => Interlocked.Exchange(ref ticks, now.UtcTicks);
        public override DateTimeOffset GetUtcNow() => new(Interlocked.Read(ref ticks), TimeSpan.Zero);
    }
    private sealed class Workflow(AuthorizationWebApplicationFactory original, WebApplicationFactory<ApplicationDbContext> factory,
        Session student, Session otherStudent, Session staff, HttpClient publicClient, Clock clock) : IAsyncDisposable
    {
        public WebApplicationFactory<ApplicationDbContext> Factory { get; } = factory;
        public Session Student { get; } = student;
        public Session OtherStudent { get; } = otherStudent;
        public Session Staff { get; } = staff;
        public HttpClient Public { get; } = publicClient;
        public Clock Clock { get; } = clock;

        public static async Task<Workflow> CreateAsync(string staffRole)
        {
            var original = new AuthorizationWebApplicationFactory();
            var clock = new Clock();
            var factory = original.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
            {
                services.RemoveAll<TimeProvider>();
                services.AddSingleton<TimeProvider>(clock);
                services.PostConfigure<ReservationOptions>(options =>
                {
                    options.CampusTimeZone = "America/Chicago";
                    // Explicitly synthetic test hours; these do not establish official operating hours.
                    options.WeeklyPickupIntervals = [new() { Day = "Monday", OpensAt = "09:00", ClosesAt = "17:00" }];
                    options.ClosedDates = [];
                });
            }));
            await PickupScheduleTestData.PersistConfiguredAsync(factory.Services);
            return new(original, factory,
                await SignedInAsync(factory, ApplicationRoles.Student, "Workflow owner"),
                await SignedInAsync(factory, ApplicationRoles.Student, "Other workflow student"),
                await SignedInAsync(factory, staffRole, "Workflow staff"), NewClient(factory), clock);
        }

        private static HttpClient NewClient(WebApplicationFactory<ApplicationDbContext> factory)
            => factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, BaseAddress = new Uri("https://localhost") });
        private static async Task<Session> SignedInAsync(WebApplicationFactory<ApplicationDbContext> factory, string role, string displayName)
        {
            var client = NewClient(factory);
            await using var scope = factory.Services.CreateAsyncScope();
            var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            var email = IdentityTestContext.NewCampusEmail();
            var user = new ApplicationUser { UserName = email, Email = email, EmailConfirmed = true, DisplayName = displayName };
            Assert.True((await users.CreateAsync(user)).Succeeded);
            Assert.True((await users.AddToRoleAsync(user, role)).Succeeded);
            var principal = await scope.ServiceProvider.GetRequiredService<SignInManager<ApplicationUser>>().CreateUserPrincipalAsync(user);
            var cookie = scope.ServiceProvider.GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>().Get(IdentityConstants.ApplicationScheme);
            var now = cookie.TimeProvider!.GetUtcNow();
            var ticket = new AuthenticationTicket(principal, new AuthenticationProperties
            {
                IssuedUtc = now, ExpiresUtc = now.AddMinutes(30)
            }, IdentityConstants.ApplicationScheme);
            client.DefaultRequestHeaders.Add("Cookie", $"{cookie.Cookie.Name}={cookie.TicketDataFormat.Protect(ticket)}");
            return new(client, user);
        }
        public async Task<Listing> AddListingAsync(string title)
        {
            await using var scope = Factory.Services.CreateAsyncScope();
            var database = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var listing = new Listing
            {
                Title = title, Description = "Synthetic end-to-end workflow item", Condition = "Good", CategoryId = 1,
                Status = ListingStatus.Available, CreatedAtUtc = Start.AddDays(-1).UtcDateTime
            };
            database.Listings.Add(listing);
            await database.SaveChangesAsync();
            return listing;
        }
        public async Task<State> ReadAsync()
        {
            await using var scope = Factory.Services.CreateAsyncScope();
            var database = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            return new(await database.Listings.AsNoTracking().OrderBy(row => row.Id).ToArrayAsync(),
                await database.Reservations.AsNoTracking().OrderBy(row => row.Id).ToArrayAsync(),
                await database.AuditEvents.AsNoTracking().OrderBy(row => row.Id).ToArrayAsync());
        }
        public async ValueTask DisposeAsync()
        {
            Student.Client.Dispose();
            OtherStudent.Client.Dispose();
            Staff.Client.Dispose();
            Public.Dispose();
            await Factory.DisposeAsync();
            await original.DisposeAsync();
        }
    }
}

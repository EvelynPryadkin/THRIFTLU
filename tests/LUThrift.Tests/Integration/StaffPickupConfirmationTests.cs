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

public sealed class StaffPickupConfirmationTests
{
    private const string Dashboard = "/Staff/Pickups";
    private const string ConfirmPath = Dashboard + "?handler=ConfirmPickup";
    private static readonly DateTimeOffset InitialNow = new(2026, 10, 29, 15, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(ApplicationRoles.Staff)]
    [InlineData(ApplicationRoles.Administrator)]
    public async Task Confirmation_CommitsPickupAndUpdatesOnlyTheOwnersHistoryAndPublicVisibility(string role)
    {
        await using var test = await PickupTest.CreateAsync(role);
        var reservation = await test.SeedAsync();
        var beforeGet = await test.SnapshotAsync();
        var card = PickupCard(await test.Viewer.Client.GetStringAsync(Dashboard), reservation.Id);
        var form = ConfirmationForm(card);
        Assert.Equal("post", Attribute(form, "method"));
        Assert.Equal(ConfirmPath, Attribute(form, "action"));
        Assert.Equal(new[] { "__RequestVerificationToken", "reservationId" },
            Tags(form, "input").Select(input => Attribute(input, "name")).OrderBy(name => name, StringComparer.Ordinal));
        Assert.Equal(reservation.Id.ToString(CultureInfo.InvariantCulture), InputValue(form, "reservationId"));
        Assert.Contains("Confirm pickup", Text(form));
        Assert.Contains(reservation.Listing.Title, WebUtility.HtmlDecode(card));
        Assert.Contains(test.Owner.User!.DisplayName, WebUtility.HtmlDecode(card));
        Assert.Contains(test.Owner.User.Email!, card);
        Assert.Contains("Pickup deadline", card);
        foreach (var availability in new[] { "Reserved", "All" })
        {
            var catalog = await test.Anonymous.Client.GetStringAsync("/Thrift?Filter.Availability=" + availability);
            Assert.Contains(reservation.Listing.Title, WebUtility.HtmlDecode(catalog));
        }
        Assert.Equal(beforeGet, await test.SnapshotAsync());
        test.Clock.AdvanceTo(InitialNow.AddMinutes(5));

        using var confirmed = await test.PostAsync(reservation.Id.ToString(CultureInfo.InvariantCulture), Token(form));

        AssertPrg(confirmed, reservation.Id);
        var saved = await test.ReadReservationAsync(reservation.Id);
        Assert.Equal(ReservationStatus.Fulfilled, saved.Status);
        Assert.Equal(InitialNow.AddMinutes(5).UtcDateTime, saved.FulfilledAtUtc);
        Assert.Equal(reservation.CreatedAtUtc, saved.CreatedAtUtc);
        Assert.Equal(reservation.ExpiresAtUtc, saved.ExpiresAtUtc);
        Assert.Equal(test.Owner.User.Id, saved.UserId);
        Assert.Equal(ListingStatus.Collected, await test.ListingStatusAsync(reservation.ListingId));
        var audit = await test.ReadPickupAuditAsync();
        Assert.Equal(test.Viewer.User!.Id, audit.ActorUserId);
        Assert.Equal(reservation.ListingId, audit.ListingId);
        Assert.Equal(saved.FulfilledAtUtc, audit.OccurredAtUtc);
        Assert.Contains($"Reservation {reservation.Id}", audit.Details);
        var after = await test.SnapshotAsync();
        var redirected = await test.Viewer.Client.GetStringAsync(confirmed.Headers.Location!);
        Assert.Contains("Pickup confirmed", Message(redirected));
        Assert.Contains("Completed", PickupCard(redirected, reservation.Id));
        Assert.Empty(ConfirmationForms(PickupCard(redirected, reservation.Id)));
        var refreshed = await test.Viewer.Client.GetStringAsync(confirmed.Headers.Location!);
        Assert.Equal(string.Empty, Message(refreshed));

        var ownerPage = await test.Owner.Client.GetStringAsync("/Account/Reservations");
        var ownerCard = AccountCard(ownerPage, reservation.Id);
        Assert.Contains("Fulfilled", ownerCard);
        Assert.Contains("Collected", ownerCard);
        var collected = Regex.Match(ownerCard, @"<dt>Collected</dt>\s*<dd\b[^>]*>\s*(?<time><time\b[^>]*>[\s\S]*?</time>)").Groups["time"].Value;
        Assert.NotEmpty(collected);
        Assert.Equal(saved.FulfilledAtUtc, DateTimeOffset.Parse(Attribute(collected, "datetime"), CultureInfo.InvariantCulture).UtcDateTime);
        Assert.Contains(test.Factory.Services.GetRequiredService<CampusTimeFormatter>().Format(saved.FulfilledAtUtc!.Value),
            WebUtility.HtmlDecode(collected));
        Assert.Contains("America/Chicago, UTC-05:00", WebUtility.HtmlDecode(collected));
        Assert.Empty(Tags(ownerCard, "form"));
        var otherPage = await test.OtherStudent.Client.GetStringAsync("/Account/Reservations?UserId=" + test.Owner.User.Id);
        Assert.DoesNotContain(reservation.Listing.Title, WebUtility.HtmlDecode(otherPage));
        foreach (var availability in new[] { "Available", "Reserved", "All" })
        {
            var catalog = await test.Anonymous.Client.GetStringAsync("/Thrift?Filter.Availability=" + availability);
            Assert.DoesNotContain(reservation.Listing.Title, WebUtility.HtmlDecode(catalog));
        }
        using var details = await test.Anonymous.Client.GetAsync("/Thrift/Details/" + reservation.ListingId);
        Assert.Equal(HttpStatusCode.NotFound, details.StatusCode);
        Assert.Equal(after, await test.SnapshotAsync());
    }

    [Theory]
    [InlineData(null, "/Identity/Account/Login")]
    [InlineData(ApplicationRoles.Student, "/Identity/Account/AccessDenied")]
    public async Task Confirmation_RejectsAnonymousAndStudents(string? role, string target)
    {
        await using var test = await PickupTest.CreateAsync(role);
        var reservation = await test.SeedAsync();
        var page = await test.Viewer.Client.GetStringAsync(role is null ? "/Identity/Account/Login" : "/Account/Reservations");
        var before = await test.SnapshotAsync();

        using var response = await test.PostAsync(reservation.Id.ToString(CultureInfo.InvariantCulture), Token(page));

        AssertRedirect(response, target);
        Assert.Equal(before, await test.SnapshotAsync());
    }

    [Theory]
    [InlineData(ApplicationRoles.Staff, true)]
    [InlineData(ApplicationRoles.Staff, false)]
    [InlineData(ApplicationRoles.Administrator, true)]
    [InlineData(ApplicationRoles.Administrator, false)]
    public async Task Confirmation_RechecksRevokedOrInactiveStaffAfterTheFormWasLoaded(string role, bool revokeRole)
    {
        await using var test = await PickupTest.CreateAsync(role);
        var reservation = await test.SeedAsync();
        var token = Token(ConfirmationForm(PickupCard(await test.Viewer.Client.GetStringAsync(Dashboard), reservation.Id)));
        await test.MutateAsync(async database =>
        {
            if (revokeRole) await database.UserRoles.Where(row => row.UserId == test.Viewer.User!.Id).ExecuteDeleteAsync();
            else await database.Users.Where(row => row.Id == test.Viewer.User!.Id)
                .ExecuteUpdateAsync(setters => setters.SetProperty(row => row.IsActive, false));
        });
        var before = await test.SnapshotAsync();

        using var response = await test.PostAsync(reservation.Id.ToString(CultureInfo.InvariantCulture), token);

        AssertRedirect(response, "/Identity/Account/AccessDenied");
        Assert.Equal(before, await test.SnapshotAsync());
    }

    [Fact]
    public async Task Confirmation_MissingAntiforgeryTokenRejectsWithoutWrites()
    {
        await using var test = await PickupTest.CreateAsync();
        var reservation = await test.SeedAsync();
        await test.Viewer.Client.GetStringAsync(Dashboard);
        var before = await test.SnapshotAsync();

        using var response = await test.PostAsync(reservation.Id.ToString(CultureInfo.InvariantCulture), null);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(before, await test.SnapshotAsync());
    }

    [Fact]
    public async Task Confirmation_IgnoresForgedFieldsAndUsesOnlyTheFormReservationIdAndAuthenticatedActor()
    {
        await using var test = await PickupTest.CreateAsync();
        var selected = await test.SeedAsync();
        var other = await test.SeedAsync("Other pickup");
        var token = Token(ConfirmationForm(PickupCard(await test.Viewer.Client.GetStringAsync(Dashboard), selected.Id)));
        var fields = new Dictionary<string, string>
        {
            ["reservationId"] = selected.Id.ToString(CultureInfo.InvariantCulture), ["__RequestVerificationToken"] = token,
            ["UserId"] = test.OtherStudent.User!.Id, ["ActorUserId"] = test.Owner.User!.Id,
            ["StaffId"] = test.Owner.User.Id, ["Role"] = ApplicationRoles.Administrator,
            ["Status"] = "Canceled", ["ListingStatus"] = "Available", ["ListingId"] = other.ListingId.ToString(CultureInfo.InvariantCulture),
            ["FulfilledAtUtc"] = "2000-01-01T00:00:00Z", ["ExpiresAtUtc"] = "2099-01-01T00:00:00Z",
            ["CreatedAtUtc"] = "2000-01-01T00:00:00Z", ["Audit.Action"] = "Forged", ["Audit.Details"] = "Forged",
            ["Filter.ReservationId"] = "not-an-id", ["Filter.StudentEmail"] = new string('x', 257)
        };

        using var response = await test.Viewer.Client.PostAsync(ConfirmPath + "&reservationId=" + other.Id,
            new FormUrlEncodedContent(fields));

        AssertPrg(response, selected.Id);
        var saved = await test.ReadReservationAsync(selected.Id);
        Assert.Equal(ReservationStatus.Fulfilled, saved.Status);
        Assert.Equal(test.Owner.User.Id, saved.UserId);
        Assert.Equal(InitialNow.UtcDateTime, saved.FulfilledAtUtc);
        Assert.Equal(selected.CreatedAtUtc, saved.CreatedAtUtc);
        Assert.Equal(selected.ExpiresAtUtc, saved.ExpiresAtUtc);
        Assert.Equal(ReservationStatus.Active, (await test.ReadReservationAsync(other.Id)).Status);
        Assert.Equal(ListingStatus.Held, await test.ListingStatusAsync(other.ListingId));
        var audit = await test.ReadPickupAuditAsync();
        Assert.Equal(test.Viewer.User!.Id, audit.ActorUserId);
        Assert.Equal(selected.ListingId, audit.ListingId);
        Assert.DoesNotContain("Forged", audit.Details);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("not-an-id")]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("2147483648")]
    public async Task Confirmation_MalformedOrMissingFormIdCannotFallBackToTheQueryString(string? submittedId)
    {
        await using var test = await PickupTest.CreateAsync();
        var reservation = await test.SeedAsync();
        var token = Token(ConfirmationForm(PickupCard(await test.Viewer.Client.GetStringAsync(Dashboard), reservation.Id)));
        var before = await test.SnapshotAsync();

        using var response = await test.PostAsync(submittedId, token, "&reservationId=" + reservation.Id);

        AssertPrg(response);
        var html = await test.Viewer.Client.GetStringAsync(response.Headers.Location!);
        Assert.NotEmpty(Message(html));
        Assert.DoesNotContain("Pickup confirmed", Message(html));
        Assert.Equal(before, await test.SnapshotAsync());
    }

    [Theory]
    [InlineData("expired")]
    [InlineData("canceled")]
    [InlineData("missing")]
    [InlineData("unknown")]
    public async Task Confirmation_StaleOrUnknownReservationsReturnSafeFeedbackWithoutChanges(string scenario)
    {
        await using var test = await PickupTest.CreateAsync();
        var reservation = await test.SeedAsync();
        var token = Token(ConfirmationForm(PickupCard(await test.Viewer.Client.GetStringAsync(Dashboard), reservation.Id)));
        if (scenario == "expired") test.Clock.AdvanceTo(new DateTimeOffset(reservation.ExpiresAtUtc));
        if (scenario == "canceled") await test.MutateAsync(database => database.Reservations.Where(row => row.Id == reservation.Id)
            .ExecuteUpdateAsync(setters => setters.SetProperty(row => row.Status, ReservationStatus.Canceled)));
        if (scenario == "missing") await test.MutateAsync(database => database.Listings.Where(row => row.Id == reservation.ListingId)
            .ExecuteUpdateAsync(setters => setters.SetProperty(row => row.Status, ListingStatus.Missing)));
        var id = scenario == "unknown" ? int.MaxValue : reservation.Id;
        var before = await test.SnapshotAsync();

        using var response = await test.PostAsync(id.ToString(CultureInfo.InvariantCulture), token);

        AssertPrg(response, id);
        var html = await test.Viewer.Client.GetStringAsync(response.Headers.Location!);
        Assert.NotEmpty(Message(html));
        Assert.DoesNotContain("Pickup confirmed", Message(html));
        if (scenario == "expired") Assert.Contains("deadline", Message(html), StringComparison.OrdinalIgnoreCase);
        Assert.Equal(before, await test.SnapshotAsync());
    }

    [Fact]
    public async Task Confirmation_DuplicatePostAndRefreshDoNotRepeatFulfillmentOrAudit()
    {
        await using var test = await PickupTest.CreateAsync();
        var reservation = await test.SeedAsync();
        var token = Token(ConfirmationForm(PickupCard(await test.Viewer.Client.GetStringAsync(Dashboard), reservation.Id)));
        using var first = await test.PostAsync(reservation.Id.ToString(CultureInfo.InvariantCulture), token);
        AssertPrg(first, reservation.Id);
        Assert.Contains("Pickup confirmed", Message(await test.Viewer.Client.GetStringAsync(first.Headers.Location!)));
        var after = await test.SnapshotAsync();
        test.Clock.AdvanceTo(InitialNow.AddMinutes(1));

        using var duplicate = await test.PostAsync(reservation.Id.ToString(CultureInfo.InvariantCulture), token);

        AssertPrg(duplicate, reservation.Id);
        var html = await test.Viewer.Client.GetStringAsync(duplicate.Headers.Location!);
        Assert.DoesNotContain("Pickup confirmed", Message(html));
        Assert.NotEmpty(Message(html));
        await test.Viewer.Client.GetStringAsync(duplicate.Headers.Location!);
        Assert.Equal(after, await test.SnapshotAsync());
        Assert.Equal(InitialNow.UtcDateTime, (await test.ReadReservationAsync(reservation.Id)).FulfilledAtUtc);
    }

    [Fact]
    public async Task Confirmation_AuditFailureRollsBackAndNeverShowsSuccess()
    {
        await using var test = await PickupTest.CreateAsync();
        var reservation = await test.SeedAsync();
        var token = Token(ConfirmationForm(PickupCard(await test.Viewer.Client.GetStringAsync(Dashboard), reservation.Id)));
        await test.MutateAsync(database => database.Database.ExecuteSqlRawAsync(
            "CREATE TRIGGER fail_pickup_audit BEFORE INSERT ON AuditEvents BEGIN SELECT RAISE(ABORT, 'private audit failure'); END;"));
        var before = await test.SnapshotAsync();

        using var response = await test.PostAsync(reservation.Id.ToString(CultureInfo.InvariantCulture), token);

        AssertPrg(response, reservation.Id);
        var html = await test.Viewer.Client.GetStringAsync(response.Headers.Location!);
        Assert.DoesNotContain("Pickup confirmed", Message(html));
        Assert.DoesNotContain("private audit failure", html);
        Assert.NotEmpty(Message(html));
        Assert.Equal(before, await test.SnapshotAsync());
        Assert.Null((await test.ReadReservationAsync(reservation.Id)).FulfilledAtUtc);
    }

    [Fact]
    public async Task GetWithConfirmationHandlerCannotCollectAnOtherwiseEligiblePickup()
    {
        await using var test = await PickupTest.CreateAsync();
        var reservation = await test.SeedAsync();
        Assert.Single(ConfirmationForms(PickupCard(await test.Viewer.Client.GetStringAsync(Dashboard), reservation.Id)));
        var before = await test.SnapshotAsync();

        using var response = await test.Viewer.Client.GetAsync(ConfirmPath + "&reservationId=" + reservation.Id);

        Assert.Contains(response.StatusCode, new[] { HttpStatusCode.OK, HttpStatusCode.NotFound, HttpStatusCode.MethodNotAllowed });
        Assert.Equal(before, await test.SnapshotAsync());
        Assert.Equal(ReservationStatus.Active, (await test.ReadReservationAsync(reservation.Id)).Status);
        Assert.Single(ConfirmationForms(PickupCard(await test.Viewer.Client.GetStringAsync(Dashboard), reservation.Id)));
    }

    [Fact]
    public async Task LegacyFulfilledHistoryWithoutCollectionTimeDoesNotInventOneOrWriteDuringGet()
    {
        await using var test = await PickupTest.CreateAsync();
        var reservation = await test.SeedAsync();
        await test.MutateAsync(async database =>
        {
            await database.Reservations.Where(row => row.Id == reservation.Id)
                .ExecuteUpdateAsync(setters => setters.SetProperty(row => row.Status, ReservationStatus.Fulfilled));
            await database.Listings.Where(row => row.Id == reservation.ListingId)
                .ExecuteUpdateAsync(setters => setters.SetProperty(row => row.Status, ListingStatus.Collected));
        });
        var before = await test.SnapshotAsync();

        var card = AccountCard(await test.Owner.Client.GetStringAsync("/Account/Reservations"), reservation.Id);

        Assert.Contains("Fulfilled", card);
        Assert.Contains("Collection time was not recorded.", card);
        Assert.Equal(2, Tags(card, "time").Length); // Only the saved creation and deadline times exist.
        Assert.Null((await test.ReadReservationAsync(reservation.Id)).FulfilledAtUtc);
        Assert.Equal(before, await test.SnapshotAsync());
    }

    [Theory]
    [InlineData("expired")]
    [InlineData("fulfilled")]
    [InlineData("canceled")]
    [InlineData("missing")]
    [InlineData("metadata")]
    [InlineData("superseded")]
    public async Task Dashboard_HidesConfirmationForIneligibleRowsAndAllGetsRemainReadOnly(string scenario)
    {
        await using var test = await PickupTest.CreateAsync();
        var reservation = await test.SeedAsync();
        if (scenario == "expired") test.Clock.AdvanceTo(new DateTimeOffset(reservation.ExpiresAtUtc));
        await test.MutateAsync(async database =>
        {
            if (scenario == "fulfilled") await database.Reservations.Where(row => row.Id == reservation.Id)
                .ExecuteUpdateAsync(setters => setters.SetProperty(row => row.Status, ReservationStatus.Fulfilled));
            if (scenario == "canceled") await database.Reservations.Where(row => row.Id == reservation.Id)
                .ExecuteUpdateAsync(setters => setters.SetProperty(row => row.Status, ReservationStatus.Canceled));
            if (scenario == "missing") await database.Listings.Where(row => row.Id == reservation.ListingId)
                .ExecuteUpdateAsync(setters => setters.SetProperty(row => row.Status, ListingStatus.Missing));
            if (scenario == "metadata") await database.Reservations.Where(row => row.Id == reservation.Id)
                .ExecuteUpdateAsync(setters => setters.SetProperty(row => row.FulfilledAtUtc, InitialNow.AddMinutes(-1).UtcDateTime));
            if (scenario == "superseded")
            {
                database.Reservations.Add(new Reservation
                {
                    UserId = test.OtherStudent.User!.Id, ListingId = reservation.ListingId, Status = ReservationStatus.Canceled,
                    CreatedAtUtc = reservation.CreatedAtUtc.AddMinutes(1), ExpiresAtUtc = reservation.ExpiresAtUtc.AddMinutes(1)
                });
                await database.SaveChangesAsync();
            }
        });
        var before = await test.SnapshotAsync();

        var lookup = Dashboard + "?Filter.ReservationId=" + reservation.Id;
        var html = await test.Viewer.Client.GetStringAsync(lookup);
        Assert.Empty(ConfirmationForms(PickupCard(html, reservation.Id)));
        using var forgedGet = await test.Viewer.Client.GetAsync(lookup + "&handler=ConfirmPickup&reservationId=" + reservation.Id);
        Assert.Contains(forgedGet.StatusCode, new[] { HttpStatusCode.OK, HttpStatusCode.NotFound, HttpStatusCode.MethodNotAllowed });
        Assert.Equal(before, await test.SnapshotAsync());
    }

    private static void AssertRedirect(HttpResponseMessage response, string path)
    {
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.NotNull(response.Headers.Location);
        Assert.Equal(path, new Uri(new Uri("https://localhost"), response.Headers.Location).AbsolutePath);
    }

    private static void AssertPrg(HttpResponseMessage response, int? id = null)
    {
        AssertRedirect(response, Dashboard);
        if (id is not null)
        {
            var query = QueryHelpers.ParseQuery(new Uri(new Uri("https://localhost"), response.Headers.Location!).Query);
            Assert.Equal(id.Value.ToString(CultureInfo.InvariantCulture), query["Filter.ReservationId"].ToString());
        }
    }

    private static string[] Tags(string html, string tag)
        => Regex.Matches(html, $@"<{tag}\b[^>]*>", RegexOptions.IgnoreCase).Cast<Match>().Select(match => match.Value).ToArray();
    private static string Attribute(string tag, string name)
        => WebUtility.HtmlDecode(Regex.Match(tag, "\\b" + name + "=\"([^\"]*)\"", RegexOptions.IgnoreCase).Groups[1].Value);
    private static string InputValue(string form, string name)
        => Attribute(Assert.Single(Tags(form, "input"), input => Attribute(input, "name") == name), "value");
    private static string Token(string html)
    {
        var tokens = Tags(html, "input").Where(input => Attribute(input, "name") == "__RequestVerificationToken").ToArray();
        Assert.NotEmpty(tokens);
        return Attribute(tokens[0], "value");
    }
    private static string[] ConfirmationForms(string html)
        => Regex.Matches(html, @"<form\b[^>]*>[\s\S]*?</form>", RegexOptions.IgnoreCase).Cast<Match>()
            .Where(form => Attribute(form.Value, "action").Contains("handler=ConfirmPickup", StringComparison.OrdinalIgnoreCase))
            .Select(form => form.Value).ToArray();
    private static string ConfirmationForm(string html) => Assert.Single(ConfirmationForms(html));
    private static string PickupCard(string html, int id)
        => Card(html, "pickup-title-" + id);
    private static string AccountCard(string html, int id)
        => Card(html, "reservation-title-" + id);
    private static string Card(string html, string label)
        => Assert.Single(Regex.Matches(html, @"<article\b[^>]*>[\s\S]*?</article>", RegexOptions.IgnoreCase).Cast<Match>(),
            card => Attribute(card.Value, "aria-labelledby") == label).Value;
    private static string Text(string html)
        => Regex.Replace(WebUtility.HtmlDecode(Regex.Replace(html, "<[^>]+>", string.Empty)), @"\s+", " ").Trim();
    private static string Message(string html)
        => Text(Regex.Match(html, "<div\\b[^>]*id=\"pickup-message\"[^>]*>([\\s\\S]*?)</div>", RegexOptions.IgnoreCase).Groups[1].Value);

    private sealed record Session(HttpClient Client, ApplicationUser? User);
    private sealed class Clock : TimeProvider
    {
        private long ticks = InitialNow.UtcTicks;
        public void AdvanceTo(DateTimeOffset now) => Interlocked.Exchange(ref ticks, now.UtcTicks);
        public override DateTimeOffset GetUtcNow() => new(Interlocked.Read(ref ticks), TimeSpan.Zero);
    }

    private sealed class PickupTest(
        AuthorizationWebApplicationFactory original, WebApplicationFactory<ApplicationDbContext> factory,
        Session viewer, Session owner, Session otherStudent, Session anonymous, Clock clock) : IAsyncDisposable
    {
        public WebApplicationFactory<ApplicationDbContext> Factory { get; } = factory;
        public Session Viewer { get; } = viewer;
        public Session Owner { get; } = owner;
        public Session OtherStudent { get; } = otherStudent;
        public Session Anonymous { get; } = anonymous;
        public Clock Clock { get; } = clock;

        public static async Task<PickupTest> CreateAsync(string? role = ApplicationRoles.Staff)
        {
            var original = new AuthorizationWebApplicationFactory();
            var clock = new Clock();
            var factory = original.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
            {
                services.RemoveAll<TimeProvider>();
                services.AddSingleton<TimeProvider>(clock);
                services.PostConfigure<ReservationOptions>(options => options.CampusTimeZone = "America/Chicago");
            }));
            return new(original, factory, await CreateSessionAsync(factory, role, "Pickup operator"),
                await CreateSessionAsync(factory, ApplicationRoles.Student, "Pickup owner"),
                await CreateSessionAsync(factory, ApplicationRoles.Student, "Other pickup student"),
                await CreateSessionAsync(factory, null, "Anonymous"), clock);
        }

        private static async Task<Session> CreateSessionAsync(WebApplicationFactory<ApplicationDbContext> factory, string? role, string displayName)
        {
            var client = factory.CreateClient(new WebApplicationFactoryClientOptions
            {
                AllowAutoRedirect = false, BaseAddress = new Uri("https://localhost")
            });
            if (role is null) return new(client, null);
            await using var scope = factory.Services.CreateAsyncScope();
            var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            var email = IdentityTestContext.NewEmail();
            var user = new ApplicationUser { UserName = email, Email = email, DisplayName = displayName };
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

        public async Task<Reservation> SeedAsync(string title = "Confirmation test winter coat")
        {
            await using var scope = Factory.Services.CreateAsyncScope();
            var database = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var reservation = new Reservation
            {
                UserId = Owner.User!.Id, Status = ReservationStatus.Active,
                CreatedAtUtc = InitialNow.AddMinutes(-40).UtcDateTime, ExpiresAtUtc = InitialNow.AddMinutes(20).UtcDateTime,
                Listing = new Listing
                {
                    Title = title, Description = "Synthetic pickup confirmation fixture", Condition = "Good", CategoryId = 1,
                    Status = ListingStatus.Held, CreatedAtUtc = InitialNow.AddDays(-1).UtcDateTime
                }
            };
            database.Reservations.Add(reservation);
            await database.SaveChangesAsync();
            return reservation;
        }

        public Task<HttpResponseMessage> PostAsync(string? id, string? token, string extraQuery = "")
        {
            var fields = new Dictionary<string, string>();
            if (id is not null) fields["reservationId"] = id;
            if (token is not null) fields["__RequestVerificationToken"] = token;
            return Viewer.Client.PostAsync(ConfirmPath + extraQuery, new FormUrlEncodedContent(fields));
        }

        public async Task MutateAsync(Func<ApplicationDbContext, Task> operation)
        {
            await using var scope = Factory.Services.CreateAsyncScope();
            await operation(scope.ServiceProvider.GetRequiredService<ApplicationDbContext>());
        }
        public async Task<Reservation> ReadReservationAsync(int id)
        {
            await using var scope = Factory.Services.CreateAsyncScope();
            return await scope.ServiceProvider.GetRequiredService<ApplicationDbContext>().Reservations.AsNoTracking().SingleAsync(row => row.Id == id);
        }
        public async Task<ListingStatus> ListingStatusAsync(int id)
        {
            await using var scope = Factory.Services.CreateAsyncScope();
            return await scope.ServiceProvider.GetRequiredService<ApplicationDbContext>().Listings.Where(row => row.Id == id).Select(row => row.Status).SingleAsync();
        }
        public async Task<AuditEvent> ReadPickupAuditAsync()
        {
            await using var scope = Factory.Services.CreateAsyncScope();
            return await scope.ServiceProvider.GetRequiredService<ApplicationDbContext>().AuditEvents.AsNoTracking().SingleAsync();
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
            Viewer.Client.Dispose();
            Owner.Client.Dispose();
            OtherStudent.Client.Dispose();
            Anonymous.Client.Dispose();
            await Factory.DisposeAsync();
            await original.DisposeAsync();
        }
    }
}

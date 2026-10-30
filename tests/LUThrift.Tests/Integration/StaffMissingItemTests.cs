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

public sealed class StaffMissingItemTests
{
    private const string Dashboard = "/Staff/Pickups";
    private const string MissingPath = Dashboard + "?handler=ReportMissing";
    private static readonly DateTimeOffset InitialNow = new(2026, 10, 29, 15, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(ApplicationRoles.Staff)]
    [InlineData(ApplicationRoles.Administrator)]
    public async Task ReportMissing_CancelsOnlyTheSelectedHoldAndShowsTheOwnersControlledReason(string role)
    {
        await using var test = await MissingTest.CreateAsync(role);
        var selected = await test.SeedAsync();
        var unaffected = await test.SeedAsync("Still held item");
        var before = await test.SnapshotAsync();
        var card = PickupCard(await test.Viewer.Client.GetStringAsync(Dashboard), selected.Id);
        var form = MissingForm(card);
        Assert.Equal("post", Attribute(form, "method"));
        Assert.Equal(MissingPath, Attribute(form, "action"));
        Assert.Equal(new[] { "__RequestVerificationToken", "reservationId" },
            Tags(form, "input").Select(input => Attribute(input, "name")).OrderBy(name => name, StringComparer.Ordinal));
        Assert.Contains("Report item missing", Text(form));
        Assert.Contains(selected.Listing.Title, WebUtility.HtmlDecode(card));
        Assert.Contains(test.Owner.User!.Email!, card);
        Assert.Equal(selected.Id.ToString(CultureInfo.InvariantCulture), InputValue(form, "reservationId"));
        Assert.Contains("Active reservations: 2 of 3", await test.Owner.Client.GetStringAsync("/Account/Reservations"));
        Assert.Equal(before, await test.SnapshotAsync());

        using var response = await test.PostAsync(selected.Id.ToString(CultureInfo.InvariantCulture), Token(form));

        AssertPrg(response, selected.Id);
        var saved = await test.ReadReservationAsync(selected.Id);
        Assert.Equal(ReservationStatus.Canceled, saved.Status);
        Assert.Equal(ReservationCancellationReasons.ItemNotFound, saved.CancellationReason);
        Assert.Null(saved.FulfilledAtUtc);
        Assert.Equal(selected.UserId, saved.UserId);
        Assert.Equal(selected.CreatedAtUtc, saved.CreatedAtUtc);
        Assert.Equal(selected.ExpiresAtUtc, saved.ExpiresAtUtc);
        Assert.Equal(ListingStatus.Missing, await test.ListingStatusAsync(selected.ListingId));
        Assert.Equal(ReservationStatus.Active, (await test.ReadReservationAsync(unaffected.Id)).Status);
        Assert.Equal(ListingStatus.Held, await test.ListingStatusAsync(unaffected.ListingId));
        var audit = await test.ReadMissingAuditAsync();
        Assert.Equal(InventoryAuditActions.ReservationItemMissing, audit.Action);
        Assert.Equal(test.Viewer.User!.Id, audit.ActorUserId);
        Assert.Equal(selected.ListingId, audit.ListingId);
        Assert.Equal(InitialNow.UtcDateTime, audit.OccurredAtUtc);
        Assert.Contains($"Reservation {selected.Id}", audit.Details);
        Assert.Contains("Canceled", audit.Details);
        Assert.Contains("Missing", audit.Details);
        var after = await test.SnapshotAsync();
        var redirected = await test.Viewer.Client.GetStringAsync(response.Headers.Location!);
        Assert.Contains("reported missing", Message(redirected), StringComparison.OrdinalIgnoreCase);
        Assert.Empty(MissingForms(PickupCard(redirected, selected.Id)));
        Assert.Equal(string.Empty, Message(await test.Viewer.Client.GetStringAsync(response.Headers.Location!)));
        var history = await test.Owner.Client.GetStringAsync("/Account/Reservations");
        Assert.Contains("Active reservations: 1 of 3", history);
        var historySection = Regex.Match(history, "<section[^>]*id=\"reservation-history\"[^>]*>([\\s\\S]*?)</section>").Groups[1].Value;
        var historyCard = AccountCard(historySection, selected.Id);
        Assert.Contains("Canceled", historyCard);
        Assert.Contains("Cancellation reason", historyCard);
        Assert.Contains(ReservationCancellationReasons.ItemNotFound, Text(historyCard));
        Assert.DoesNotContain(audit.Details!, Text(historyCard));
        Assert.DoesNotContain(audit.ActorUserId, historyCard);
        Assert.Empty(Tags(historyCard, "form"));
        var other = await test.OtherStudent.Client.GetStringAsync("/Account/Reservations?UserId=" + test.Owner.User.Id);
        Assert.DoesNotContain(selected.Listing.Title, WebUtility.HtmlDecode(other));
        Assert.DoesNotContain(ReservationCancellationReasons.ItemNotFound, Text(other));
        foreach (var availability in new[] { "Available", "Reserved", "All" })
        {
            var html = await test.Anonymous.Client.GetStringAsync("/Thrift?Filter.Availability=" + availability);
            Assert.DoesNotContain(selected.Listing.Title, WebUtility.HtmlDecode(html));
        }
        using var details = await test.Anonymous.Client.GetAsync("/Thrift/Details/" + selected.ListingId);
        Assert.Equal(HttpStatusCode.NotFound, details.StatusCode);
        Assert.Equal(after, await test.SnapshotAsync());
        var reserveToken = Token(other);
        using var reserve = await test.OtherStudent.Client.PostAsync("/Account/Reserve", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["ListingId"] = selected.ListingId.ToString(CultureInfo.InvariantCulture), ["__RequestVerificationToken"] = reserveToken
        }));
        AssertRedirect(reserve, "/Account/Reservations");
        var denied = await test.OtherStudent.Client.GetStringAsync(reserve.Headers.Location!);
        Assert.Contains("not available", WebUtility.HtmlDecode(denied), StringComparison.OrdinalIgnoreCase);
        Assert.Equal(after, await test.SnapshotAsync());
    }

    [Theory]
    [InlineData(null, "/Identity/Account/Login")]
    [InlineData(ApplicationRoles.Student, "/Identity/Account/AccessDenied")]
    public async Task ReportMissing_AnonymousAndStudentRequestsCannotMutateHolds(string? role, string target)
    {
        await using var test = await MissingTest.CreateAsync(role);
        var selected = await test.SeedAsync();
        var token = Token(await test.Viewer.Client.GetStringAsync(role is null ? "/Identity/Account/Login" : "/Account/Reservations"));
        var before = await test.SnapshotAsync();

        using var response = await test.PostAsync(selected.Id.ToString(CultureInfo.InvariantCulture), token);

        AssertRedirect(response, target);
        Assert.Equal(before, await test.SnapshotAsync());
    }

    [Theory]
    [InlineData(ApplicationRoles.Staff, true)]
    [InlineData(ApplicationRoles.Staff, false)]
    [InlineData(ApplicationRoles.Administrator, true)]
    [InlineData(ApplicationRoles.Administrator, false)]
    public async Task ReportMissing_RechecksInactiveOrRevokedStaffAfterLoadingTheForm(string role, bool revoke)
    {
        await using var test = await MissingTest.CreateAsync(role);
        var selected = await test.SeedAsync();
        var token = Token(MissingForm(PickupCard(await test.Viewer.Client.GetStringAsync(Dashboard), selected.Id)));
        await test.MutateAsync(async database =>
        {
            if (revoke) await database.UserRoles.Where(row => row.UserId == test.Viewer.User!.Id).ExecuteDeleteAsync();
            else await database.Users.Where(row => row.Id == test.Viewer.User!.Id)
                .ExecuteUpdateAsync(setters => setters.SetProperty(row => row.IsActive, false));
        });
        var before = await test.SnapshotAsync();

        using var response = await test.PostAsync(selected.Id.ToString(CultureInfo.InvariantCulture), token);

        AssertRedirect(response, "/Identity/Account/AccessDenied");
        Assert.Equal(before, await test.SnapshotAsync());
    }

    [Fact]
    public async Task ReportMissing_RequiresAntiforgeryToken()
    {
        await using var test = await MissingTest.CreateAsync();
        var selected = await test.SeedAsync();
        await test.Viewer.Client.GetStringAsync(Dashboard);
        var before = await test.SnapshotAsync();

        using var response = await test.PostAsync(selected.Id.ToString(CultureInfo.InvariantCulture), null);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(before, await test.SnapshotAsync());
    }

    [Fact]
    public async Task ReportMissing_IgnoresForgedReasonIdentityStateAndQueryId()
    {
        await using var test = await MissingTest.CreateAsync();
        var selected = await test.SeedAsync();
        var other = await test.SeedAsync("Another held item");
        var token = Token(MissingForm(PickupCard(await test.Viewer.Client.GetStringAsync(Dashboard), selected.Id)));
        using var response = await test.Viewer.Client.PostAsync(MissingPath + "&reservationId=" + other.Id,
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["reservationId"] = selected.Id.ToString(CultureInfo.InvariantCulture), ["__RequestVerificationToken"] = token,
                ["UserId"] = test.OtherStudent.User!.Id, ["ActorUserId"] = test.Owner.User!.Id,
                ["StaffId"] = test.Owner.User.Id, ["Role"] = ApplicationRoles.Administrator,
                ["Status"] = "Active", ["ListingStatus"] = "Available", ["ListingId"] = other.ListingId.ToString(CultureInfo.InvariantCulture),
                ["CancellationReason"] = "<script>private forged reason</script>", ["FulfilledAtUtc"] = "2000-01-01T00:00:00Z",
                ["ExpiresAtUtc"] = "2099-01-01T00:00:00Z", ["Audit.Details"] = "Forged internal note",
                ["Filter.ReservationId"] = "invalid", ["Filter.StudentEmail"] = new string('x', 257)
            }));

        AssertPrg(response, selected.Id);
        var saved = await test.ReadReservationAsync(selected.Id);
        Assert.Equal(ReservationStatus.Canceled, saved.Status);
        Assert.Equal(test.Owner.User.Id, saved.UserId);
        Assert.Equal(ReservationCancellationReasons.ItemNotFound, saved.CancellationReason);
        Assert.Null(saved.FulfilledAtUtc);
        Assert.Equal(selected.ExpiresAtUtc, saved.ExpiresAtUtc);
        Assert.Equal(ReservationStatus.Active, (await test.ReadReservationAsync(other.Id)).Status);
        Assert.Equal(ListingStatus.Held, await test.ListingStatusAsync(other.ListingId));
        var audit = await test.ReadMissingAuditAsync();
        Assert.Equal(test.Viewer.User!.Id, audit.ActorUserId);
        Assert.DoesNotContain("Forged", audit.Details);
        var history = await test.Owner.Client.GetStringAsync("/Account/Reservations");
        Assert.DoesNotContain("private forged reason", history);
        Assert.DoesNotContain("Forged internal note", history);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("not-an-id")]
    [InlineData("2147483648")]
    public async Task ReportMissing_InvalidBodyIdCannotFallBackToQueryId(string? id)
    {
        await using var test = await MissingTest.CreateAsync();
        var selected = await test.SeedAsync();
        var token = Token(MissingForm(PickupCard(await test.Viewer.Client.GetStringAsync(Dashboard), selected.Id)));
        var before = await test.SnapshotAsync();

        using var response = await test.PostAsync(id, token, "&reservationId=" + selected.Id);

        AssertPrg(response);
        Assert.NotEmpty(Message(await test.Viewer.Client.GetStringAsync(response.Headers.Location!)));
        Assert.Equal(before, await test.SnapshotAsync());
    }

    [Theory]
    [InlineData("expired")]
    [InlineData("canceled")]
    [InlineData("fulfilled")]
    [InlineData("missing")]
    [InlineData("unknown")]
    public async Task ReportMissing_StaleAndUnknownRecordsReturnFeedbackWithoutWrites(string scenario)
    {
        await using var test = await MissingTest.CreateAsync();
        var selected = await test.SeedAsync();
        var token = Token(MissingForm(PickupCard(await test.Viewer.Client.GetStringAsync(Dashboard), selected.Id)));
        if (scenario == "expired") test.Clock.AdvanceTo(new DateTimeOffset(selected.ExpiresAtUtc));
        await test.MutateAsync(async database =>
        {
            if (scenario == "canceled") await database.Reservations.Where(row => row.Id == selected.Id)
                .ExecuteUpdateAsync(setters => setters.SetProperty(row => row.Status, ReservationStatus.Canceled));
            if (scenario == "fulfilled") await database.Reservations.Where(row => row.Id == selected.Id)
                .ExecuteUpdateAsync(setters => setters.SetProperty(row => row.Status, ReservationStatus.Fulfilled));
            if (scenario == "missing") await database.Listings.Where(row => row.Id == selected.ListingId)
                .ExecuteUpdateAsync(setters => setters.SetProperty(row => row.Status, ListingStatus.Missing));
        });
        var id = scenario == "unknown" ? int.MaxValue : selected.Id;
        var before = await test.SnapshotAsync();

        using var response = await test.PostAsync(id.ToString(CultureInfo.InvariantCulture), token);

        AssertPrg(response, id);
        var html = await test.Viewer.Client.GetStringAsync(response.Headers.Location!);
        Assert.NotEmpty(Message(html));
        Assert.DoesNotContain("Item reported missing.", Message(html));
        if (scenario != "unknown") Assert.Empty(MissingForms(PickupCard(html, selected.Id)));
        Assert.Equal(before, await test.SnapshotAsync());
    }

    [Fact]
    public async Task ReportMissing_DuplicateRequestAndOldStudentCancellationCannotReleaseMissingInventory()
    {
        await using var test = await MissingTest.CreateAsync();
        var selected = await test.SeedAsync();
        var cancelToken = Token(await test.Owner.Client.GetStringAsync("/Account/Reservations"));
        var token = Token(MissingForm(PickupCard(await test.Viewer.Client.GetStringAsync(Dashboard), selected.Id)));
        using var first = await test.PostAsync(selected.Id.ToString(CultureInfo.InvariantCulture), token);
        AssertPrg(first, selected.Id);
        Assert.Contains("reported missing", Message(await test.Viewer.Client.GetStringAsync(first.Headers.Location!)));
        var after = await test.SnapshotAsync();

        using var duplicate = await test.PostAsync(selected.Id.ToString(CultureInfo.InvariantCulture), token);

        AssertPrg(duplicate, selected.Id);
        var html = await test.Viewer.Client.GetStringAsync(duplicate.Headers.Location!);
        Assert.DoesNotContain("Item reported missing.", Message(html));
        Assert.NotEmpty(Message(html));
        await test.Viewer.Client.GetStringAsync(duplicate.Headers.Location!);
        using var canceled = await test.Owner.Client.PostAsync("/Account/Reservations?handler=Cancel",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["id"] = selected.Id.ToString(CultureInfo.InvariantCulture), ["__RequestVerificationToken"] = cancelToken
            }));
        AssertRedirect(canceled, "/Account/Reservations");
        await test.Owner.Client.GetStringAsync(canceled.Headers.Location!);
        Assert.Equal(after, await test.SnapshotAsync());
        Assert.Equal(ListingStatus.Missing, await test.ListingStatusAsync(selected.ListingId));
    }

    [Fact]
    public async Task ReportMissing_AnOldFormCannotChangeTheReplacementHold()
    {
        await using var test = await MissingTest.CreateAsync();
        var old = await test.SeedAsync();
        var token = Token(MissingForm(PickupCard(await test.Viewer.Client.GetStringAsync(Dashboard), old.Id)));
        await test.MutateAsync(async database =>
        {
            await database.Reservations.Where(row => row.Id == old.Id)
                .ExecuteUpdateAsync(setters => setters.SetProperty(row => row.Status, ReservationStatus.Canceled));
            database.Reservations.Add(new Reservation
            {
                UserId = test.OtherStudent.User!.Id, ListingId = old.ListingId, Status = ReservationStatus.Active,
                CreatedAtUtc = InitialNow.UtcDateTime, ExpiresAtUtc = InitialNow.AddHours(1).UtcDateTime
            });
            await database.SaveChangesAsync();
        });
        var before = await test.SnapshotAsync();

        using var response = await test.PostAsync(old.Id.ToString(CultureInfo.InvariantCulture), token);

        AssertPrg(response, old.Id);
        Assert.Equal(before, await test.SnapshotAsync());
        Assert.Equal(ListingStatus.Held, await test.ListingStatusAsync(old.ListingId));
    }

    [Fact]
    public async Task ReportMissing_AuditFailureRollsBackAndDoesNotReportSuccess()
    {
        await using var test = await MissingTest.CreateAsync();
        var selected = await test.SeedAsync();
        var token = Token(MissingForm(PickupCard(await test.Viewer.Client.GetStringAsync(Dashboard), selected.Id)));
        await test.MutateAsync(database => database.Database.ExecuteSqlRawAsync(
            "CREATE TRIGGER fail_missing_audit BEFORE INSERT ON AuditEvents BEGIN SELECT RAISE(ABORT, 'private missing audit failure'); END;"));
        var before = await test.SnapshotAsync();

        using var response = await test.PostAsync(selected.Id.ToString(CultureInfo.InvariantCulture), token);

        AssertPrg(response, selected.Id);
        var html = await test.Viewer.Client.GetStringAsync(response.Headers.Location!);
        Assert.NotEmpty(Message(html));
        Assert.DoesNotContain("Item reported missing.", Message(html));
        Assert.DoesNotContain("private missing audit failure", html);
        Assert.Equal(before, await test.SnapshotAsync());
    }

    [Fact]
    public async Task ReportMissing_GetRequestsNeverMutateEvenAnEligibleHold()
    {
        await using var test = await MissingTest.CreateAsync();
        var selected = await test.SeedAsync();
        var before = await test.SnapshotAsync();

        using var response = await test.Viewer.Client.GetAsync(MissingPath + "&reservationId=" + selected.Id);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Single(MissingForms(PickupCard(await response.Content.ReadAsStringAsync(), selected.Id)));
        Assert.Equal(before, await test.SnapshotAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StudentHistory_NeverDisplaysLegacyInternalCancellationTextOrAuditDetails(bool recognizedButActive)
    {
        await using var test = await MissingTest.CreateAsync();
        var selected = await test.SeedAsync();
        const string privateNote = "<script>private audit note with staff-only details</script>";
        await test.MutateAsync(async database =>
        {
            await database.Reservations.Where(row => row.Id == selected.Id).ExecuteUpdateAsync(setters => setters
                .SetProperty(row => row.Status, recognizedButActive ? ReservationStatus.Active : ReservationStatus.Canceled)
                .SetProperty(row => row.CancellationReason, recognizedButActive ? ReservationCancellationReasons.ItemNotFound : privateNote));
            database.AuditEvents.Add(new AuditEvent
            {
                ListingId = selected.ListingId, ActorUserId = test.Viewer.User!.Id,
                Action = InventoryAuditActions.ReservationItemMissing, OccurredAtUtc = InitialNow.UtcDateTime, Details = privateNote
            });
            await database.SaveChangesAsync();
        });
        var before = await test.SnapshotAsync();

        var html = await test.Owner.Client.GetStringAsync("/Account/Reservations");

        Assert.DoesNotContain("private audit note", WebUtility.HtmlDecode(html));
        Assert.DoesNotContain(ReservationCancellationReasons.ItemNotFound, Text(html));
        Assert.DoesNotContain("Cancellation reason", AccountCard(html, selected.Id));
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
    private static string[] MissingForms(string html)
        => Regex.Matches(html, @"<form\b[^>]*>[\s\S]*?</form>", RegexOptions.IgnoreCase).Cast<Match>()
            .Where(form => Attribute(form.Value, "action").Contains("handler=ReportMissing", StringComparison.OrdinalIgnoreCase))
            .Select(form => form.Value).ToArray();
    private static string MissingForm(string html) => Assert.Single(MissingForms(html));
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

    private sealed class MissingTest(
        AuthorizationWebApplicationFactory original, WebApplicationFactory<ApplicationDbContext> factory,
        Session viewer, Session owner, Session otherStudent, Session anonymous, Clock clock) : IAsyncDisposable
    {
        public WebApplicationFactory<ApplicationDbContext> Factory { get; } = factory;
        public Session Viewer { get; } = viewer;
        public Session Owner { get; } = owner;
        public Session OtherStudent { get; } = otherStudent;
        public Session Anonymous { get; } = anonymous;
        public Clock Clock { get; } = clock;

        public static async Task<MissingTest> CreateAsync(string? role = ApplicationRoles.Staff)
        {
            var original = new AuthorizationWebApplicationFactory();
            var clock = new Clock();
            var factory = original.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
            {
                services.RemoveAll<TimeProvider>();
                services.AddSingleton<TimeProvider>(clock);
                services.PostConfigure<ReservationOptions>(options =>
                {
                    // Explicitly synthetic hours allow a real reserve POST to reject Missing status.
                    options.CampusTimeZone = "America/Chicago";
                    options.WeeklyPickupIntervals = [new() { Day = "Thursday", OpensAt = "09:00", ClosesAt = "17:00" }];
                });
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

        public async Task<Reservation> SeedAsync(string title = "Missing test winter coat")
        {
            await using var scope = Factory.Services.CreateAsyncScope();
            var database = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var reservation = new Reservation
            {
                UserId = Owner.User!.Id, Status = ReservationStatus.Active,
                CreatedAtUtc = InitialNow.AddMinutes(-40).UtcDateTime, ExpiresAtUtc = InitialNow.AddMinutes(20).UtcDateTime,
                Listing = new Listing
                {
                    Title = title, Description = "Synthetic missing-item fixture", Condition = "Good", CategoryId = 1,
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
            return Viewer.Client.PostAsync(MissingPath + extraQuery, new FormUrlEncodedContent(fields));
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
        public async Task<AuditEvent> ReadMissingAuditAsync()
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

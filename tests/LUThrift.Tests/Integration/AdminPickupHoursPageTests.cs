using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using LUThrift.Web.Authorization;
using LUThrift.Web.Configuration;
using LUThrift.Web.Data;
using LUThrift.Web.Models;
using LUThrift.Web.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace LUThrift.Tests.Integration;

public sealed class AdminPickupHoursPageTests
{
    private const string PagePath = "/Admin/PickupHours";
    private const string ImportPath = PagePath + "?handler=Import";

    [Theory]
    [InlineData(null, "/Identity/Account/Login")]
    [InlineData(ApplicationRoles.Student, "/Identity/Account/AccessDenied")]
    [InlineData(ApplicationRoles.Staff, "/Identity/Account/AccessDenied")]
    public async Task NonAdministrator_CannotReadOrPostHours(string? role, string redirect)
    {
        using var factory = new AuthorizationWebApplicationFactory();
        using var client = await factory.CreateIdentityClientAsync(role);
        var before = await SnapshotAsync(factory);
        using var get = await client.GetAsync(PagePath);
        AssertRedirect(client, get, redirect);
        foreach (var path in new[] { PagePath, ImportPath })
        {
            using var post = await client.PostAsync(path, new FormUrlEncodedContent(Fields()));
            AssertRedirect(client, post, redirect);
        }
        Assert.Equal(before, await SnapshotAsync(factory));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OldAdministratorCookie_LosesReadAndWriteAccessAfterRoleRemovalOrDeactivation(bool deactivate)
    {
        using var factory = new AuthorizationWebApplicationFactory();
        using var client = await factory.CreateIdentityClientAsync(ApplicationRoles.Administrator);
        var token = Token(await client.GetStringAsync(PagePath));
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var database = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var id = await database.Users.Select(user => user.Id).SingleAsync();
            if (deactivate)
                await database.Users.Where(user => user.Id == id).ExecuteUpdateAsync(setters => setters.SetProperty(user => user.IsActive, false));
            else
                await database.UserRoles.Where(row => row.UserId == id).ExecuteDeleteAsync();
        }
        var before = await SnapshotAsync(factory);
        using var get = await client.GetAsync(PagePath);
        AssertRedirect(client, get, "/Identity/Account/AccessDenied");
        foreach (var path in new[] { PagePath, ImportPath })
        {
            using var post = await client.PostAsync(path, new FormUrlEncodedContent(Fields(token)));
            AssertRedirect(client, post, "/Identity/Account/AccessDenied");
        }
        Assert.Equal(before, await SnapshotAsync(factory));
    }

    [Fact]
    public async Task Save_UsesVersionedFormPrgServerAuditAndReadOnlyGets()
    {
        using var factory = new AuthorizationWebApplicationFactory();
        using var client = await factory.CreateIdentityClientAsync(ApplicationRoles.Administrator);
        var before = await SnapshotAsync(factory);
        using var page = await client.GetAsync(PagePath);
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        Assert.True(page.Headers.CacheControl?.NoStore);
        var html = await page.Content.ReadAsStringAsync();
        Assert.Contains("Not saved yet", html);
        Assert.Contains("Reservations unavailable: no opening hours", html);
        Assert.Equal(before, await SnapshotAsync(factory));
        Assert.Contains(PagePath, await client.GetStringAsync("/Admin"));
        var form = Fields(Token(html));
        form["Role"] = ApplicationRoles.Student;
        form["ActorUserId"] = "forged-actor";
        form["OccurredAtUtc"] = "1900-01-01T00:00:00Z";
        form["DurationMinutes"] = "999";
        form["MaximumActiveReservations"] = "99";
        using var response = await client.PostAsync(PagePath, new FormUrlEncodedContent(form));
        AssertRedirect(client, response, PagePath);

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var database = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var saved = await scope.ServiceProvider.GetRequiredService<PickupScheduleService>().GetAsync();
            Assert.NotNull(saved.Version);
            Assert.Equal("America/Chicago", saved.CampusTimeZone);
            Assert.Equal(new[] { "09:00", "13:00" }, saved.WeeklyPickupIntervals.Select(interval => interval.OpensAt));
            Assert.Equal(new[] { "2026-12-25" }, saved.ClosedDates);
            var audit = await database.AdministrativeAuditEvents.SingleAsync();
            Assert.Equal(await database.Users.Select(user => user.Id).SingleAsync(), audit.ActorUserId);
            Assert.Equal(AdministrativeAuditActions.PickupScheduleSaved, audit.Action);
            Assert.Equal(PickupSchedule.SingletonId, audit.TargetScheduleId);
            Assert.True(audit.OccurredAtUtc.Year > 1900);
            Assert.Equal(TimeSpan.FromHours(1), ReservationPolicy.Duration);
            Assert.Equal(3, ReservationPolicy.MaximumActiveReservations);
        }
        var after = await SnapshotAsync(factory);
        var refreshed = await client.GetStringAsync(PagePath);
        Assert.Contains("Pickup schedule saved.", refreshed);
        Assert.Contains("saved-schedule-version", refreshed);
        Assert.NotEqual(string.Empty, InputValue(refreshed, "Input.ExpectedVersion"));
        Assert.Equal(after, await SnapshotAsync(factory));
    }

    [Theory]
    [InlineData("invalid-time")]
    [InlineData("invalid-date")]
    [InlineData("overlap")]
    [InlineData("timezone")]
    [InlineData("row-format")]
    [InlineData("interval-count")]
    [InlineData("closed-date-count")]
    [InlineData("field-length")]
    [InlineData("version")]
    public async Task InvalidOrOversizedInput_RedisplaysHelpfulValidationWithoutWriting(string invalid)
    {
        using var factory = new AuthorizationWebApplicationFactory();
        using var client = await factory.CreateIdentityClientAsync(ApplicationRoles.Administrator);
        var fields = Fields(Token(await client.GetStringAsync(PagePath)));
        switch (invalid)
        {
            case "invalid-time": fields["Input.WeeklyIntervalsText"] = "Monday, 25:00, 26:00"; break;
            case "invalid-date": fields["Input.ClosedDatesText"] = "2026-02-30"; break;
            case "overlap": fields["Input.WeeklyIntervalsText"] = "Monday, 09:00, 12:00\nMonday, 11:00, 15:00"; break;
            case "timezone": fields["Input.CampusTimeZone"] = "Not/AZone"; break;
            case "row-format": fields["Input.WeeklyIntervalsText"] = "Monday 09:00 12:00"; break;
            case "interval-count": fields["Input.WeeklyIntervalsText"] = string.Join('\n', Enumerable.Repeat("Monday, 09:00, 12:00", 43)); break;
            case "closed-date-count": fields["Input.ClosedDatesText"] = string.Join("\r\n", Enumerable.Repeat("2026-12-25", 367)); break;
            case "field-length": fields["Input.WeeklyIntervalsText"] = new string('x', 2049); break;
            case "version": fields["Input.ExpectedVersion"] = "not-a-guid"; break;
        }
        var before = await SnapshotAsync(factory);
        using var response = await client.PostAsync(PagePath, new FormUrlEncodedContent(fields));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var html = await response.Content.ReadAsStringAsync();
        Assert.Contains("validation-summary-errors", html);
        Assert.Contains("Save pickup hours", html);
        Assert.Equal(fields["Input.CampusTimeZone"], InputValue(html, "Input.CampusTimeZone"));
        Assert.Equal(before, await SnapshotAsync(factory));
    }

    [Fact]
    public async Task MaximumClosedDates_AcceptsNormalBrowserCrLfLineEndings()
    {
        using var factory = new AuthorizationWebApplicationFactory();
        using var client = await factory.CreateIdentityClientAsync(ApplicationRoles.Administrator);
        var fields = Fields(Token(await client.GetStringAsync(PagePath)));
        fields["Input.ClosedDatesText"] = string.Join("\r\n", Enumerable.Range(0, 366)
            .Select(offset => new DateOnly(2028, 1, 1).AddDays(offset).ToString("yyyy-MM-dd"))) + "\r\n";

        using var response = await client.PostAsync(PagePath, new FormUrlEncodedContent(fields));

        AssertRedirect(client, response, PagePath);
        await using var scope = factory.Services.CreateAsyncScope();
        var saved = await scope.ServiceProvider.GetRequiredService<PickupScheduleService>().GetAsync();
        Assert.Equal(366, saved.ClosedDates.Count);
        Assert.Equal("2028-01-01", saved.ClosedDates[0]);
        Assert.Equal("2028-12-31", saved.ClosedDates[^1]);
    }

    [Fact]
    public async Task StaleForm_ReturnsConflictAndKeepsWinningScheduleAndAudit()
    {
        using var factory = new AuthorizationWebApplicationFactory();
        using var client = await factory.CreateIdentityClientAsync(ApplicationRoles.Administrator);
        var firstPage = await client.GetStringAsync(PagePath);
        var staleForm = Fields(Token(firstPage));
        using var initial = await client.PostAsync(PagePath, new FormUrlEncodedContent(staleForm));
        AssertRedirect(client, initial, PagePath);
        var current = await client.GetStringAsync(PagePath);
        var form = Fields(Token(current), InputValue(current, "Input.ExpectedVersion"));
        form["Input.WeeklyIntervalsText"] = "Tuesday, 11:00, 16:00";
        using var saved = await client.PostAsync(PagePath, new FormUrlEncodedContent(form));
        AssertRedirect(client, saved, PagePath);
        var before = await SnapshotAsync(factory);
        using var stale = await client.PostAsync(PagePath, new FormUrlEncodedContent(staleForm));
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        var html = await stale.Content.ReadAsStringAsync();
        Assert.Contains("refresh the saved schedule", html);
        Assert.Contains("Tuesday", html);
        Assert.Equal(string.Empty, InputValue(html, "Input.ExpectedVersion"));
        Assert.Equal(before, await SnapshotAsync(factory));
    }

    [Fact]
    public async Task ClearingHours_IsExplicitVersionedSaveAndPreservesHistory()
    {
        using var factory = new AuthorizationWebApplicationFactory();
        using var client = await factory.CreateIdentityClientAsync(ApplicationRoles.Administrator);
        using var initial = await client.PostAsync(PagePath, new FormUrlEncodedContent(Fields(Token(await client.GetStringAsync(PagePath)))));
        AssertRedirect(client, initial, PagePath);
        var html = await client.GetStringAsync(PagePath);
        var empty = Fields(Token(html), InputValue(html, "Input.ExpectedVersion"));
        empty["Input.WeeklyIntervalsText"] = " \n ";
        empty["Input.ClosedDatesText"] = string.Empty;
        using var saved = await client.PostAsync(PagePath, new FormUrlEncodedContent(empty));
        AssertRedirect(client, saved, PagePath);
        Assert.Contains("Reservations unavailable: no opening hours", await client.GetStringAsync(PagePath));
        await using var scope = factory.Services.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        Assert.Empty(await database.PickupScheduleIntervals.ToListAsync());
        Assert.Equal(2, await database.AdministrativeAuditEvents.CountAsync());
    }

    [Theory]
    [InlineData(PagePath)]
    [InlineData(ImportPath)]
    public async Task MissingAntiforgeryToken_RejectsSaveAndImport(string path)
    {
        using var factory = new AuthorizationWebApplicationFactory();
        using var client = await factory.CreateIdentityClientAsync(ApplicationRoles.Administrator);
        var before = await SnapshotAsync(factory);
        using var post = await client.PostAsync(path, new FormUrlEncodedContent(Fields()));
        Assert.Equal(HttpStatusCode.BadRequest, post.StatusCode);
        Assert.Equal(before, await SnapshotAsync(factory));
    }

    [Fact]
    public async Task ExplicitImport_UsesReviewedServerSettingsNotSubmittedValuesAndNeverOverwrites()
    {
        using var factory = new AuthorizationWebApplicationFactory();
        using var client = await factory.CreateIdentityClientAsync(ApplicationRoles.Administrator);
        var options = factory.Services.GetRequiredService<IOptions<ReservationOptions>>().Value;
        options.CampusTimeZone = "America/New_York";
        options.WeeklyPickupIntervals = [new() { Day = "Wednesday", OpensAt = "11:00", ClosesAt = "15:00" }];
        options.ClosedDates = ["2026-12-24"];
        var before = await SnapshotAsync(factory);
        var html = await client.GetStringAsync(PagePath);
        Assert.Contains("America/New_York", html);
        Assert.Contains("Wednesday, 11:00, 15:00", html);
        Assert.Equal(before, await SnapshotAsync(factory));
        using var imported = await client.PostAsync(ImportPath, new FormUrlEncodedContent(Fields(Token(html))));
        AssertRedirect(client, imported, PagePath);
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var saved = await scope.ServiceProvider.GetRequiredService<PickupScheduleService>().GetAsync();
            Assert.Equal("America/New_York", saved.CampusTimeZone);
            Assert.Equal("Wednesday", Assert.Single(saved.WeeklyPickupIntervals).Day);
            Assert.Equal("2026-12-24", Assert.Single(saved.ClosedDates));
            var audit = await scope.ServiceProvider.GetRequiredService<ApplicationDbContext>().AdministrativeAuditEvents.SingleAsync();
            Assert.Equal(AdministrativeAuditActions.PickupScheduleImported, audit.Action);
        }
        var after = await SnapshotAsync(factory);
        using var repeat = await client.PostAsync(ImportPath, new FormUrlEncodedContent(Fields(Token(html))));
        AssertRedirect(client, repeat, PagePath);
        Assert.Contains("Reload the current schedule", await client.GetStringAsync(PagePath));
        Assert.Equal(after, await SnapshotAsync(factory));
    }

    private static Dictionary<string, string> Fields(string? token = null, string version = "")
    {
        var values = new Dictionary<string, string>
        {
            ["Input.ExpectedVersion"] = version,
            ["Input.CampusTimeZone"] = "America/Chicago",
            ["Input.WeeklyIntervalsText"] = "Monday, 09:00, 12:00\nMonday, 13:00, 16:00",
            ["Input.ClosedDatesText"] = "2026-12-25"
        };
        if (token is not null) values["__RequestVerificationToken"] = token;
        return values;
    }

    private static string Token(string html) => InputValue(html, "__RequestVerificationToken");
    private static string InputValue(string html, string name)
    {
        var tag = Regex.Matches(html, @"<input\b[^>]*>", RegexOptions.IgnoreCase).Cast<Match>()
            .First(match => Attribute(match.Value, "name") == name).Value;
        return Attribute(tag, "value");
    }
    private static string Attribute(string tag, string name)
        => WebUtility.HtmlDecode(Regex.Match(tag, $"\\b{Regex.Escape(name)}=\"([^\"]*)\"", RegexOptions.IgnoreCase).Groups[1].Value);
    private static void AssertRedirect(HttpClient client, HttpResponseMessage response, string expectedPath)
    {
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.NotNull(response.Headers.Location);
        Assert.Equal(expectedPath, new Uri(client.BaseAddress!, response.Headers.Location).AbsolutePath);
    }
    private static async Task<string> SnapshotAsync(AuthorizationWebApplicationFactory factory)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        return JsonSerializer.Serialize(new
        {
            Schedule = await scope.ServiceProvider.GetRequiredService<PickupScheduleService>().GetAsync(),
            Audits = await database.AdministrativeAuditEvents.AsNoTracking().OrderBy(row => row.Id).ToArrayAsync(),
            Inventory = await database.Listings.AsNoTracking().OrderBy(row => row.Id).Select(row => new { row.Id, row.Status }).ToArrayAsync(),
            Reservations = await database.Reservations.AsNoTracking().OrderBy(row => row.Id).Select(row => new { row.Id, row.Status, row.ExpiresAtUtc }).ToArrayAsync(),
            InventoryAudits = await database.AuditEvents.AsNoTracking().OrderBy(row => row.Id).ToArrayAsync()
        });
    }
}

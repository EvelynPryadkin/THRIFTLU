using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using LUThrift.Web.Authorization;
using LUThrift.Web.Data;
using LUThrift.Web.Models;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace LUThrift.Tests.Integration;

public sealed class CurrentAccountAuthorizationTests
{
    private const string LoginPath = "/Identity/Account/Login";
    private const string LogoutPath = "/Identity/Account/Logout";
    private const string DeniedPath = "/Identity/Account/AccessDenied";

    [Theory]
    [InlineData(ApplicationRoles.Staff, "revoked")]
    [InlineData(ApplicationRoles.Administrator, "revoked")]
    [InlineData(ApplicationRoles.Staff, "inactive")]
    [InlineData(ApplicationRoles.Administrator, "inactive")]
    [InlineData(ApplicationRoles.Staff, "deleted")]
    [InlineData(ApplicationRoles.Administrator, "deleted")]
    public async Task OldCookie_CannotOpenStaffPagesOrSubmitInventoryChangesAfterAccessIsLost(string role, string change)
    {
        using var factory = new AuthorizationWebApplicationFactory();
        var account = await CreateAccountAsync(factory, role);
        using var client = await LoginAsync(factory, account);
        await using (var setup = factory.Services.CreateAsyncScope())
        {
            var database = setup.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            await database.Listings.Where(row => row.Id == 1).ExecuteUpdateAsync(setters => setters
                .SetProperty(row => row.Status, ListingStatus.Draft));
            await database.Listings.Where(row => row.Id == 2).ExecuteUpdateAsync(setters => setters
                .SetProperty(row => row.Status, ListingStatus.Available));
        }
        // Capture a valid token while the account can still use the form. Subsequent
        // requests retain the original login cookie; no new sign-in refreshes roles.
        var token = await TokenAsync(client, "/Staff/Inventory/Create");
        await ChangeAccountAsync(factory, account.Id, change);
        var before = await SnapshotAsync(factory);

        foreach (var path in new[] { "/Staff", "/Staff/Inventory", "/Staff/Inventory/Create",
                     "/Staff/Inventory/Edit?id=1", "/Staff/Pickups", "/Admin" })
        {
            using var response = await client.GetAsync(path);
            AssertRedirect(client, response, DeniedPath);
        }
        var fields = ValidListingFields(token);
        foreach (var path in new[] { "/Staff/Inventory/Create", "/Staff/Inventory/Edit?id=1",
                     "/Staff/Inventory?handler=Publish&id=1", "/Staff/Inventory?handler=Archive&id=1",
                     "/Staff/Inventory?handler=MarkMissing&id=2", "/Staff/Inventory?handler=RecordWalkInCollection&id=2" })
        {
            using var response = await client.PostAsync(path, new FormUrlEncodedContent(fields));
            AssertRedirect(client, response, DeniedPath);
        }
        Assert.Equal(before, await SnapshotAsync(factory));
        using var catalog = await client.GetAsync("/Thrift");
        Assert.Equal(HttpStatusCode.OK, catalog.StatusCode);
    }

    [Theory]
    [InlineData(ApplicationRoles.Staff)]
    [InlineData(ApplicationRoles.Administrator)]
    public async Task CurrentActiveStaff_WithRealLoginCanCreateAndUseProtectedPages(string role)
    {
        using var factory = new AuthorizationWebApplicationFactory();
        var account = await CreateAccountAsync(factory, role);
        using var client = await LoginAsync(factory, account);
        foreach (var path in new[] { "/Staff", "/Staff/Inventory", "/Staff/Pickups" })
        {
            using var response = await client.GetAsync(path);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }
        using var admin = await client.GetAsync("/Admin");
        if (role == ApplicationRoles.Administrator)
            Assert.Equal(HttpStatusCode.OK, admin.StatusCode);
        else
            AssertRedirect(client, admin, DeniedPath);

        var token = await TokenAsync(client, "/Staff/Inventory/Create");
        using var created = await client.PostAsync("/Staff/Inventory/Create",
            new FormUrlEncodedContent(ValidListingFields(token)));
        AssertRedirect(client, created, "/Staff/Inventory");
        await using var scope = factory.Services.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var listing = await database.Listings.SingleAsync(row => row.Title == "Current account listing");
        Assert.Equal(ListingStatus.Draft, listing.Status);
        var audit = await database.AuditEvents.SingleAsync();
        Assert.Equal(listing.Id, audit.ListingId);
        Assert.Equal(account.Id, audit.ActorUserId);
        Assert.Equal(InventoryAuditActions.Created, audit.Action);
    }

    [Fact]
    public async Task DemotedAdministrator_OldCookieKeepsStaffAccessOnlyWhenCurrentStaffMembershipExists()
    {
        using var factory = new AuthorizationWebApplicationFactory();
        var account = await CreateAccountAsync(factory, ApplicationRoles.Administrator);
        using var client = await LoginAsync(factory, account);
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            var current = (await users.FindByIdAsync(account.Id))!;
            Assert.True((await users.AddToRoleAsync(current, ApplicationRoles.Staff)).Succeeded);
            Assert.True((await users.RemoveFromRoleAsync(current, ApplicationRoles.Administrator)).Succeeded);
        }
        var before = await SnapshotAsync(factory);
        using var staff = await client.GetAsync("/Staff/Inventory");
        Assert.Equal(HttpStatusCode.OK, staff.StatusCode);
        using var admin = await client.GetAsync("/Admin");
        AssertRedirect(client, admin, DeniedPath);
        Assert.Equal(before, await SnapshotAsync(factory));
    }

    [Fact]
    public async Task InactiveLogin_UsesSameGenericErrorAsUnknownAccountAndIssuesNoAuthenticationCookie()
    {
        using var factory = new AuthorizationWebApplicationFactory();
        var account = await CreateAccountAsync(factory, ApplicationRoles.Student);
        await ChangeAccountAsync(factory, account.Id, "inactive");
        var before = await SnapshotAsync(factory);
        var errors = new List<string>();
        foreach (var email in new[] { account.Email, IdentityTestContext.NewEmail() })
        {
            using var client = await factory.CreateIdentityClientAsync();
            var token = await TokenAsync(client, LoginPath);
            using var response = await client.PostAsync(LoginPath, new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["Input.Email"] = email, ["Input.Password"] = account.Password,
                ["__RequestVerificationToken"] = token
            }));
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Null(response.Headers.Location);
            var summary = Regex.Match(await response.Content.ReadAsStringAsync(),
                "<div\\b[^>]*role=\"alert\"[^>]*>([\\s\\S]*?)</div>", RegexOptions.IgnoreCase);
            Assert.True(summary.Success);
            errors.Add(Regex.Replace(WebUtility.HtmlDecode(Regex.Replace(summary.Groups[1].Value, "<[^>]+>", "")), "\\s+", " ").Trim());
            var cookieName = factory.Services.GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>()
                .Get(IdentityConstants.ApplicationScheme).Cookie.Name!;
            Assert.DoesNotContain(response.Headers.TryGetValues("Set-Cookie", out var values) ? values : [],
                header => header.StartsWith(cookieName + "=", StringComparison.Ordinal));
            using var protectedPage = await client.GetAsync("/Account/Reservations");
            AssertRedirect(client, protectedPage, LoginPath);
        }
        Assert.All(errors, error => Assert.Equal("Invalid email or password.", error));
        Assert.Equal(before, await SnapshotAsync(factory));
    }

    [Fact]
    public async Task DeactivatedStudent_OldCookieCannotReadAccountOrReserveOrCancel_ButCanBrowseAndLogout()
    {
        using var factory = new AuthorizationWebApplicationFactory();
        var account = await CreateAccountAsync(factory, ApplicationRoles.Student);
        using var client = await LoginAsync(factory, account);
        int reservationId;
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var database = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var listing = await database.Listings.SingleAsync(row => row.Id == 1);
            listing.Status = ListingStatus.Held;
            var now = DateTime.UtcNow;
            var reservation = new Reservation
            {
                ListingId = listing.Id, UserId = account.Id,
                CreatedAtUtc = now, ExpiresAtUtc = now.AddHours(1), Status = ReservationStatus.Active
            };
            database.Reservations.Add(reservation);
            await database.SaveChangesAsync();
            reservationId = reservation.Id;
        }
        var token = await TokenAsync(client, "/Account/Reservations");
        await ChangeAccountAsync(factory, account.Id, "inactive");
        var before = await SnapshotAsync(factory);
        foreach (var path in new[] { "/Account/Reservations", "/Account/Reserve" })
        {
            using var response = await client.GetAsync(path);
            AssertRedirect(client, response, DeniedPath);
        }
        using var reserve = await client.PostAsync("/Account/Reserve", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["ListingId"] = "2", ["__RequestVerificationToken"] = token
        }));
        AssertRedirect(client, reserve, DeniedPath);
        using var cancel = await client.PostAsync("/Account/Reservations?handler=Cancel", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["id"] = reservationId.ToString(System.Globalization.CultureInfo.InvariantCulture), ["__RequestVerificationToken"] = token
        }));
        AssertRedirect(client, cancel, DeniedPath);
        foreach (var path in new[] { "/", "/Thrift", "/Thrift/Details/1" })
        {
            using var response = await client.GetAsync(path);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }
        var logoutToken = await TokenAsync(client, LogoutPath);
        using var logout = await client.PostAsync(LogoutPath, new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = logoutToken
        }));
        AssertRedirect(client, logout, "/");
        using var afterLogout = await client.GetAsync("/Account/Reservations");
        AssertRedirect(client, afterLogout, LoginPath);
        Assert.Equal(before, await SnapshotAsync(factory));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SignInManager_RejectsInactiveOrDeletedAccountDespiteStaleTrackedUser(bool deleted)
    {
        using var factory = new AuthorizationWebApplicationFactory();
        var account = await CreateAccountAsync(factory, ApplicationRoles.Student);
        await using var scope = factory.Services.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var signIns = scope.ServiceProvider.GetRequiredService<SignInManager<ApplicationUser>>();
        Assert.IsType<CurrentAccountSignInManager>(signIns);
        var tracked = (await users.FindByIdAsync(account.Id))!;
        Assert.True(await signIns.CanSignInAsync(tracked));
        await ChangeAccountAsync(factory, account.Id, deleted ? "deleted" : "inactive");
        Assert.True(tracked.IsActive);

        Assert.False(await signIns.CanSignInAsync(tracked));
        var result = await signIns.CheckPasswordSignInAsync(tracked, account.Password, lockoutOnFailure: true);
        Assert.True(result.IsNotAllowed);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DefaultIdentityRecoveryPage_RechecksCurrentAccountBeforeRedeemingCode(bool deactivate)
    {
        using var factory = new AuthorizationWebApplicationFactory();
        var account = await CreateAccountAsync(factory, ApplicationRoles.Student);
        string recoveryCode;
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            var user = (await users.FindByIdAsync(account.Id))!;
            Assert.True((await users.ResetAuthenticatorKeyAsync(user)).Succeeded);
            Assert.True((await users.SetTwoFactorEnabledAsync(user, true)).Succeeded);
            recoveryCode = Assert.Single((await users.GenerateNewTwoFactorRecoveryCodesAsync(user, 1))!);
        }
        using var client = await factory.CreateIdentityClientAsync();
        var token = await TokenAsync(client, LoginPath);
        using var passwordStep = await client.PostAsync(LoginPath, new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["Input.Email"] = account.Email, ["Input.Password"] = account.Password,
            ["__RequestVerificationToken"] = token
        }));
        var cookieOptions = factory.Services.GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>();
        var pendingCookie = cookieOptions.Get(IdentityConstants.TwoFactorUserIdScheme).Cookie.Name!;
        Assert.Contains(passwordStep.Headers.GetValues("Set-Cookie"),
            header => header.StartsWith(pendingCookie + "=", StringComparison.Ordinal));
        const string recoveryPath = "/Identity/Account/LoginWithRecoveryCode";
        var recoveryToken = await TokenAsync(client, recoveryPath);
        if (deactivate) await ChangeAccountAsync(factory, account.Id, "inactive");
        var before = await SnapshotAsync(factory);

        using var recovery = await client.PostAsync(recoveryPath, new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["Input.RecoveryCode"] = recoveryCode, ["__RequestVerificationToken"] = recoveryToken
        }));

        var applicationCookie = cookieOptions.Get(IdentityConstants.ApplicationScheme).Cookie.Name!;
        var cookies = recovery.Headers.TryGetValues("Set-Cookie", out var values) ? values : [];
        if (deactivate)
        {
            Assert.Equal(HttpStatusCode.OK, recovery.StatusCode);
            Assert.DoesNotContain(cookies, header => header.StartsWith(applicationCookie + "=", StringComparison.Ordinal));
        }
        else
        {
            AssertRedirect(client, recovery, "/");
            Assert.Contains(cookies, header => header.StartsWith(applicationCookie + "=", StringComparison.Ordinal));
        }
        using var protectedPage = await client.GetAsync("/Account/Reservations");
        if (deactivate) AssertRedirect(client, protectedPage, LoginPath);
        else Assert.Equal(HttpStatusCode.OK, protectedPage.StatusCode);
        await using var verification = factory.Services.CreateAsyncScope();
        var usersAfter = verification.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var accountAfter = (await usersAfter.FindByIdAsync(account.Id))!;
        Assert.Equal(deactivate ? 1 : 0, await usersAfter.CountRecoveryCodesAsync(accountAfter));
        Assert.Equal(before, await SnapshotAsync(factory));
    }

    private sealed record Account(string Id, string Email, string Password);

    private static async Task<Account> CreateAccountAsync(AuthorizationWebApplicationFactory factory, string role)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var email = IdentityTestContext.NewEmail();
        var password = IdentityTestContext.NewPassword();
        var account = new ApplicationUser { UserName = email, Email = email, DisplayName = "Current permission test" };
        Assert.True((await users.CreateAsync(account, password)).Succeeded);
        Assert.True((await users.AddToRoleAsync(account, role)).Succeeded);
        return new(account.Id, email, password);
    }

    private static async Task<HttpClient> LoginAsync(AuthorizationWebApplicationFactory factory, Account account)
    {
        var client = await factory.CreateIdentityClientAsync();
        var token = await TokenAsync(client, LoginPath);
        using var login = await client.PostAsync(LoginPath, new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["Input.Email"] = account.Email, ["Input.Password"] = account.Password,
            ["__RequestVerificationToken"] = token
        }));
        AssertRedirect(client, login, "/");
        var cookieName = factory.Services.GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>()
            .Get(IdentityConstants.ApplicationScheme).Cookie.Name!;
        Assert.Contains(login.Headers.GetValues("Set-Cookie"), header => header.StartsWith(cookieName + "=", StringComparison.Ordinal));
        return client;
    }

    private static async Task ChangeAccountAsync(AuthorizationWebApplicationFactory factory, string accountId, string change)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        if (change == "revoked")
            await database.UserRoles.Where(row => row.UserId == accountId).ExecuteDeleteAsync();
        else if (change == "inactive")
            await database.Users.Where(row => row.Id == accountId).ExecuteUpdateAsync(setters => setters.SetProperty(row => row.IsActive, false));
        else if (change == "deleted")
            await database.Users.Where(row => row.Id == accountId).ExecuteDeleteAsync();
        else
            throw new ArgumentException("Unknown account change.", nameof(change));
    }

    private static Dictionary<string, string> ValidListingFields(string token) => new()
    {
        ["Input.Title"] = "Current account listing", ["Input.Description"] = "Synthetic authorization fixture.",
        ["Input.Condition"] = "Good", ["Input.Size"] = "M", ["Input.CategoryId"] = "1",
        ["__RequestVerificationToken"] = token
    };

    private static async Task<string> TokenAsync(HttpClient client, string path)
    {
        using var page = await client.GetAsync(path);
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        var match = Regex.Match(await page.Content.ReadAsStringAsync(),
            "<input\\b[^>]*name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"", RegexOptions.IgnoreCase);
        Assert.True(match.Success, "Expected an antiforgery token.");
        return WebUtility.HtmlDecode(match.Groups[1].Value);
    }

    private static void AssertRedirect(HttpClient client, HttpResponseMessage response, string path)
    {
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.NotNull(response.Headers.Location);
        Assert.Equal(path, new Uri(client.BaseAddress!, response.Headers.Location).AbsolutePath);
    }

    private static async Task<string> SnapshotAsync(AuthorizationWebApplicationFactory factory)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        return JsonSerializer.Serialize(new
        {
            Listings = await database.Listings.AsNoTracking().OrderBy(row => row.Id).Select(row => new
            {
                row.Id, row.Title, row.Description, row.Condition, row.Size, row.CategoryId, row.Status, row.CreatedAtUtc
            }).ToArrayAsync(),
            Reservations = await database.Reservations.AsNoTracking().OrderBy(row => row.Id).Select(row => new
            {
                row.Id, row.ListingId, row.UserId, row.CreatedAtUtc, row.ExpiresAtUtc, row.Status, row.FulfilledAtUtc, row.CancellationReason
            }).ToArrayAsync(),
            Audits = await database.AuditEvents.AsNoTracking().OrderBy(row => row.Id).Select(row => new
            {
                row.Id, row.ListingId, row.ActorUserId, row.Action, row.OccurredAtUtc, row.Details
            }).ToArrayAsync()
        });
    }
}

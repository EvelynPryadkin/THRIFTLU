using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using LUThrift.Web.Authorization;
using LUThrift.Web.Data;
using LUThrift.Web.Models;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace LUThrift.Tests.Integration;

public sealed class StaffAccessPageTests
{
    private const string PagePath = "/Admin/Users";
    private const string PostPath = PagePath + "?handler=Change";

    [Theory]
    [InlineData(null, "/Identity/Account/Login")]
    [InlineData(ApplicationRoles.Student, "/Identity/Account/AccessDenied")]
    [InlineData(ApplicationRoles.Staff, "/Identity/Account/AccessDenied")]
    public async Task NonAdministrator_CannotReadAccountsOrPostChanges(string? role, string redirect)
    {
        using var factory = new AuthorizationWebApplicationFactory();
        using var client = await factory.CreateIdentityClientAsync(role);
        var target = await AddAccountAsync(factory, "Private staff-access target");
        var before = await SnapshotAsync(factory);
        using var page = await client.GetAsync(PagePath);
        AssertRedirect(client, page, redirect);
        Assert.DoesNotContain(target.Email!, await page.Content.ReadAsStringAsync());
        using var post = await client.PostAsync(PostPath, new FormUrlEncodedContent(Fields(target.Id, "Grant")));
        AssertRedirect(client, post, redirect);
        Assert.Equal(before, await SnapshotAsync(factory));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OldAdministratorCookie_CannotReadOrChangeAfterRevocationOrDeactivation(bool deactivate)
    {
        using var factory = new AuthorizationWebApplicationFactory();
        using var client = await factory.CreateIdentityClientAsync(ApplicationRoles.Administrator);
        var target = await AddAccountAsync(factory);
        var token = await TokenAsync(client, PagePath);
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var database = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var adminId = await (from membership in database.UserRoles
                join role in database.Roles on membership.RoleId equals role.Id
                where role.Name == ApplicationRoles.Administrator select membership.UserId).SingleAsync();
            if (deactivate)
                await database.Users.Where(row => row.Id == adminId).ExecuteUpdateAsync(setters => setters.SetProperty(row => row.IsActive, false));
            else
                await database.UserRoles.Where(row => row.UserId == adminId).ExecuteDeleteAsync();
        }
        var before = await SnapshotAsync(factory);
        foreach (var path in new[] { PagePath, PagePath + "?Filter.Search=" + new string('x', 101) })
        {
            using var page = await client.GetAsync(path);
            AssertRedirect(client, page, "/Identity/Account/AccessDenied");
        }
        using var post = await client.PostAsync(PostPath, new FormUrlEncodedContent(Fields(target.Id, "Grant", token)));
        AssertRedirect(client, post, "/Identity/Account/AccessDenied");
        Assert.Equal(before, await SnapshotAsync(factory));
    }

    [Fact]
    public async Task GrantAndRemove_UsePrgPreserveStudentIgnoreForgedFieldsAndDoNotDuplicateAudits()
    {
        using var factory = new AuthorizationWebApplicationFactory();
        using var client = await factory.CreateIdentityClientAsync(ApplicationRoles.Administrator);
        var target = await AddAccountAsync(factory);
        var token = await TokenAsync(client, PagePath);
        var fields = Fields(target.Id, "Grant", token);
        fields["Role"] = ApplicationRoles.Administrator;
        fields["ActorUserId"] = target.Id;
        fields["IsActive"] = "false";
        fields["OccurredAtUtc"] = "1900-01-01T00:00:00Z";
        using var grant = await client.PostAsync(PostPath, new FormUrlEncodedContent(fields));
        AssertRedirect(client, grant, PagePath);
        await AssertRolesAsync(factory, target.Id, ApplicationRoles.Staff, ApplicationRoles.Student);
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var database = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var audit = await database.AdministrativeAuditEvents.AsNoTracking().SingleAsync();
            Assert.NotEqual(target.Id, audit.ActorUserId);
            Assert.Equal(target.Id, audit.TargetUserId);
            Assert.Equal(AdministrativeAuditActions.StaffGranted, audit.Action);
            Assert.True(audit.OccurredAtUtc.Year > 1900);
            Assert.True(await database.Users.Where(row => row.Id == target.Id).Select(row => row.IsActive).SingleAsync());
        }
        var afterGrant = await SnapshotAsync(factory);
        using var refresh = await client.GetAsync(PagePath);
        Assert.Equal(HttpStatusCode.OK, refresh.StatusCode);
        Assert.Contains("staff-access-message", await refresh.Content.ReadAsStringAsync());
        Assert.Equal(afterGrant, await SnapshotAsync(factory));
        using var duplicate = await client.PostAsync(PostPath, new FormUrlEncodedContent(fields));
        AssertRedirect(client, duplicate, PagePath);
        Assert.Equal(afterGrant, await SnapshotAsync(factory));

        using var remove = await client.PostAsync(PostPath, new FormUrlEncodedContent(Fields(target.Id, "Remove", token)));
        AssertRedirect(client, remove, PagePath);
        await AssertRolesAsync(factory, target.Id, ApplicationRoles.Student);
        var afterRemove = await SnapshotAsync(factory);
        using var removeAgain = await client.PostAsync(PostPath, new FormUrlEncodedContent(Fields(target.Id, "Remove", token)));
        AssertRedirect(client, removeAgain, PagePath);
        Assert.Equal(afterRemove, await SnapshotAsync(factory));
        await using var verification = factory.Services.CreateAsyncScope();
        Assert.Equal(2, await verification.ServiceProvider.GetRequiredService<ApplicationDbContext>().AdministrativeAuditEvents.CountAsync());
    }

    [Theory]
    [InlineData("Administrator")]
    [InlineData("Delete")]
    [InlineData("0")]
    [InlineData("99")]
    [InlineData("")]
    public async Task ForgedOrMissingAction_IsRejectedWithoutChanges(string action)
    {
        using var factory = new AuthorizationWebApplicationFactory();
        using var client = await factory.CreateIdentityClientAsync(ApplicationRoles.Administrator);
        var target = await AddAccountAsync(factory);
        var token = await TokenAsync(client, PagePath);
        var before = await SnapshotAsync(factory);
        using var response = await client.PostAsync(PostPath, new FormUrlEncodedContent(Fields(target.Id, action, token)));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(before, await SnapshotAsync(factory));
    }

    [Fact]
    public async Task MissingAntiforgeryToken_DoesNotChangeMembership()
    {
        using var factory = new AuthorizationWebApplicationFactory();
        using var client = await factory.CreateIdentityClientAsync(ApplicationRoles.Administrator);
        var target = await AddAccountAsync(factory);
        var before = await SnapshotAsync(factory);
        using var response = await client.PostAsync(PostPath, new FormUrlEncodedContent(Fields(target.Id, "Grant")));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(before, await SnapshotAsync(factory));
    }

    [Fact]
    public async Task AdministratorAccounts_HaveNoChangeFormAndDirectPostsCannotModifyThem()
    {
        using var factory = new AuthorizationWebApplicationFactory();
        using var client = await factory.CreateIdentityClientAsync(ApplicationRoles.Administrator);
        var target = await AddAccountAsync(factory, "Protected administrator", ApplicationRoles.Administrator);
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            Assert.True((await users.AddToRoleAsync((await users.FindByIdAsync(target.Id))!, ApplicationRoles.Staff)).Succeeded);
        }
        using var page = await client.GetAsync(QueryHelpers.AddQueryString(PagePath, "Filter.Search", target.Email!));
        var html = await page.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        Assert.DoesNotContain(Forms(html), form => Attribute(form, "action").Contains("handler=Change", StringComparison.Ordinal));
        var token = await TokenAsync(client, "/Identity/Account/Logout");
        var before = await SnapshotAsync(factory);
        foreach (var action in new[] { "Grant", "Remove" })
        {
            using var post = await client.PostAsync(PostPath, new FormUrlEncodedContent(Fields(target.Id, action, token)));
            AssertRedirect(client, post, PagePath);
        }
        Assert.Equal(before, await SnapshotAsync(factory));
    }

    [Fact]
    public async Task RemovedStaff_OldRoleCookieImmediatelyLosesStaffAccess()
    {
        using var factory = new AuthorizationWebApplicationFactory();
        using var administrator = await factory.CreateIdentityClientAsync(ApplicationRoles.Administrator);
        using var staff = await factory.CreateIdentityClientAsync(ApplicationRoles.Staff);
        string staffId;
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var database = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            staffId = await (from membership in database.UserRoles join role in database.Roles on membership.RoleId equals role.Id
                where role.Name == ApplicationRoles.Staff select membership.UserId).SingleAsync();
            var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            Assert.True((await users.AddToRoleAsync((await users.FindByIdAsync(staffId))!, ApplicationRoles.Student)).Succeeded);
        }
        using var before = await staff.GetAsync("/Staff/Inventory");
        Assert.Equal(HttpStatusCode.OK, before.StatusCode);
        var token = await TokenAsync(administrator, PagePath);
        using var remove = await administrator.PostAsync(PostPath, new FormUrlEncodedContent(Fields(staffId, "Remove", token)));
        AssertRedirect(administrator, remove, PagePath);
        using var denied = await staff.GetAsync("/Staff/Inventory");
        AssertRedirect(staff, denied, "/Identity/Account/AccessDenied");
        await AssertRolesAsync(factory, staffId, ApplicationRoles.Student);

        // Force the normal validation interval to elapse in this isolated test host.
        // Immediate revocation above comes from current membership; stamp validation
        // then rejects the old session entirely, including otherwise valid Student access.
        factory.Services.GetRequiredService<IOptions<SecurityStampValidatorOptions>>().Value.ValidationInterval = TimeSpan.Zero;
        using var invalidated = await staff.GetAsync("/Account/Reservations");
        AssertRedirect(staff, invalidated, "/Identity/Account/Login");
    }

    [Fact]
    public async Task SearchAndPagination_EncodeNamesPreserveSelectionAndDoNotWrite()
    {
        using var factory = new AuthorizationWebApplicationFactory();
        using var client = await factory.CreateIdentityClientAsync(ApplicationRoles.Administrator);
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var database = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            database.Users.AddRange(Enumerable.Range(1, 23).Select(index => new ApplicationUser
            {
                Id = $"page-fixture-{index:000}", DisplayName = "<script>fixture</script> %_", IsActive = true,
                Email = $"fixture{index:000}@example.test", NormalizedEmail = $"FIXTURE{index:000}@EXAMPLE.TEST"
            }));
            await database.SaveChangesAsync();
        }
        var before = await SnapshotAsync(factory);
        using var landing = await client.GetAsync("/Admin");
        Assert.Contains(Tags(await landing.Content.ReadAsStringAsync(), "a"), tag => Attribute(tag, "href") == PagePath);
        var path = QueryHelpers.AddQueryString(PagePath, "Filter.Search", "  %_  ");
        using var first = await client.GetAsync(path);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.True(first.Headers.CacheControl?.NoStore);
        var html = await first.Content.ReadAsStringAsync();
        Assert.DoesNotContain("<script>fixture</script>", html);
        Assert.Contains("&lt;script&gt;fixture&lt;/script&gt;", html);
        Assert.Equal("%_", Attribute(Assert.Single(Tags(html, "input"), tag => Attribute(tag, "name") == "Filter.Search"), "value"));
        var next = Assert.Single(Tags(html, "a"), tag => Attribute(tag, "rel") == "next");
        var nextUrl = new Uri(client.BaseAddress!, Attribute(next, "href"));
        var query = QueryHelpers.ParseQuery(nextUrl.Query);
        Assert.Equal("%_", query["Filter.Search"].ToString());
        Assert.Equal("2", query["pageNumber"].ToString());
        using var second = await client.GetAsync(nextUrl.PathAndQuery);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        Assert.Equal(before, await SnapshotAsync(factory));
    }

    [Fact]
    public async Task InvalidSearchAndExtremePageNumber_AreSafe()
    {
        using var factory = new AuthorizationWebApplicationFactory();
        using var client = await factory.CreateIdentityClientAsync(ApplicationRoles.Administrator);
        var target = await AddAccountAsync(factory);
        var before = await SnapshotAsync(factory);
        using var invalid = await client.GetAsync(QueryHelpers.AddQueryString(PagePath, "Filter.Search", new string('x', 101)));
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        Assert.DoesNotContain(target.Email!, await invalid.Content.ReadAsStringAsync());
        foreach (var page in new[] { "-1", "2147483647", "999999999999999999999", "not-a-number" })
        {
            using var response = await client.GetAsync(QueryHelpers.AddQueryString(PagePath, "pageNumber", page));
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }
        Assert.Equal(before, await SnapshotAsync(factory));
    }

    private static async Task<ApplicationUser> AddAccountAsync(AuthorizationWebApplicationFactory factory,
        string name = "Synthetic student target", string role = ApplicationRoles.Student)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var email = IdentityTestContext.NewEmail();
        var user = new ApplicationUser { DisplayName = name, UserName = email, Email = email };
        Assert.True((await users.CreateAsync(user)).Succeeded);
        Assert.True((await users.AddToRoleAsync(user, role)).Succeeded);
        return user;
    }

    private static Dictionary<string, string> Fields(string id, string action, string? token = null)
    {
        var fields = new Dictionary<string, string> { ["targetUserId"] = id, ["action"] = action };
        if (token is not null) fields["__RequestVerificationToken"] = token;
        return fields;
    }

    private static async Task<string> TokenAsync(HttpClient client, string path)
    {
        using var response = await client.GetAsync(path);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var token = Assert.Single(Tags(await response.Content.ReadAsStringAsync(), "input")
            .Where(tag => Attribute(tag, "name") == "__RequestVerificationToken").Select(tag => Attribute(tag, "value")).Distinct());
        Assert.NotEmpty(token);
        return token;
    }

    private static string[] Tags(string html, string tag) => Regex.Matches(html, $@"<{tag}\b[^>]*>", RegexOptions.IgnoreCase)
        .Cast<Match>().Select(match => match.Value).ToArray();
    private static string[] Forms(string html) => Regex.Matches(html, @"<form\b[^>]*>[\s\S]*?</form>", RegexOptions.IgnoreCase)
        .Cast<Match>().Select(match => match.Value).ToArray();
    private static string Attribute(string tag, string name) => WebUtility.HtmlDecode(Regex.Match(tag,
        "\\b" + name + "=\"([^\"]*)\"", RegexOptions.IgnoreCase).Groups[1].Value);
    private static void AssertRedirect(HttpClient client, HttpResponseMessage response, string path)
    {
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.NotNull(response.Headers.Location);
        Assert.Equal(path, new Uri(client.BaseAddress!, response.Headers.Location).AbsolutePath);
    }
    private static async Task AssertRolesAsync(AuthorizationWebApplicationFactory factory, string id, params string[] expected)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        Assert.Equal(expected.Order(), (await users.GetRolesAsync((await users.FindByIdAsync(id))!)).Order());
    }
    private static async Task<string> SnapshotAsync(AuthorizationWebApplicationFactory factory)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        return JsonSerializer.Serialize(new
        {
            Users = await database.Users.AsNoTracking().OrderBy(row => row.Id).Select(row => new
            { row.Id, row.IsActive, row.SecurityStamp, row.ConcurrencyStamp }).ToArrayAsync(),
            Roles = await database.UserRoles.AsNoTracking().OrderBy(row => row.UserId).ThenBy(row => row.RoleId).ToArrayAsync(),
            Audits = await database.AdministrativeAuditEvents.AsNoTracking().OrderBy(row => row.Id).ToArrayAsync(),
            Inventory = await database.Listings.AsNoTracking().OrderBy(row => row.Id).Select(row => new { row.Id, row.Status }).ToArrayAsync(),
            InventoryAudits = await database.AuditEvents.AsNoTracking().OrderBy(row => row.Id).ToArrayAsync()
        });
    }
}

using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;
using LUThrift.Web.Authorization;
using LUThrift.Web.Data;
using LUThrift.Web.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace LUThrift.Tests.Integration;

public class StaffListingFormTests
{
    private const string CreatePath = "/Staff/Inventory/Create";
    private const string DashboardPath = "/Staff/Inventory";

    [Theory]
    [InlineData(false, null, "/Identity/Account/Login")]
    [InlineData(true, null, "/Identity/Account/Login")]
    [InlineData(false, ApplicationRoles.Student, "/Identity/Account/AccessDenied")]
    [InlineData(true, ApplicationRoles.Student, "/Identity/Account/AccessDenied")]
    [InlineData(false, ApplicationRoles.Staff, null)]
    [InlineData(true, ApplicationRoles.Staff, null)]
    [InlineData(false, ApplicationRoles.Administrator, null)]
    [InlineData(true, ApplicationRoles.Administrator, null)]
    public async Task Forms_EnforceStaffAccessAndExposeOnlyEditableFields(bool edit, string? role, string? redirectPath)
    {
        using var factory = new AuthorizationWebApplicationFactory();
        using var client = await factory.CreateIdentityClientAsync(role);
        var path = edit ? EditPath(1) : CreatePath;

        using var response = await client.GetAsync(path);

        if (redirectPath is not null)
        {
            AssertRedirect(client, response, redirectPath);
            using var content = new FormUrlEncodedContent(ValidFields());
            using var post = await client.PostAsync(path, content);
            AssertRedirect(client, post, redirectPath);
            return;
        }

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var form = ListingForm(await response.Content.ReadAsStringAsync());
        var names = Controls(form).Select(control => Attribute(control, "name"))
            .OrderBy(name => name, StringComparer.Ordinal).ToArray();
        Assert.Equal(new[]
        {
            "Input.CategoryId", "Input.Condition", "Input.Description", "Input.Size", "Input.Title", "__RequestVerificationToken"
        }, names);
        AssertCategories(form);
        Assert.NotEmpty(Token(form));
        if (edit)
        {
            Assert.Equal(EditPath(1), Attribute(form, "action"));
            Assert.Equal("Blue hoodie", InputValue(form, "Input.Title"));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Forms_RejectMissingAntiforgeryTokenWithoutWrites(bool edit)
    {
        using var factory = new AuthorizationWebApplicationFactory();
        using var client = await factory.CreateIdentityClientAsync(ApplicationRoles.Staff);
        var before = await ListingsAsync(factory);
        var path = edit ? EditPath(before[0].Id) : CreatePath;
        await GetFormAsync(client, path);
        using var content = new FormUrlEncodedContent(ValidFields());

        using var response = await client.PostAsync(path, content);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await AssertNoWritesAsync(factory, before);
    }

    [Fact]
    public async Task Create_IgnoresProtectedFieldsAndUsesAuthenticatedActorWithOneTimeConfirmation()
    {
        using var factory = new AuthorizationWebApplicationFactory();
        using var client = await factory.CreateIdentityClientAsync(ApplicationRoles.Staff);
        var before = await ListingsAsync(factory);
        var form = await GetFormAsync(client, CreatePath);
        var fields = ValidFields();
        fields["__RequestVerificationToken"] = Token(form);
        AddForgedFields(fields);
        var startedAt = DateTime.UtcNow;
        using var content = new FormUrlEncodedContent(fields);

        using var response = await client.PostAsync(CreatePath, content);

        AssertRedirect(client, response, DashboardPath);
        await AssertOneTimeMessageAsync(client, "Listing created as a draft.");
        await using var scope = factory.Services.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        Assert.Equal(before.Length + 1, await database.Listings.CountAsync());
        var created = await database.Listings.SingleAsync(listing => listing.Title == fields["Input.Title"]);
        Assert.NotEqual(int.MaxValue, created.Id);
        Assert.Equal(ListingStatus.Draft, created.Status);
        Assert.InRange(created.CreatedAtUtc, startedAt, DateTime.UtcNow);
        var audit = await database.AuditEvents.SingleAsync();
        Assert.Equal(created.Id, audit.ListingId);
        Assert.Equal(InventoryAuditActions.Created, audit.Action);
        Assert.Equal(await database.Users.Select(user => user.Id).SingleAsync(), audit.ActorUserId);
    }

    [Fact]
    public async Task Edit_UsesQueryIdAndPreservesProtectedFieldsWithAuthenticatedAudit()
    {
        using var factory = new AuthorizationWebApplicationFactory();
        using var client = await factory.CreateIdentityClientAsync(ApplicationRoles.Administrator);
        var before = await ListingsAsync(factory);
        var original = before[0];
        var other = before[1];
        var path = EditPath(original.Id);
        var form = await GetFormAsync(client, path);
        var fields = ValidFields();
        fields["__RequestVerificationToken"] = Token(form);
        AddForgedFields(fields);
        fields["id"] = fields["Input.Id"] = fields["ListingId"] = other.Id.ToString(CultureInfo.InvariantCulture);
        using var content = new FormUrlEncodedContent(fields);

        using var response = await client.PostAsync(path, content);

        AssertRedirect(client, response, DashboardPath);
        await AssertOneTimeMessageAsync(client, "Listing updated.");
        await using var scope = factory.Services.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var edited = await database.Listings.SingleAsync(listing => listing.Id == original.Id);
        Assert.Equal(fields["Input.Title"], edited.Title);
        Assert.Equal(fields["Input.Description"], edited.Description);
        Assert.Equal(fields["Input.Condition"], edited.Condition);
        Assert.Equal("M", edited.Size);
        Assert.Equal(2, edited.CategoryId);
        Assert.Equal(original.Status, edited.Status);
        Assert.Equal(original.CreatedAtUtc, edited.CreatedAtUtc);
        Assert.Equal(Snapshot(other), Snapshot(await database.Listings.SingleAsync(listing => listing.Id == other.Id)));
        var audit = await database.AuditEvents.SingleAsync();
        Assert.Equal(original.Id, audit.ListingId);
        Assert.Equal(InventoryAuditActions.Updated, audit.Action);
        Assert.Equal(await database.Users.Select(user => user.Id).SingleAsync(), audit.ActorUserId);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InvalidForm_RedisplaysPostedValuesAndCategoriesWithoutWrites(bool edit)
    {
        using var factory = new AuthorizationWebApplicationFactory();
        using var client = await factory.CreateIdentityClientAsync(ApplicationRoles.Staff);
        var before = await ListingsAsync(factory);
        var path = edit ? EditPath(before[0].Id) : CreatePath;
        var fields = ValidFields();
        fields["Input.Title"] = "  Retained title  ";
        fields["Input.Description"] = "<retained> description";
        fields["Input.Condition"] = string.Empty;
        fields["__RequestVerificationToken"] = Token(await GetFormAsync(client, path));
        using var content = new FormUrlEncodedContent(fields);

        using var response = await client.PostAsync(path, content);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var form = ListingForm(await response.Content.ReadAsStringAsync());
        Assert.Equal(fields["Input.Title"], InputValue(form, "Input.Title"));
        Assert.Equal(fields["Input.Size"], InputValue(form, "Input.Size"));
        var description = Regex.Match(form, @"<textarea\b[^>]*name=""Input.Description""[^>]*>([\s\S]*?)</textarea>", RegexOptions.IgnoreCase);
        Assert.True(description.Success);
        Assert.Equal(fields["Input.Description"], WebUtility.HtmlDecode(description.Groups[1].Value).Trim());
        Assert.Contains("The Condition field is required.", WebUtility.HtmlDecode(form));
        AssertCategories(form);
        Assert.Contains(Tags(form, "option"), option => Attribute(option, "value") == "2"
            && Attribute(option, "selected") == "selected");
        await AssertNoWritesAsync(factory, before);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnknownCategory_RedisplaysFriendlyFieldErrorWithoutWrites(bool edit)
    {
        using var factory = new AuthorizationWebApplicationFactory();
        using var client = await factory.CreateIdentityClientAsync(ApplicationRoles.Staff);
        var before = await ListingsAsync(factory);
        var path = edit ? EditPath(before[0].Id) : CreatePath;
        var fields = ValidFields();
        fields["Input.CategoryId"] = int.MaxValue.ToString(CultureInfo.InvariantCulture);
        fields["__RequestVerificationToken"] = Token(await GetFormAsync(client, path));
        using var content = new FormUrlEncodedContent(fields);

        using var response = await client.PostAsync(path, content);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var form = ListingForm(await response.Content.ReadAsStringAsync());
        var error = Regex.Match(form, @"<span\b[^>]*data-valmsg-for=""Input.CategoryId""[^>]*>([\s\S]*?)</span>", RegexOptions.IgnoreCase);
        Assert.True(error.Success);
        Assert.Contains("The selected category no longer exists.", WebUtility.HtmlDecode(error.Groups[1].Value));
        AssertCategories(form);
        await AssertNoWritesAsync(factory, before);
    }

    [Fact]
    public async Task UnknownListing_ReturnsNotFoundForGetAndInvalidPost()
    {
        using var factory = new AuthorizationWebApplicationFactory();
        using var client = await factory.CreateIdentityClientAsync(ApplicationRoles.Staff);
        var before = await ListingsAsync(factory);
        var fields = ValidFields();
        fields["Input.Title"] = string.Empty;
        fields["__RequestVerificationToken"] = Token(await GetFormAsync(client, CreatePath));

        using var get = await client.GetAsync(EditPath(int.MaxValue));
        using var content = new FormUrlEncodedContent(fields);
        using var post = await client.PostAsync(EditPath(int.MaxValue), content);

        Assert.Equal(HttpStatusCode.NotFound, get.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, post.StatusCode);
        await AssertNoWritesAsync(factory, before);
    }

    [Fact]
    public async Task ListingHeldAfterFormLoad_RejectsPostAndRemovesEditForm()
    {
        using var factory = new AuthorizationWebApplicationFactory();
        using var client = await factory.CreateIdentityClientAsync(ApplicationRoles.Staff);
        var original = (await ListingsAsync(factory))[0];
        var path = EditPath(original.Id);
        var fields = ValidFields();
        fields["__RequestVerificationToken"] = Token(await GetFormAsync(client, path));
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<ApplicationDbContext>().Listings
                .Where(listing => listing.Id == original.Id)
                .ExecuteUpdateAsync(setters => setters.SetProperty(listing => listing.Status, ListingStatus.Held));
        }
        var before = await ListingsAsync(factory);
        using var content = new FormUrlEncodedContent(fields);

        using var post = await client.PostAsync(path, content);
        using var get = await client.GetAsync(path);

        foreach (var response in new[] { post, get })
        {
            Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
            var html = await response.Content.ReadAsStringAsync();
            Assert.Contains("This listing cannot be edited in its current status.", WebUtility.HtmlDecode(html));
            Assert.DoesNotContain("name=\"Input.Title\"", html);
        }
        await AssertNoWritesAsync(factory, before);
    }

    [Fact]
    public async Task AuditWriteFailure_RollsBackEditAndRedisplaysGenericError()
    {
        using var factory = new AuthorizationWebApplicationFactory();
        using var client = await factory.CreateIdentityClientAsync(ApplicationRoles.Staff);
        var before = await ListingsAsync(factory);
        var path = EditPath(before[0].Id);
        var fields = ValidFields();
        fields["__RequestVerificationToken"] = Token(await GetFormAsync(client, path));
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<ApplicationDbContext>().Database.ExecuteSqlRawAsync("""
                CREATE TRIGGER RejectFormAudit BEFORE INSERT ON "AuditEvents"
                BEGIN SELECT RAISE(ABORT, 'Form audit test failure'); END;
                """);
        }
        using var content = new FormUrlEncodedContent(fields);

        using var response = await client.PostAsync(path, content);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var html = await response.Content.ReadAsStringAsync();
        Assert.Contains("We couldn't save the listing. Please try again.", WebUtility.HtmlDecode(html));
        Assert.DoesNotContain("Form audit test failure", html);
        Assert.DoesNotContain("SqliteException", html);
        var form = ListingForm(html);
        Assert.Equal(fields["Input.Title"], InputValue(form, "Input.Title"));
        AssertCategories(form);
        await AssertNoWritesAsync(factory, before);
    }

    private static string EditPath(int id) => $"/Staff/Inventory/Edit?id={id}";

    private static Dictionary<string, string> ValidFields() => new()
    {
        ["Input.Title"] = $"Form listing {Guid.NewGuid():N}",
        ["Input.Description"] = "A listing created by a form test.",
        ["Input.Condition"] = "Good",
        ["Input.Size"] = " M ",
        ["Input.CategoryId"] = "2"
    };

    private static void AddForgedFields(Dictionary<string, string> fields)
    {
        fields["Id"] = fields["Input.Id"] = int.MaxValue.ToString(CultureInfo.InvariantCulture);
        fields["Status"] = fields["Input.Status"] = nameof(ListingStatus.Archived);
        fields["CreatedAtUtc"] = fields["Input.CreatedAtUtc"] = "2000-01-01T00:00:00Z";
        fields["ActorUserId"] = fields["Input.ActorUserId"] = fields["UserId"] = fields["Input.UserId"]
            = fields["AuditEvents[0].ActorUserId"] = Guid.NewGuid().ToString();
        fields["AuditEvents[0].Action"] = "Forged audit action";
    }

    private static async Task<string> GetFormAsync(HttpClient client, string path)
    {
        using var response = await client.GetAsync(path);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return ListingForm(await response.Content.ReadAsStringAsync());
    }

    private static string ListingForm(string html)
        => Assert.Single(Regex.Matches(html, @"<form\b[^>]*>[\s\S]*?</form>", RegexOptions.IgnoreCase).Cast<Match>(),
            form => form.Value.Contains("name=\"Input.Title\"", StringComparison.Ordinal)).Value;

    private static string[] Controls(string form) => Tags(form, "(?:input|textarea|select)");
    private static string[] Tags(string html, string tag)
        => Regex.Matches(html, $@"<{tag}\b[^>]*>", RegexOptions.IgnoreCase).Cast<Match>().Select(match => match.Value).ToArray();
    private static string Attribute(string html, string name)
        => WebUtility.HtmlDecode(Regex.Match(html, $"\\b{name}=\"([^\"]*)\"", RegexOptions.IgnoreCase).Groups[1].Value);
    private static string InputValue(string form, string name)
        => Attribute(Assert.Single(Tags(form, "input"), control => Attribute(control, "name") == name), "value");
    private static string Token(string form) => InputValue(form, "__RequestVerificationToken");

    private static void AssertCategories(string form)
    {
        var select = Assert.Single(Regex.Matches(form, @"<select\b[^>]*>[\s\S]*?</select>", RegexOptions.IgnoreCase).Cast<Match>(),
            control => Attribute(control.Value, "name") == "Input.CategoryId");
        var options = Regex.Matches(select.Value, @"<option\b[^>]*>([\s\S]*?)</option>", RegexOptions.IgnoreCase).Cast<Match>().ToArray();
        Assert.Equal(new[] { "0", "1", "2", "3" }, options.Select(option => Attribute(option.Value, "value")));
        Assert.Equal(new[] { "Choose a category", "Clothing", "Dorm supplies", "Books" },
            options.Select(option => WebUtility.HtmlDecode(option.Groups[1].Value).Trim()));
    }

    private static void AssertRedirect(HttpClient client, HttpResponseMessage response, string expectedPath)
    {
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.NotNull(response.Headers.Location);
        Assert.Equal(expectedPath, new Uri(client.BaseAddress!, response.Headers.Location).AbsolutePath);
    }

    private static async Task AssertOneTimeMessageAsync(HttpClient client, string message)
    {
        using var first = await client.GetAsync(DashboardPath);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        var html = await first.Content.ReadAsStringAsync();
        Assert.Contains("role=\"status\"", html);
        Assert.Contains(message, WebUtility.HtmlDecode(html));
        using var refresh = await client.GetAsync(DashboardPath);
        Assert.Equal(HttpStatusCode.OK, refresh.StatusCode);
        Assert.DoesNotContain(message, WebUtility.HtmlDecode(await refresh.Content.ReadAsStringAsync()));
    }

    private static async Task<Listing[]> ListingsAsync(AuthorizationWebApplicationFactory factory)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<ApplicationDbContext>().Listings
            .AsNoTracking().OrderBy(listing => listing.Id).ToArrayAsync();
    }

    private static async Task AssertNoWritesAsync(AuthorizationWebApplicationFactory factory, Listing[] before)
    {
        Assert.Equal(before.Select(Snapshot), (await ListingsAsync(factory)).Select(Snapshot));
        await using var scope = factory.Services.CreateAsyncScope();
        Assert.Empty(await scope.ServiceProvider.GetRequiredService<ApplicationDbContext>().AuditEvents.ToListAsync());
    }

    private static object Snapshot(Listing listing)
        => (listing.Id, listing.Title, listing.Description, listing.Condition, listing.Size,
            listing.CategoryId, listing.Status, listing.CreatedAtUtc);
}

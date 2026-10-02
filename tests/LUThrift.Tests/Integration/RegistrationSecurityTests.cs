using System.Net;
using System.Text.RegularExpressions;
using LUThrift.Web.Authorization;
using LUThrift.Web.Data;
using LUThrift.Web.Models;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace LUThrift.Tests.Integration;

public class RegistrationSecurityTests(AuthorizationWebApplicationFactory factory)
    : IClassFixture<AuthorizationWebApplicationFactory>
{
    private const string RegisterPath = "/Identity/Account/Register";

    [Theory]
    [InlineData(ApplicationRoles.Staff)]
    [InlineData(ApplicationRoles.Administrator)]
    public async Task Registration_IgnoresPrivilegedRoleAndAccountFieldsInFormAndQuery(string requestedRole)
    {
        using var client = await factory.CreateIdentityClientAsync();
        var token = await ReadRegistrationTokenAsync(client);
        var email = IdentityTestContext.NewEmail();
        var forgedId = Guid.NewGuid().ToString();
        var form = CreateRegistrationForm(email);
        form["__RequestVerificationToken"] = token;
        form["Role"] = requestedRole;
        form["Roles[0]"] = requestedRole;
        form["Input.Role"] = requestedRole;
        form["Input.Roles[0]"] = requestedRole;
        form["Id"] = form["Input.Id"] = forgedId;
        form["IsActive"] = form["Input.IsActive"] = "false";
        form["CreatedAtUtc"] = form["Input.CreatedAtUtc"] = "2000-01-01T00:00:00Z";
        var path = QueryHelpers.AddQueryString(RegisterPath, new Dictionary<string, string?>
        {
            ["role"] = requestedRole,
            ["roles"] = requestedRole,
            ["Input.Role"] = requestedRole
        });
        var startedAt = DateTime.UtcNow;
        using var content = new FormUrlEncodedContent(form);

        using var response = await client.PostAsync(path, content);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.NotNull(response.Headers.Location);
        Assert.Equal("/", new Uri(client.BaseAddress!, response.Headers.Location).AbsolutePath);
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            var user = await users.FindByEmailAsync(email);
            Assert.NotNull(user);
            Assert.Equal(new[] { ApplicationRoles.Student }, await users.GetRolesAsync(user));
            Assert.NotEqual(forgedId, user.Id);
            Assert.True(user.IsActive);
            Assert.InRange(user.CreatedAtUtc, startedAt, DateTime.UtcNow);
        }

        foreach (var protectedPath in new[] { "/Staff", "/Admin" })
        {
            using var denied = await client.GetAsync(protectedPath);
            Assert.Equal(HttpStatusCode.Redirect, denied.StatusCode);
            Assert.NotNull(denied.Headers.Location);
            var location = new Uri(client.BaseAddress!, denied.Headers.Location);
            // AccessDenied confirms the registration cookie authenticated the user, without privileged access.
            Assert.Equal("/Identity/Account/AccessDenied", location.AbsolutePath);
            Assert.Equal(protectedPath, QueryHelpers.ParseQuery(location.Query)["ReturnUrl"].ToString());
        }
    }

    [Fact]
    public async Task Registration_WithoutAntiforgeryTokenRejectsRequestAndCreatesNoAccount()
    {
        using var client = await factory.CreateIdentityClientAsync();
        using var page = await client.GetAsync(RegisterPath);
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        var email = IdentityTestContext.NewEmail();
        using var content = new FormUrlEncodedContent(CreateRegistrationForm(email));

        using var response = await client.PostAsync(RegisterPath, content);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await using var scope = factory.Services.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        Assert.False(await database.Users.AnyAsync(user => user.Email == email));
    }

    private static async Task<string> ReadRegistrationTokenAsync(HttpClient client)
    {
        using var response = await client.GetAsync(RegisterPath);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var html = await response.Content.ReadAsStringAsync();
        var form = Assert.Single(Regex.Matches(html, @"<form\b[^>]*>[\s\S]*?</form>", RegexOptions.IgnoreCase)
            .Cast<Match>()).Value;
        var controls = Regex.Matches(form, @"<(?:input|select|textarea)\b[^>]*>", RegexOptions.IgnoreCase)
            .Cast<Match>()
            .Select(match => match.Value)
            .ToArray();
        var names = controls.Select(control => ReadAttribute(control, "name"))
            .Where(name => name.Length > 0)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(new[]
        {
            "Input.ConfirmPassword", "Input.DisplayName", "Input.Email", "Input.Password", "__RequestVerificationToken"
        }, names);
        Assert.DoesNotMatch(@"(?i)<select\b", form);

        var tokenControl = Assert.Single(controls, control => ReadAttribute(control, "name") == "__RequestVerificationToken");
        var token = ReadAttribute(tokenControl, "value");
        Assert.NotEmpty(token);
        return token;
    }

    private static string ReadAttribute(string element, string attribute)
        => WebUtility.HtmlDecode(Regex.Match(element, $"\\b{attribute}=\"([^\"]*)\"", RegexOptions.IgnoreCase)
            .Groups[1].Value);

    private static Dictionary<string, string> CreateRegistrationForm(string email)
    {
        var password = IdentityTestContext.NewPassword();
        return new Dictionary<string, string>
        {
            ["Input.DisplayName"] = "Security test student",
            ["Input.Email"] = email,
            ["Input.Password"] = password,
            ["Input.ConfirmPassword"] = password
        };
    }
}

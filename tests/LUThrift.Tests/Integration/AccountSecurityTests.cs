using System.Net;
using System.Text.RegularExpressions;
using LUThrift.Web.Authorization;
using LUThrift.Web.Models;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;

namespace LUThrift.Tests.Integration;

public class AccountSecurityTests(AuthorizationWebApplicationFactory factory)
    : IClassFixture<AuthorizationWebApplicationFactory>
{
    [Fact]
    public async Task Login_InvalidCredentialsUseSameErrorAndDoNotAuthenticate()
    {
        var account = await CreateAccountAsync(ApplicationRoles.Student);

        foreach (var email in new[] { account.Email, IdentityTestContext.NewEmail() })
        {
            using var client = await factory.CreateIdentityClientAsync();
            using var loginPage = await client.GetAsync("/Identity/Account/Login");
            Assert.Equal(HttpStatusCode.OK, loginPage.StatusCode);
            var token = AntiforgeryToken(await loginPage.Content.ReadAsStringAsync());

            using var response = await client.PostAsync("/Identity/Account/Login", new FormUrlEncodedContent(
                new Dictionary<string, string>
                {
                    ["Input.Email"] = email,
                    ["Input.Password"] = IdentityTestContext.NewPassword(),
                    ["__RequestVerificationToken"] = token
                }));

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Null(response.Headers.Location);
            Assert.Equal("Invalid email or password.", LoginError(await response.Content.ReadAsStringAsync()));
            await AssertRedirectsToLoginAsync(client);
        }
    }

    [Fact]
    public async Task Logout_RequiresAntiforgeryPostAndLoginRejectsExternalReturnUrl()
    {
        var account = await CreateAccountAsync(ApplicationRoles.Staff);
        using var client = await factory.CreateIdentityClientAsync();
        var loginPath = "/Identity/Account/Login?returnUrl="
            + Uri.EscapeDataString("https://example.invalid/redirect");
        using var loginPage = await client.GetAsync(loginPath);
        Assert.Equal(HttpStatusCode.OK, loginPage.StatusCode);
        var loginToken = AntiforgeryToken(await loginPage.Content.ReadAsStringAsync());

        using var login = await client.PostAsync(loginPath, new FormUrlEncodedContent(
            new Dictionary<string, string>
            {
                ["Input.Email"] = account.Email,
                ["Input.Password"] = account.Password,
                ["__RequestVerificationToken"] = loginToken
            }));

        Assert.Equal(HttpStatusCode.Redirect, login.StatusCode);
        Assert.Equal("/", login.Headers.Location?.OriginalString);
        await AssertStaffAccessAsync(client);

        using var logoutPage = await client.GetAsync("/Identity/Account/Logout");
        Assert.Equal(HttpStatusCode.OK, logoutPage.StatusCode);
        var logoutToken = AntiforgeryToken(await logoutPage.Content.ReadAsStringAsync());
        await AssertStaffAccessAsync(client);

        using var rejectedLogout = await client.PostAsync("/Identity/Account/Logout",
            new FormUrlEncodedContent(new Dictionary<string, string>()));
        Assert.Equal(HttpStatusCode.BadRequest, rejectedLogout.StatusCode);
        await AssertStaffAccessAsync(client);

        using var logout = await client.PostAsync("/Identity/Account/Logout", new FormUrlEncodedContent(
            new Dictionary<string, string> { ["__RequestVerificationToken"] = logoutToken }));

        Assert.Equal(HttpStatusCode.Redirect, logout.StatusCode);
        Assert.Equal("/", logout.Headers.Location?.OriginalString);
        await AssertRedirectsToLoginAsync(client);
    }

    private async Task<(string Email, string Password)> CreateAccountAsync(string role)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var email = IdentityTestContext.NewEmail();
        var password = IdentityTestContext.NewPassword();
        var user = new ApplicationUser
        {
            DisplayName = "Account security test",
            Email = email,
            UserName = email
        };

        Assert.True((await users.CreateAsync(user, password)).Succeeded);
        Assert.True((await users.AddToRoleAsync(user, role)).Succeeded);
        return (email, password);
    }

    private static async Task AssertStaffAccessAsync(HttpClient client)
    {
        using var response = await client.GetAsync("/Staff");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private static async Task AssertRedirectsToLoginAsync(HttpClient client)
    {
        using var response = await client.GetAsync("/Staff");
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.NotNull(response.Headers.Location);
        Assert.Equal("/Identity/Account/Login",
            new Uri(client.BaseAddress!, response.Headers.Location).AbsolutePath);
    }

    private static string AntiforgeryToken(string html)
    {
        var match = Regex.Match(html,
            "<input\\b[^>]*name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"",
            RegexOptions.IgnoreCase);
        Assert.True(match.Success, "Expected an antiforgery token in the form.");
        return WebUtility.HtmlDecode(match.Groups[1].Value);
    }

    private static string LoginError(string html)
    {
        var summary = Regex.Match(html,
            "<div\\b[^>]*role=\"alert\"[^>]*>([\\s\\S]*?)</div>",
            RegexOptions.IgnoreCase);
        Assert.True(summary.Success, "Expected a login validation summary.");
        var text = WebUtility.HtmlDecode(Regex.Replace(summary.Groups[1].Value, "<[^>]+>", string.Empty));
        return Regex.Replace(text, "\\s+", " ").Trim();
    }
}

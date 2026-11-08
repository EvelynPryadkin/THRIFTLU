using System.Net;
using System.Text.RegularExpressions;
using LUThrift.Web.Authorization;
using LUThrift.Web.Data;
using LUThrift.Web.Models;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Microsoft.Net.Http.Headers;

namespace LUThrift.Tests.Integration;

public sealed class RequestSecurityTests
{
    private const string LoginPath = "/Identity/Account/Login";
    private const string RegisterPath = "/Identity/Account/Register";

    [Theory]
    [InlineData("Production", "https://localhost", true)]
    [InlineData("Development", "https://localhost", true)]
    [InlineData("Development", "http://localhost", false)]
    public async Task Login_IssuesProtectedCookieAndPreservesTheLocalReturnUrl(
        string environment, string address, bool secure)
    {
        await using var host = SecurityHost.Create(environment, address);
        Assert.Equal(environment, host.Services.GetRequiredService<IHostEnvironment>().EnvironmentName);
        var account = await host.CreateAccountAsync();
        var loginPath = LoginPath + "?returnUrl=" + Uri.EscapeDataString("/Account/Reservations");
        var token = await TokenAsync(host.Client, loginPath);

        using var login = await LoginAsync(host.Client, loginPath, account.Email, account.Password, token);

        Assert.Equal(HttpStatusCode.Redirect, login.StatusCode);
        Assert.Equal("/Account/Reservations", login.Headers.Location!.OriginalString);
        var cookie = ApplicationCookie(login, host.Services);
        Assert.Equal(secure, cookie.Secure);
        Assert.True(cookie.HttpOnly);
        Assert.Equal(Microsoft.Net.Http.Headers.SameSiteMode.Lax, cookie.SameSite);
        Assert.Equal("/", cookie.Path.Value);
        AssertSecurityHeaders(login);
        using var accountPage = await host.Client.GetAsync("/Account/Reservations");
        Assert.Equal(HttpStatusCode.OK, accountPage.StatusCode);
    }

    [Theory]
    [InlineData("Production", CookieSecurePolicy.Always)]
    [InlineData("Development", CookieSecurePolicy.SameAsRequest)]
    public async Task EveryIdentityCookie_UsesTheConfiguredSecurityDefaults(
        string environment, CookieSecurePolicy securePolicy)
    {
        await using var host = SecurityHost.Create(environment);
        var cookies = host.Services.GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>();
        foreach (var scheme in new[]
        {
            IdentityConstants.ApplicationScheme, IdentityConstants.ExternalScheme,
            IdentityConstants.TwoFactorRememberMeScheme, IdentityConstants.TwoFactorUserIdScheme
        })
        {
            var cookie = cookies.Get(scheme).Cookie;
            Assert.True(cookie.HttpOnly);
            Assert.Equal(Microsoft.AspNetCore.Http.SameSiteMode.Lax, cookie.SameSite);
            Assert.Equal(securePolicy, cookie.SecurePolicy);
        }
        var application = cookies.Get(IdentityConstants.ApplicationScheme);
        Assert.Equal("/Identity/Account/Login", application.LoginPath.Value);
        Assert.Equal("/Identity/Account/AccessDenied", application.AccessDeniedPath.Value);
    }

    [Fact]
    public async Task Registration_InProductionSetsProtectedCookieAndRejectsExternalReturnUrl()
    {
        await using var host = SecurityHost.Create("Production");
        var path = RegisterPath + "?returnUrl=" + Uri.EscapeDataString("https://example.invalid/external");
        var email = IdentityTestContext.NewEmail();
        var password = IdentityTestContext.NewPassword();
        var token = await TokenAsync(host.Client, path);
        using var response = await host.Client.PostAsync(path, new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["Input.DisplayName"] = "Synthetic request security student",
            ["Input.Email"] = email,
            ["Input.Password"] = password,
            ["Input.ConfirmPassword"] = password,
            ["__RequestVerificationToken"] = token
        }));

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/", response.Headers.Location!.OriginalString);
        var cookie = ApplicationCookie(response, host.Services);
        Assert.True(cookie.Secure);
        Assert.True(cookie.HttpOnly);
        Assert.Equal(Microsoft.Net.Http.Headers.SameSiteMode.Lax, cookie.SameSite);
        await using var scope = host.Services.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var account = await users.FindByEmailAsync(email);
        Assert.NotNull(account);
        Assert.Equal(new[] { ApplicationRoles.Student }, await users.GetRolesAsync(account));
    }

    [Fact]
    public async Task LoginLockout_StillPersistsFailuresAndUsesGenericFeedbackWithoutAuthentication()
    {
        await using var host = SecurityHost.Create("Production");
        var account = await host.CreateAccountAsync();
        var token = await TokenAsync(host.Client, LoginPath);
        var options = host.Services.GetRequiredService<IOptions<IdentityOptions>>().Value;
        Assert.Equal(5, options.Lockout.MaxFailedAccessAttempts);
        Assert.True(options.Lockout.AllowedForNewUsers);
        var wrongPassword = IdentityTestContext.NewPassword();

        for (var attempt = 1; attempt <= options.Lockout.MaxFailedAccessAttempts; attempt++)
        {
            using var rejected = await LoginAsync(host.Client, LoginPath, account.Email, wrongPassword, token);
            Assert.Equal(HttpStatusCode.OK, rejected.StatusCode);
            Assert.Contains("Invalid email or password.", await rejected.Content.ReadAsStringAsync());
            AssertNoApplicationCookie(rejected, host.Services);
            await using var scope = host.Services.CreateAsyncScope();
            var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            var saved = (await users.FindByIdAsync(account.Id))!;
            if (attempt < options.Lockout.MaxFailedAccessAttempts)
            {
                Assert.Equal(attempt, await users.GetAccessFailedCountAsync(saved));
                Assert.False(await users.IsLockedOutAsync(saved));
            }
            else
            {
                Assert.True(await users.IsLockedOutAsync(saved));
                Assert.True(await users.GetLockoutEndDateAsync(saved) > DateTimeOffset.UtcNow);
            }
        }

        // The sixth request is below the default login request limit. Identity's
        // account lockout, rather than rate limiting, must reject the correct password.
        using var correctButLocked = await LoginAsync(host.Client, LoginPath, account.Email, account.Password, token);
        Assert.Equal(HttpStatusCode.OK, correctButLocked.StatusCode);
        Assert.Contains("Invalid email or password.", await correctButLocked.Content.ReadAsStringAsync());
        AssertNoApplicationCookie(correctButLocked, host.Services);
        using var privatePage = await host.Client.GetAsync("/Account/Reservations");
        Assert.Equal(HttpStatusCode.Redirect, privatePage.StatusCode);
        Assert.Contains(LoginPath, privatePage.Headers.Location!.OriginalString);
    }

    [Theory]
    [InlineData("/", HttpStatusCode.OK)]
    [InlineData(LoginPath, HttpStatusCode.OK)]
    [InlineData("/Thrift/Image/2147483647", HttpStatusCode.NotFound)]
    [InlineData("/missing-request-security-route", HttpStatusCode.NotFound)]
    [InlineData("/Account/Reservations", HttpStatusCode.Redirect)]
    public async Task Responses_HaveContentTypeReferrerAndFramingProtection(string path, HttpStatusCode status)
    {
        await using var host = SecurityHost.Create("Production");
        using var response = await host.Client.GetAsync(path);

        Assert.Equal(status, response.StatusCode);
        AssertSecurityHeaders(response);
        if (path == LoginPath)
        {
            // Rendering this form generates antiforgery cookies/headers. Its
            // SAMEORIGIN default must not overwrite the app's DENY protection.
            Assert.NotEmpty(Token(await response.Content.ReadAsStringAsync()));
        }
    }

    [Fact]
    public async Task DevelopmentStaticCss_RemainsAvailableWithSecurityHeaders()
    {
        await using var host = SecurityHost.Create("Development");
        var html = await host.Client.GetStringAsync("/");
        var css = Regex.Match(html, "href=\"([^\"]*/css/site[^\"]*\\.css[^\"]*)\"", RegexOptions.IgnoreCase);
        Assert.True(css.Success, "The page should reference its local site stylesheet.");

        using var response = await host.Client.GetAsync(WebUtility.HtmlDecode(css.Groups[1].Value));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/css", response.Content.Headers.ContentType!.MediaType);
        AssertSecurityHeaders(response);
    }

    private static void AssertSecurityHeaders(HttpResponseMessage response)
    {
        Assert.Equal("nosniff", Assert.Single(response.Headers.GetValues("X-Content-Type-Options")));
        Assert.Equal("strict-origin-when-cross-origin", Assert.Single(response.Headers.GetValues("Referrer-Policy")));
        Assert.Equal("DENY", Assert.Single(response.Headers.GetValues("X-Frame-Options")));
        Assert.Equal("frame-ancestors 'none'", Assert.Single(response.Headers.GetValues("Content-Security-Policy")));
    }

    private static SetCookieHeaderValue ApplicationCookie(HttpResponseMessage response, IServiceProvider services)
    {
        var name = services.GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>()
            .Get(IdentityConstants.ApplicationScheme).Cookie.Name;
        var cookies = response.Headers.TryGetValues(HeaderNames.SetCookie, out var values)
            ? values.Select(value => SetCookieHeaderValue.Parse(value)).Where(cookie => cookie.Name.Value == name).ToArray() : [];
        // Assert only names/attributes, never include an authentication token in failure output.
        Assert.Single(cookies.Select(cookie => cookie.Name.Value));
        return cookies[0];
    }

    private static void AssertNoApplicationCookie(HttpResponseMessage response, IServiceProvider services)
    {
        var name = services.GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>()
            .Get(IdentityConstants.ApplicationScheme).Cookie.Name;
        var count = response.Headers.TryGetValues(HeaderNames.SetCookie, out var values)
            ? values.Select(value => SetCookieHeaderValue.Parse(value)).Count(cookie => cookie.Name.Value == name) : 0;
        Assert.Equal(0, count);
    }

    private static async Task<string> TokenAsync(HttpClient client, string path)
    {
        using var response = await client.GetAsync(path);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return Token(await response.Content.ReadAsStringAsync());
    }

    private static string Token(string html)
    {
        var match = Regex.Match(html, "<input\\b[^>]*name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"",
            RegexOptions.IgnoreCase);
        Assert.True(match.Success, "The page should render an antiforgery token.");
        return WebUtility.HtmlDecode(match.Groups[1].Value);
    }

    private static Task<HttpResponseMessage> LoginAsync(HttpClient client, string path, string email, string password, string token)
        => client.PostAsync(path, new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["Input.Email"] = email, ["Input.Password"] = password, ["__RequestVerificationToken"] = token
        }));

    private sealed record Account(string Id, string Email, string Password);
    private sealed class SecurityHost(AuthorizationWebApplicationFactory original,
        WebApplicationFactory<ApplicationDbContext> factory, HttpClient client) : IAsyncDisposable
    {
        public IServiceProvider Services => factory.Services;
        public HttpClient Client { get; } = client;

        public static SecurityHost Create(string environment, string address = "https://localhost")
        {
            var original = new AuthorizationWebApplicationFactory();
            var factory = original.WithWebHostBuilder(builder =>
            {
                builder.UseEnvironment(environment);
                builder.UseStaticWebAssets();
                builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(
                    new Dictionary<string, string?> { ["DevelopmentAdmin:Email"] = string.Empty }));
            });
            var client = factory.CreateClient(new WebApplicationFactoryClientOptions
            {
                AllowAutoRedirect = false, BaseAddress = new Uri(address)
            });
            return new(original, factory, client);
        }

        public async Task<Account> CreateAccountAsync()
        {
            await using var scope = Services.CreateAsyncScope();
            var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            var email = IdentityTestContext.NewEmail();
            var password = IdentityTestContext.NewPassword();
            var user = new ApplicationUser { DisplayName = "Synthetic request security student", Email = email, UserName = email };
            Assert.True((await users.CreateAsync(user, password)).Succeeded);
            Assert.True((await users.AddToRoleAsync(user, ApplicationRoles.Student)).Succeeded);
            return new(user.Id, email, password);
        }

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await factory.DisposeAsync();
            await original.DisposeAsync();
        }
    }
}

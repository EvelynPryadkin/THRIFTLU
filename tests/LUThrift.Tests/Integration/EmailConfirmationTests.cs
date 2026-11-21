using System.Globalization;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using LUThrift.Web.Authorization;
using LUThrift.Web.Configuration;
using LUThrift.Web.Data;
using LUThrift.Web.Models;
using LUThrift.Web.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.UI.Services;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace LUThrift.Tests.Integration;

// Synthetic accounts and a fake sender only; no email is delivered by these tests.
public sealed class EmailConfirmationTests
{
    private const string Register = "/Identity/Account/Register";
    private const string Login = "/Identity/Account/Login";
    private const string Resend = "/Identity/Account/ResendEmailConfirmation";

    [Fact]
    public async Task Registration_RequiresMailboxConfirmationAndUsesTrustedCallbackOrigin()
    {
        await using var test = new Fixture();
        var email = IdentityTestContext.NewCampusEmail();
        var password = IdentityTestContext.NewPassword();
        var fields = Registration(email, password, await TokenAsync(test.Client, Register));
        fields["Input.EmailConfirmed"] = "true";
        fields["Input.EmailConfirmationRequiredForSignIn"] = "false";
        using var request = new HttpRequestMessage(HttpMethod.Post, Register + "?returnUrl=%2FThrift")
        { Content = new FormUrlEncodedContent(fields) };
        request.Headers.Host = "untrusted.example.invalid";
        request.Headers.Add("X-Forwarded-Host", "forged.example.invalid");
        using var registered = await test.Client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Redirect, registered.StatusCode);
        Assert.StartsWith("/Identity/Account/RegisterConfirmation", registered.Headers.Location!.OriginalString);
        var message = Assert.Single(test.Sender.Messages);
        var callback = Callback(message.Html);
        Assert.Equal("https://confirmed.example.test", callback.GetLeftPart(UriPartial.Authority));
        var query = QueryHelpers.ParseQuery(callback.Query);
        Assert.Equal("/Thrift", query["returnUrl"]);
        Assert.False(string.IsNullOrEmpty(query["code"]));
        await using var scope = test.Factory.Services.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var account = (await users.FindByEmailAsync(email))!;
        Assert.False(account.EmailConfirmed);
        Assert.True(account.EmailConfirmationRequiredForSignIn);
        Assert.Equal(new[] { ApplicationRoles.Student }, await users.GetRolesAsync(account));
        var normalPage = await test.Client.GetStringAsync(registered.Headers.Location);
        Assert.DoesNotContain(query["code"].ToString(), normalPage);
        Assert.DoesNotContain("userId=", normalPage);
        Assert.DoesNotContain("code=", normalPage);
        using var denied = await SignInAsync(test.Client, email, password);
        Assert.Equal(HttpStatusCode.OK, denied.StatusCode);
        Assert.Contains("Invalid email or password.", await denied.Content.ReadAsStringAsync());

        using var confirmed = await test.Client.GetAsync(callback.PathAndQuery);
        Assert.Equal(HttpStatusCode.Redirect, confirmed.StatusCode);
        Assert.Equal("no-referrer", confirmed.Headers.GetValues("Referrer-Policy").Single());
        Assert.DoesNotContain("code=", confirmed.Headers.Location!.OriginalString);
        Assert.DoesNotContain("userId=", confirmed.Headers.Location.OriginalString);
        Assert.Contains("Your email is confirmed", await test.Client.GetStringAsync(confirmed.Headers.Location));
        using var stillAnonymous = await test.Client.GetAsync("/Account/Reservations");
        Assert.Contains("/Identity/Account/Login", stillAnonymous.Headers.Location!.OriginalString);
        using var signedIn = await SignInAsync(test.Client, email, password, "/Thrift");
        Assert.Equal(HttpStatusCode.Redirect, signedIn.StatusCode);
        Assert.Equal("/Thrift", signedIn.Headers.Location!.OriginalString);
        scope.ServiceProvider.GetRequiredService<ApplicationDbContext>().ChangeTracker.Clear();
        Assert.True((await users.FindByIdAsync(account.Id))!.EmailConfirmed);
    }

    [Fact]
    public async Task RepeatedConfirmation_PreservesFirstResultWithoutSavingAgainOrSigningIn()
    {
        var saves = new SaveCounter();
        await using var test = new Fixture(saves);
        var account = await test.AddAccountAsync(required: true);
        await using var scope = test.Factory.Services.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var user = (await users.FindByIdAsync(account.Id))!;
        var code = WebEncoders.Base64UrlEncode(Encoding.UTF8.GetBytes(await users.GenerateEmailConfirmationTokenAsync(user)));
        var path = QueryHelpers.AddQueryString("/Identity/Account/ConfirmEmail", new Dictionary<string, string?>
        { ["userId"] = account.Id, ["code"] = code });
        var beforeConfirmation = saves.Count;
        using var first = await test.Client.GetAsync(path);
        Assert.Equal(HttpStatusCode.Redirect, first.StatusCode);
        Assert.Contains("Your email is confirmed", await test.Client.GetStringAsync(first.Headers.Location!));
        Assert.True(saves.Count > beforeConfirmation);
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var confirmed = await db.Users.AsNoTracking().SingleAsync(row => row.Id == account.Id);
        Assert.True(confirmed.EmailConfirmed);
        var afterFirstConfirmation = saves.Count;

        using var repeated = await test.Client.GetAsync(path);

        Assert.Equal(HttpStatusCode.Redirect, repeated.StatusCode);
        Assert.Contains("Your email is confirmed", await test.Client.GetStringAsync(repeated.Headers.Location!));
        Assert.Equal(afterFirstConfirmation, saves.Count);
        var unchanged = await db.Users.AsNoTracking().SingleAsync(row => row.Id == account.Id);
        Assert.True(unchanged.EmailConfirmed);
        Assert.Equal(confirmed.SecurityStamp, unchanged.SecurityStamp);
        Assert.Equal(confirmed.ConcurrencyStamp, unchanged.ConcurrencyStamp);
        Assert.Equal(confirmed.CreatedAtUtc, unchanged.CreatedAtUtc);
        using var privatePage = await test.Client.GetAsync("/Account/Reservations");
        Assert.Equal(HttpStatusCode.Redirect, privatePage.StatusCode);
        Assert.Contains(Login, privatePage.Headers.Location!.OriginalString);
    }

    [Theory]
    [InlineData("synthetic@gmail.com")]
    [InlineData("synthetic@yahoo.com")]
    [InlineData("synthetic@students.lawrence.edu")]
    [InlineData("synthetic@lawrence.edu.example.invalid")]
    public async Task NewRegistration_RejectsNonCampusDomainsWithoutAccountOrEmail(string email)
    {
        await using var test = new Fixture();
        using var response = await test.Client.PostAsync(Register, new FormUrlEncodedContent(
            Registration(email, IdentityTestContext.NewPassword(), await TokenAsync(test.Client, Register))));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("Use your @lawrence.edu", await response.Content.ReadAsStringAsync());
        await using var scope = test.Factory.Services.CreateAsyncScope();
        Assert.Empty(await scope.ServiceProvider.GetRequiredService<ApplicationDbContext>().Users.ToArrayAsync());
        Assert.Empty(test.Sender.Messages);
    }

    [Theory]
    [InlineData("bad")]
    [InlineData("wrong-user")]
    [InlineData("expired")]
    [InlineData("oversized")]
    public async Task InvalidConfirmation_DoesNotConfirmOrSignIn(string scenario)
    {
        await using var test = new Fixture();
        var account = await test.AddAccountAsync(required: true);
        await using var scope = test.Factory.Services.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var user = (await users.FindByIdAsync(account.Id))!;
        var code = WebEncoders.Base64UrlEncode(Encoding.UTF8.GetBytes(await users.GenerateEmailConfirmationTokenAsync(user)));
        if (scenario == "bad") code = "!not-base64";
        if (scenario == "oversized") code = new string('x', 4097);
        if (scenario == "expired") scope.ServiceProvider.GetRequiredService<IOptions<DataProtectionTokenProviderOptions>>().Value.TokenLifespan = TimeSpan.FromSeconds(-1);
        using var response = await test.Client.GetAsync(QueryHelpers.AddQueryString("/Identity/Account/ConfirmEmail", new Dictionary<string, string?>
        { ["userId"] = scenario == "wrong-user" ? "missing-account" : account.Id, ["code"] = code, ["returnUrl"] = "https://evil.invalid" }));
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.DoesNotContain("evil.invalid", response.Headers.Location!.OriginalString);
        var html = await test.Client.GetStringAsync(response.Headers.Location);
        Assert.Contains("invalid or expired", html);
        Assert.DoesNotContain(code, html);
        scope.ServiceProvider.GetRequiredService<ApplicationDbContext>().ChangeTracker.Clear();
        Assert.False((await users.FindByIdAsync(account.Id))!.EmailConfirmed);
    }

    [Fact]
    public async Task DeliveryFailure_PreservesAccountAndAllowsSafeResendWithGenericResponses()
    {
        await using var test = new Fixture();
        test.Sender.Fail = true;
        var email = IdentityTestContext.NewCampusEmail();
        using var register = await test.Client.PostAsync(Register, new FormUrlEncodedContent(
            Registration(email, IdentityTestContext.NewPassword(), await TokenAsync(test.Client, Register))));
        Assert.Equal(HttpStatusCode.Redirect, register.StatusCode);
        var html = await test.Client.GetStringAsync(register.Headers.Location!);
        Assert.Contains("Your account was created", html);
        Assert.Contains("request another email", html);
        Assert.DoesNotContain("synthetic-provider-secret", html);
        var failedResend = await ResendAsync(test, email);
        test.Sender.Fail = false;
        var sentResend = await ResendAsync(test, email);
        var unknownResend = await ResendAsync(test, IdentityTestContext.NewCampusEmail());
        Assert.Equal(StatusMessage(failedResend), StatusMessage(sentResend));
        Assert.Equal(StatusMessage(sentResend), StatusMessage(unknownResend));
        Assert.Single(test.Sender.Messages);
        await using var scope = test.Factory.Services.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var user = (await users.FindByEmailAsync(email))!;
        Assert.False(user.EmailConfirmed);
        Assert.Equal(new[] { ApplicationRoles.Student }, await users.GetRolesAsync(user));
        Assert.Single(await scope.ServiceProvider.GetRequiredService<ApplicationDbContext>().Users.ToArrayAsync());
    }

    [Fact]
    public async Task ExistingAccounts_KeepSignInAndManagementButStudentsNeedVerifiedCampusEmail()
    {
        await using var test = new Fixture();
        var student = await test.AddAccountAsync(required: false);
        using var login = await SignInAsync(test.Client, student.Email, student.Password);
        Assert.Equal(HttpStatusCode.Redirect, login.StatusCode);
        await using var scope = test.Factory.Services.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var account = (await users.FindByIdAsync(student.Id))!;
        Assert.False(account.EmailConfirmed);
        var principal = await scope.ServiceProvider.GetRequiredService<SignInManager<ApplicationUser>>().CreateUserPrincipalAsync(account);
        Assert.False(await scope.ServiceProvider.GetRequiredService<ReservationService>().IsEligibleStudentAsync(principal));
        var code = await users.GenerateEmailConfirmationTokenAsync(account);
        Assert.True((await users.ConfirmEmailAsync(account, code)).Succeeded);
        Assert.True(await scope.ServiceProvider.GetRequiredService<ReservationService>().IsEligibleStudentAsync(principal));
        var staff = await test.AddAccountAsync(required: false, role: ApplicationRoles.Administrator, email: IdentityTestContext.NewEmail());
        using var other = test.NewClient();
        using var staffLogin = await SignInAsync(other, staff.Email, staff.Password, "/Admin");
        Assert.Equal(HttpStatusCode.Redirect, staffLogin.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await other.GetAsync("/Admin")).StatusCode);
        Assert.False((await users.FindByIdAsync(staff.Id))!.EmailConfirmed);
    }

    [Fact]
    public async Task Resend_RequiresAntiforgeryAndSkipsInactiveOrAlreadyConfirmedAccounts()
    {
        await using var test = new Fixture();
        var account = await test.AddAccountAsync(required: false);
        using var noToken = await test.Client.PostAsync(Resend, new FormUrlEncodedContent(new Dictionary<string, string> { ["Email"] = account.Email }));
        Assert.Equal(HttpStatusCode.BadRequest, noToken.StatusCode);
        await using var scope = test.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        await db.Users.Where(row => row.Id == account.Id).ExecuteUpdateAsync(s => s.SetProperty(u => u.IsActive, false));
        var inactive = await ResendAsync(test, account.Email);
        await db.Users.Where(row => row.Id == account.Id).ExecuteUpdateAsync(s => s.SetProperty(u => u.IsActive, true).SetProperty(u => u.EmailConfirmed, true));
        var confirmed = await ResendAsync(test, account.Email);
        Assert.Equal(StatusMessage(inactive), StatusMessage(confirmed));
        Assert.Empty(test.Sender.Messages);
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(false, false)]
    [InlineData(true, false)]
    public async Task ExistingStudent_WithoutVerifiedCampusEmailCannotCreateButCanCancelOwnHold(bool confirmed, bool campus)
    {
        await using var test = new Fixture();
        var account = await test.AddAccountAsync(required: false, email: campus ? IdentityTestContext.NewCampusEmail() : IdentityTestContext.NewEmail());
        await using var scope = test.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        await db.Users.Where(u => u.Id == account.Id).ExecuteUpdateAsync(s => s.SetProperty(u => u.EmailConfirmed, confirmed));
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var principal = await scope.ServiceProvider.GetRequiredService<SignInManager<ApplicationUser>>().CreateUserPrincipalAsync((await users.FindByIdAsync(account.Id))!);
        var reservations = scope.ServiceProvider.GetRequiredService<ReservationService>();
        var rejected = await reservations.CreateAsync(new() { ListingId = 1 }, principal);
        Assert.Equal(ReservationCreationStatus.StudentRequired, rejected.Status);
        Assert.Empty(await db.Reservations.ToArrayAsync());
        Assert.Empty(await db.AuditEvents.ToArrayAsync());
        await db.Listings.Where(l => l.Id == 1).ExecuteUpdateAsync(s => s.SetProperty(l => l.Status, ListingStatus.Held));
        var hold = new Reservation { UserId = account.Id, ListingId = 1, CreatedAtUtc = DateTime.UtcNow,
            ExpiresAtUtc = DateTime.UtcNow.AddHours(1), Status = ReservationStatus.Active };
        db.Reservations.Add(hold);
        await db.SaveChangesAsync();
        Assert.Equal(ReservationOperationStatus.Canceled, (await reservations.CancelAsync(hold.Id, principal)).Status);
    }

    [Fact]
    public async Task ResendRateLimit_RejectsFourthPostWithRetryGuidanceButLeavesGetAvailable()
    {
        await using var test = new Fixture();
        var token = await TokenAsync(test.Client, Resend);
        for (var attempt = 0; attempt < 3; attempt++)
            Assert.Equal(HttpStatusCode.Redirect, (await test.Client.PostAsync(Resend, new FormUrlEncodedContent(new Dictionary<string, string>
                { ["Email"] = IdentityTestContext.NewEmail(), ["__RequestVerificationToken"] = token }))).StatusCode);
        using var limited = await test.Client.PostAsync(Resend, new FormUrlEncodedContent(new Dictionary<string, string>
            { ["Email"] = IdentityTestContext.NewEmail(), ["__RequestVerificationToken"] = token }));
        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
        Assert.NotNull(limited.Headers.RetryAfter);
        Assert.Equal(HttpStatusCode.OK, (await test.Client.GetAsync(Resend)).StatusCode);
    }

    private static Dictionary<string, string> Registration(string email, string password, string token) => new()
    { ["Input.DisplayName"] = "Synthetic confirmation account", ["Input.Email"] = email, ["Input.Password"] = password,
        ["Input.ConfirmPassword"] = password, ["__RequestVerificationToken"] = token };
    private static async Task<string> TokenAsync(HttpClient client, string path)
        => WebUtility.HtmlDecode(Regex.Match(await client.GetStringAsync(path), "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"").Groups[1].Value);
    private static async Task<HttpResponseMessage> SignInAsync(HttpClient client, string email, string password, string returnUrl = "/")
        => await client.PostAsync(Login + "?returnUrl=" + Uri.EscapeDataString(returnUrl), new FormUrlEncodedContent(new Dictionary<string, string>
        { ["Input.Email"] = email, ["Input.Password"] = password, ["__RequestVerificationToken"] = await TokenAsync(client, Login) }));
    private static async Task<string> ResendAsync(Fixture test, string email)
    {
        using var response = await test.Client.PostAsync(Resend, new FormUrlEncodedContent(new Dictionary<string, string>
        { ["Email"] = email, ["__RequestVerificationToken"] = await TokenAsync(test.Client, Resend) }));
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        return await test.Client.GetStringAsync(response.Headers.Location!);
    }
    private static string StatusMessage(string html) => Regex.Match(html, "<p role=\"status\">(.*?)</p>", RegexOptions.Singleline).Groups[1].Value;
    private static Uri Callback(string html) => new(WebUtility.HtmlDecode(Regex.Match(html, "href=\"([^\"]+)\"").Groups[1].Value));

    private sealed class FakeSender : IEmailSender
    {
        public bool Fail { get; set; }
        public List<(string Email, string Html)> Messages { get; } = [];
        public Task SendEmailAsync(string email, string subject, string htmlMessage)
        {
            if (Fail) throw new InvalidOperationException("synthetic-provider-secret");
            Messages.Add((email, htmlMessage));
            return Task.CompletedTask;
        }
    }
    private sealed class Fixture : IAsyncDisposable
    {
        private readonly AuthorizationWebApplicationFactory original = new();
        public FakeSender Sender { get; } = new();
        public WebApplicationFactory<ApplicationDbContext> Factory { get; }
        public HttpClient Client { get; }
        public Fixture(SaveChangesInterceptor? saveInterceptor = null)
        {
            Factory = original.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
            {
                services.RemoveAll<IEmailSender>();
                services.AddSingleton<IEmailSender>(Sender);
                services.PostConfigure<EmailConfirmationOptions>(o => o.PublicBaseUrl = "https://confirmed.example.test");
                if (saveInterceptor is not null)
                    services.ConfigureDbContext<ApplicationDbContext>(options => options.AddInterceptors(saveInterceptor));
            }));
            Client = NewClient();
        }
        public HttpClient NewClient() => Factory.CreateClient(new WebApplicationFactoryClientOptions
        { AllowAutoRedirect = false, BaseAddress = new Uri("https://localhost") });
        public async Task<(string Id, string Email, string Password)> AddAccountAsync(bool required, string role = ApplicationRoles.Student, string? email = null)
        {
            await using var scope = Factory.Services.CreateAsyncScope();
            var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            email ??= IdentityTestContext.NewCampusEmail();
            var password = IdentityTestContext.NewPassword();
            var user = new ApplicationUser { UserName = email, Email = email, DisplayName = "Synthetic existing account", EmailConfirmationRequiredForSignIn = required };
            Assert.True((await users.CreateAsync(user, password)).Succeeded);
            Assert.True((await users.AddToRoleAsync(user, role)).Succeeded);
            return (user.Id, email, password);
        }
        public async ValueTask DisposeAsync() { Client.Dispose(); await Factory.DisposeAsync(); await original.DisposeAsync(); }
    }

    private sealed class SaveCounter : SaveChangesInterceptor
    {
        private int count;
        public int Count => Volatile.Read(ref count);
        public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
        {
            Interlocked.Increment(ref count);
            return result;
        }
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref count);
            return ValueTask.FromResult(result);
        }
    }
}

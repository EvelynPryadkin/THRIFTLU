using System.Net;
using System.Text.RegularExpressions;
using LUThrift.Web.Authorization;
using LUThrift.Web.Data;
using LUThrift.Web.Models;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace LUThrift.Tests.Integration;

public class ReservationAccountPageTests
{
    private const string PagePath = "/Account/Reservations";
    private static readonly DateTimeOffset Now = new(2026, 10, 26, 15, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(null)]
    [InlineData(ApplicationRoles.Student)]
    [InlineData(ApplicationRoles.Staff)]
    [InlineData(ApplicationRoles.Administrator)]
    public async Task AccountPage_RequiresStudentAndNavigationMatchesAuthentication(string? role)
    {
        await using var test = await AccountTest.CreateAsync(role);
        using var home = await test.Client.GetAsync("/");
        var navigation = await home.Content.ReadAsStringAsync();
        Assert.Equal(role == ApplicationRoles.Student, navigation.Contains("My reservations", StringComparison.Ordinal));

        using var response = await test.Client.GetAsync(PagePath);
        if (role == ApplicationRoles.Student)
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var page = await response.Content.ReadAsStringAsync();
            Assert.Contains("You have no current reservations.", page);
            Assert.Contains("No recent reservations yet.", page);
        }
        else
        {
            Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
            Assert.NotNull(response.Headers.Location);
            Assert.Equal(role is null ? "/Identity/Account/Login" : "/Identity/Account/AccessDenied",
                new Uri(test.Client.BaseAddress!, response.Headers.Location).AbsolutePath);
        }
    }

    [Fact]
    public async Task Get_ShowsOnlyOwnReservationsAndEffectiveExpiryWithoutWriting()
    {
        await using var test = await AccountTest.CreateAsync();
        var active = await test.SeedAsync("<b>Own active item</b>", Now.AddMinutes(20));
        var expired = await test.SeedAsync("Own expired item", Now);
        var otherUser = await test.AddOtherUserAsync();
        var other = await test.SeedAsync("Private other student's item", Now.AddMinutes(20), otherUser);

        using var response = await test.Client.GetAsync(PagePath + "?handler=Cancel&id=" + active.Id);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var main = MainContent(await response.Content.ReadAsStringAsync());
        Assert.Contains(active.Listing.Title, WebUtility.HtmlDecode(main));
        Assert.DoesNotContain(active.Listing.Title, main);
        Assert.DoesNotContain(other.Listing.Title, main);
        var activeCard = Card(main, active.Id);
        var expiredCard = Card(main, expired.Id);
        Assert.Contains("Cancel reservation", activeCard);
        Assert.Contains("Expired", expiredCard);
        Assert.DoesNotContain("<form", expiredCard, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("<dt>Deadline</dt>", main);
        Assert.Contains("2026-10-26 10:20:00 (America/Chicago, UTC-05:00)", WebUtility.HtmlDecode(activeCard));
        Assert.Contains("2026-10-26 10:00:00 (America/Chicago, UTC-05:00)", WebUtility.HtmlDecode(expiredCard));
        var times = Regex.Matches(main, "<time\\b[^>]*datetime=\"([^\"]+)\"", RegexOptions.IgnoreCase);
        Assert.Equal(4, times.Count);
        Assert.All(times.Cast<Match>(), time => Assert.EndsWith("Z", time.Groups[1].Value));

        foreach (var reservation in new[] { active, expired, other })
        {
            var state = await test.StateAsync(reservation.Id);
            Assert.Equal(ReservationStatus.Active, state.Status);
            Assert.Equal(ListingStatus.Held, state.ListingStatus);
        }
        Assert.Equal(0, await test.AuditCountAsync());
    }

    [Fact]
    public async Task Cancel_RequiresAntiforgeryAndIgnoresPostedOwnerStatusAndQueryId()
    {
        await using var test = await AccountTest.CreateAsync();
        var own = await test.SeedAsync("Own cancelable item", Now.AddMinutes(20));
        var other = await test.SeedAsync("Other student's item", Now.AddMinutes(20), await test.AddOtherUserAsync());
        var token = Token(await test.Client.GetStringAsync(PagePath));

        using var missingToken = await test.Client.PostAsync(PagePath + "?handler=Cancel",
            new FormUrlEncodedContent(new Dictionary<string, string> { ["id"] = own.Id.ToString() }));
        Assert.Equal(HttpStatusCode.BadRequest, missingToken.StatusCode);
        Assert.Equal(ReservationStatus.Active, (await test.StateAsync(own.Id)).Status);

        var fields = new Dictionary<string, string>
        {
            ["id"] = own.Id.ToString(), ["__RequestVerificationToken"] = token,
            ["UserId"] = other.UserId, ["ListingId"] = other.ListingId.ToString(),
            ["Status"] = "Fulfilled", ["ExpiresAtUtc"] = "2099-01-01T00:00:00Z",
            ["FulfilledAtUtc"] = "2099-01-01T00:00:00Z", ["CancellationReason"] = "Untrusted form value"
        };
        using var response = await test.Client.PostAsync(PagePath + "?handler=Cancel&id=" + other.Id,
            new FormUrlEncodedContent(fields));
        AssertPrg(response);
        var ownState = await test.StateAsync(own.Id);
        Assert.Equal(ReservationStatus.Canceled, ownState.Status);
        Assert.Equal(ListingStatus.Available, ownState.ListingStatus);
        Assert.Equal(own.ExpiresAtUtc, ownState.ExpiresAtUtc);
        Assert.Null(ownState.FulfilledAtUtc);
        Assert.NotEqual("Untrusted form value", ownState.CancellationReason);
        Assert.Equal(ReservationStatus.Active, (await test.StateAsync(other.Id)).Status);
        Assert.Equal(ListingStatus.Held, (await test.StateAsync(other.Id)).ListingStatus);
        Assert.Equal(1, await test.AuditCountAsync());

        using var repeated = await test.Client.PostAsync(PagePath + "?handler=Cancel", new FormUrlEncodedContent(fields));
        AssertPrg(repeated);
        Assert.Equal(1, await test.AuditCountAsync());
        Assert.Equal(ReservationStatus.Canceled, (await test.StateAsync(own.Id)).Status);
    }

    [Fact]
    public async Task Cancel_OtherOwnerAndUnknownIdReturnTheSameSafeMessageWithoutChanges()
    {
        await using var test = await AccountTest.CreateAsync();
        var other = await test.SeedAsync("Private other item", Now.AddMinutes(20), await test.AddOtherUserAsync());
        var token = Token(await test.Client.GetStringAsync(PagePath));
        var messages = new List<string>();
        foreach (var id in new[] { other.Id, int.MaxValue })
        {
            using var response = await test.Client.PostAsync(PagePath + "?handler=Cancel", new FormUrlEncodedContent(
                new Dictionary<string, string> { ["id"] = id.ToString(), ["__RequestVerificationToken"] = token }));
            AssertPrg(response);
            var main = MainContent(await test.Client.GetStringAsync(PagePath));
            var alert = Regex.Match(main, "<div\\b[^>]*role=\"status\"[^>]*>([\\s\\S]*?)</div>", RegexOptions.IgnoreCase);
            Assert.True(alert.Success);
            messages.Add(WebUtility.HtmlDecode(alert.Groups[1].Value).Trim());
            Assert.DoesNotContain(other.Listing.Title, main);
        }

        Assert.Equal(messages[0], messages[1]);
        Assert.Equal(ReservationStatus.Active, (await test.StateAsync(other.Id)).Status);
        Assert.Equal(ListingStatus.Held, (await test.StateAsync(other.Id)).ListingStatus);
        Assert.Equal(0, await test.AuditCountAsync());
    }

    [Fact]
    public async Task Cancel_AtDeadlineExpiresInsteadOfCanceling()
    {
        await using var test = await AccountTest.CreateAsync();
        var reservation = await test.SeedAsync("Expired before submitted cancellation", Now);
        var token = Token(await test.Client.GetStringAsync(PagePath));

        using var response = await test.Client.PostAsync(PagePath + "?handler=Cancel", new FormUrlEncodedContent(
            new Dictionary<string, string> { ["id"] = reservation.Id.ToString(), ["__RequestVerificationToken"] = token }));

        AssertPrg(response);
        var state = await test.StateAsync(reservation.Id);
        Assert.Equal(ReservationStatus.Expired, state.Status);
        Assert.Equal(ListingStatus.Available, state.ListingStatus);
        Assert.Equal(reservation.ExpiresAtUtc, state.ExpiresAtUtc);
        Assert.Null(state.FulfilledAtUtc);
        Assert.Equal(1, await test.AuditCountAsync());
    }

    [Theory]
    [InlineData("not-an-id")]
    [InlineData("2147483648")]
    public async Task Cancel_MalformedIdDoesNotUseAQueryIdOrChangeReservations(string malformedId)
    {
        await using var test = await AccountTest.CreateAsync();
        var reservation = await test.SeedAsync("Reservation with malformed submitted ID", Now.AddMinutes(20));
        var token = Token(await test.Client.GetStringAsync(PagePath));

        using var response = await test.Client.PostAsync(PagePath + "?handler=Cancel&id=" + reservation.Id,
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["id"] = malformedId, ["__RequestVerificationToken"] = token
            }));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(ReservationStatus.Active, (await test.StateAsync(reservation.Id)).Status);
        Assert.Equal(ListingStatus.Held, (await test.StateAsync(reservation.Id)).ListingStatus);
        Assert.Equal(0, await test.AuditCountAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Cancel_RechecksCurrentEligibilityDespiteAnExistingStudentCookie(bool removeMembership)
    {
        await using var test = await AccountTest.CreateAsync();
        var reservation = await test.SeedAsync("Reservation with revoked eligibility", Now.AddMinutes(20));
        var token = Token(await test.Client.GetStringAsync(PagePath));
        await test.RevokeEligibilityAsync(removeMembership);

        using var response = await test.Client.PostAsync(PagePath + "?handler=Cancel", new FormUrlEncodedContent(
            new Dictionary<string, string> { ["id"] = reservation.Id.ToString(), ["__RequestVerificationToken"] = token }));

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.NotNull(response.Headers.Location);
        Assert.Equal("/Identity/Account/AccessDenied", new Uri(test.Client.BaseAddress!, response.Headers.Location).AbsolutePath);
        Assert.Equal(ReservationStatus.Active, (await test.StateAsync(reservation.Id)).Status);
        Assert.Equal(ListingStatus.Held, (await test.StateAsync(reservation.Id)).ListingStatus);
        Assert.Equal(0, await test.AuditCountAsync());
    }

    private static void AssertPrg(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal(PagePath, response.Headers.Location?.OriginalString);
    }

    private static string MainContent(string html)
        => Regex.Match(html, @"<main\b[^>]*>([\s\S]*?)</main>", RegexOptions.IgnoreCase).Groups[1].Value;

    private static string Card(string html, int id)
        => Assert.Single(Regex.Matches(html, @"<article\b[^>]*>[\s\S]*?</article>", RegexOptions.IgnoreCase)
            .Cast<Match>(), match => match.Value.Contains($"aria-labelledby=\"reservation-title-{id}\"", StringComparison.Ordinal)).Value;

    private static string Token(string html)
    {
        var token = Regex.Match(html, "<input\\b[^>]*name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"",
            RegexOptions.IgnoreCase);
        Assert.True(token.Success);
        return WebUtility.HtmlDecode(token.Groups[1].Value);
    }

    private sealed class FixedClock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class AccountTest(
        AuthorizationWebApplicationFactory original, WebApplicationFactory<ApplicationDbContext> factory,
        HttpClient client, string userId) : IAsyncDisposable
    {
        public HttpClient Client { get; } = client;

        public static async Task<AccountTest> CreateAsync(string? role = ApplicationRoles.Student)
        {
            var original = new AuthorizationWebApplicationFactory();
            var factory = original.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
            {
                services.RemoveAll<TimeProvider>();
                services.AddSingleton<TimeProvider>(new FixedClock());
            }));
            var client = factory.CreateClient(new WebApplicationFactoryClientOptions
            {
                AllowAutoRedirect = false, BaseAddress = new Uri("https://localhost")
            });
            var userId = string.Empty;
            if (role is not null)
            {
                await using var scope = factory.Services.CreateAsyncScope();
                var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
                var email = IdentityTestContext.NewEmail();
                var password = IdentityTestContext.NewPassword();
                var user = new ApplicationUser { UserName = email, Email = email, DisplayName = "Reservation account test" };
                Assert.True((await users.CreateAsync(user, password)).Succeeded);
                Assert.True((await users.AddToRoleAsync(user, role)).Succeeded);
                userId = user.Id;
                var token = Token(await client.GetStringAsync("/Identity/Account/Login"));
                using var login = await client.PostAsync("/Identity/Account/Login", new FormUrlEncodedContent(
                    new Dictionary<string, string>
                    {
                        ["Input.Email"] = email, ["Input.Password"] = password, ["__RequestVerificationToken"] = token
                    }));
                Assert.Equal(HttpStatusCode.Redirect, login.StatusCode);
            }
            return new(original, factory, client, userId);
        }

        public async Task<string> AddOtherUserAsync()
        {
            await using var scope = factory.Services.CreateAsyncScope();
            var database = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var other = new ApplicationUser { DisplayName = "Other reservation owner" };
            database.Users.Add(other);
            await database.SaveChangesAsync();
            return other.Id;
        }

        public async Task<Reservation> SeedAsync(string title, DateTimeOffset deadline, string? owner = null)
        {
            await using var scope = factory.Services.CreateAsyncScope();
            var database = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var reservation = new Reservation
            {
                UserId = owner ?? userId, CreatedAtUtc = deadline.AddHours(-1).UtcDateTime,
                ExpiresAtUtc = deadline.UtcDateTime, Status = ReservationStatus.Active,
                Listing = new Listing
                {
                    Title = title, Description = "Account page test item.", Condition = "Good", CategoryId = 1,
                    Status = ListingStatus.Held, CreatedAtUtc = Now.UtcDateTime
                }
            };
            database.Reservations.Add(reservation);
            await database.SaveChangesAsync();
            return reservation;
        }

        public async Task<(ReservationStatus Status, ListingStatus ListingStatus, DateTime ExpiresAtUtc,
            DateTime? FulfilledAtUtc, string? CancellationReason)> StateAsync(int id)
        {
            await using var scope = factory.Services.CreateAsyncScope();
            var database = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var reservation = await database.Reservations.AsNoTracking().Include(row => row.Listing).SingleAsync(row => row.Id == id);
            return (reservation.Status, reservation.Listing.Status, reservation.ExpiresAtUtc,
                reservation.FulfilledAtUtc, reservation.CancellationReason);
        }

        public async Task<int> AuditCountAsync()
        {
            await using var scope = factory.Services.CreateAsyncScope();
            return await scope.ServiceProvider.GetRequiredService<ApplicationDbContext>().AuditEvents.CountAsync();
        }

        public async Task RevokeEligibilityAsync(bool removeMembership)
        {
            await using var scope = factory.Services.CreateAsyncScope();
            var database = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            if (removeMembership)
            {
                await database.UserRoles.Where(row => row.UserId == userId).ExecuteDeleteAsync();
            }
            else
            {
                await database.Users.Where(row => row.Id == userId)
                    .ExecuteUpdateAsync(setters => setters.SetProperty(row => row.IsActive, false));
            }
        }

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await factory.DisposeAsync();
            await original.DisposeAsync();
        }
    }
}

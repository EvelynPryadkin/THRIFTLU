using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text.Json;
using System.Text.RegularExpressions;
using LUThrift.Web.Authorization;
using LUThrift.Web.Configuration;
using LUThrift.Web.Data;
using LUThrift.Web.Models;
using LUThrift.Web.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using SkiaSharp;

namespace LUThrift.Tests.Integration;

public sealed class ListingImageHttpTests
{
    private const string EditPath = "/Staff/Inventory/Edit?id=1";
    private const string ImagePath = "/Thrift/Image/1";

    [Theory]
    [InlineData(ApplicationRoles.Staff)]
    [InlineData(ApplicationRoles.Administrator)]
    public async Task UploadReplaceRemove_UsesProtectedFormsAndUpdatesPublicViewsWithoutChangingInventory(string role)
    {
        await using var test = await PhotoHost.CreateAsync();
        var editor = await test.SignInAsync(role);
        await test.DatabaseAsync(async db =>
        {
            await db.Listings.Where(row => row.Id == 1).ExecuteUpdateAsync(setters =>
                setters.SetProperty(row => row.Title, "Coat <script>unsafe</script>"));
        });
        var before = await test.InventoryAsync();
        var edit = await editor.Client.GetStringAsync(EditPath);
        Assert.Matches("/images/listing-placeholder[^\"]*\\.svg", edit);
        var form = Form(edit, "UploadPhoto");
        Assert.Equal("multipart/form-data", Attribute(form, "enctype"));
        Assert.Equal("post", Attribute(form, "method"));
        Assert.Contains("name=\"Photo\"", form);
        Assert.Contains("accept=\"image/jpeg,image/png\"", form);
        using var body = Upload(form, Png());
        body.Add(new StringContent("forged-actor"), "UserId");
        body.Add(new StringContent("forged-actor"), "ActorUserId");
        body.Add(new StringContent("Archived"), "Status");
        body.Add(new StringContent("/etc/passwd"), "StorageFileName");
        body.Add(new StringContent("forged title"), "Input.Title");
        using var uploaded = await editor.Client.PostAsync(Attribute(form, "action"), body);
        AssertRedirect(uploaded);
        var first = await test.PhotoAsync();
        Assert.Equal("image/png", first.ContentType);
        Assert.Equal(editor.User.Id, Assert.Single(await test.AuditsAsync()).ActorUserId);
        Assert.Equal(before, await test.InventoryAsync());

        edit = await editor.Client.GetStringAsync(uploaded.Headers.Location!);
        Assert.Contains("Listing photo saved.", edit);
        Assert.Contains("src=\"/Thrift/Image/1\"", edit);
        Assert.Contains("alt=\"Photo of Coat &lt;script&gt;unsafe&lt;/script&gt;\"", edit);
        Assert.Equal(first.Id.ToString(), Fields(Form(edit, "RemovePhoto"))["ExpectedImageId"]);
        Assert.DoesNotContain("Listing photo saved.", await editor.Client.GetStringAsync(EditPath));
        foreach (var path in new[] { "/Thrift", "/Thrift/Details/1" })
        {
            var html = await test.Public.GetStringAsync(path);
            Assert.Contains("src=\"/Thrift/Image/1\"", html);
            Assert.Contains("Photo of Coat &lt;script&gt;unsafe&lt;/script&gt;", html);
            Assert.DoesNotContain(first.StorageFileName, html);
            Assert.DoesNotContain(test.Root, html);
        }
        using (var image = await test.Public.GetAsync(ImagePath))
        {
            AssertImageResponse(image, HttpStatusCode.OK);
            using var decoded = SKBitmap.Decode(await image.Content.ReadAsByteArrayAsync());
            Assert.Equal((7, 5), (decoded.Width, decoded.Height));
        }

        var replace = Form(edit, "UploadPhoto");
        using var replacementBody = Upload(replace, Png(11, 9));
        using var replaced = await editor.Client.PostAsync(Attribute(replace, "action"), replacementBody);
        AssertRedirect(replaced);
        var second = await test.PhotoAsync();
        Assert.NotEqual(first.Id, second.Id);
        Assert.False(File.Exists(Path.Combine(test.Root, first.StorageFileName)));
        var remove = Form(await editor.Client.GetStringAsync(EditPath), "RemovePhoto");
        using var removed = await editor.Client.PostAsync(Attribute(remove, "action"), new FormUrlEncodedContent(Fields(remove)));
        AssertRedirect(removed);
        Assert.Empty(Directory.GetFiles(test.Root));
        edit = await editor.Client.GetStringAsync(removed.Headers.Location!);
        Assert.Contains("Listing photo removed.", edit);
        Assert.Matches("/images/listing-placeholder[^\"]*\\.svg", edit);
        Assert.DoesNotContain("handler=RemovePhoto", edit);
        Assert.DoesNotContain("src=\"/Thrift/Image/1\"", await test.Public.GetStringAsync("/Thrift/Details/1"));
        Assert.Equal(before, await test.InventoryAsync());
        Assert.Equal(new[] { InventoryAuditActions.ImageAdded, InventoryAuditActions.ImageReplaced, InventoryAuditActions.ImageRemoved },
            (await test.AuditsAsync()).Select(row => row.Action));
    }

    [Theory]
    [InlineData(null, "Login")]
    [InlineData(ApplicationRoles.Student, "AccessDenied")]
    public async Task UnauthorizedPhotoPosts_DoNotReachStorage(string? role, string redirect)
    {
        await using var test = await PhotoHost.CreateAsync();
        var client = role is null ? test.Public : (await test.SignInAsync(role)).Client;
        var before = await test.SnapshotAsync();
        foreach (var handler in new[] { "UploadPhoto", "RemovePhoto" })
        {
            using var response = await client.PostAsync(EditPath + "&handler=" + handler, new FormUrlEncodedContent([]));
            Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
            Assert.Contains("/Identity/Account/" + redirect, response.Headers.Location!.OriginalString);
        }
        Assert.Equal(before, await test.SnapshotAsync());
        Assert.Empty(Directory.GetFiles(test.Root));
    }

    [Theory]
    [InlineData("UploadPhoto")]
    [InlineData("RemovePhoto")]
    public async Task MissingAntiforgeryToken_RejectsMutation(string handler)
    {
        await using var test = await PhotoHost.CreateAsync();
        var staff = await test.SignInAsync(ApplicationRoles.Staff);
        await test.SeedPhotoAsync(staff);
        var form = Form(await staff.Client.GetStringAsync(EditPath), handler);
        var before = await test.SnapshotAsync();
        using HttpContent content = handler == "UploadPhoto" ? Upload(form, Png(), token: false)
            : new FormUrlEncodedContent(Fields(form).Where(pair => pair.Key != "__RequestVerificationToken"));
        using var response = await staff.Client.PostAsync(Attribute(form, "action"), content);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(before, await test.SnapshotAsync());
    }

    [Theory]
    [InlineData("revoked", ApplicationRoles.Staff)]
    [InlineData("inactive", ApplicationRoles.Administrator)]
    public async Task StaleStaffCookie_CannotReadPrivatePhotoOrSubmitEitherAction(string change, string role)
    {
        await using var test = await PhotoHost.CreateAsync();
        var staff = await test.SignInAsync(role);
        await test.SeedPhotoAsync(staff);
        await test.StatusAsync(ListingStatus.Draft);
        var edit = await staff.Client.GetStringAsync(EditPath);
        await test.DatabaseAsync(async db =>
        {
            if (change == "inactive")
                await db.Users.Where(row => row.Id == staff.User.Id).ExecuteUpdateAsync(setters => setters.SetProperty(row => row.IsActive, false));
            else await db.UserRoles.Where(row => row.UserId == staff.User.Id).ExecuteDeleteAsync();
        });
        var before = await test.SnapshotAsync();
        using (var deniedGet = await staff.Client.GetAsync(EditPath))
            Assert.Contains("/Identity/Account/AccessDenied", deniedGet.Headers.Location!.OriginalString);
        foreach (var handler in new[] { "UploadPhoto", "RemovePhoto" })
        {
            var form = Form(edit, handler);
            using HttpContent body = handler == "UploadPhoto" ? Upload(form, Png()) : new FormUrlEncodedContent(Fields(form));
            using var denied = await staff.Client.PostAsync(Attribute(form, "action"), body);
            Assert.Equal(HttpStatusCode.Redirect, denied.StatusCode);
            Assert.Contains("/Identity/Account/AccessDenied", denied.Headers.Location!.OriginalString);
        }
        using var image = await staff.Client.GetAsync(ImagePath);
        AssertImageResponse(image, HttpStatusCode.NotFound);
        Assert.Equal(before, await test.SnapshotAsync());
    }

    [Theory]
    [InlineData(ListingStatus.Available, true, true)]
    [InlineData(ListingStatus.Held, true, true)]
    [InlineData(ListingStatus.Draft, false, true)]
    [InlineData(ListingStatus.Missing, false, true)]
    [InlineData(ListingStatus.Collected, false, false)]
    [InlineData(ListingStatus.Archived, false, false)]
    public async Task DirectImageRequests_EnforceVisibilityAndRemainReadOnly(ListingStatus status, bool publicVisible, bool staffVisible)
    {
        await using var test = await PhotoHost.CreateAsync();
        var staff = await test.SignInAsync(ApplicationRoles.Staff);
        var admin = await test.SignInAsync(ApplicationRoles.Administrator);
        var student = await test.SignInAsync(ApplicationRoles.Student);
        var photo = await test.SeedPhotoAsync(staff);
        await test.StatusAsync(status);
        var before = await test.SnapshotAsync();
        foreach (var (client, allowed) in new[] { (test.Public, publicVisible), (student.Client, publicVisible),
                     (staff.Client, staffVisible), (admin.Client, staffVisible) })
        {
            using var response = await client.GetAsync(ImagePath + "?StorageFileName=/etc/passwd");
            AssertImageResponse(response, allowed ? HttpStatusCode.OK : HttpStatusCode.NotFound);
            if (allowed)
            {
                using var decoded = SKBitmap.Decode(await response.Content.ReadAsByteArrayAsync());
                Assert.NotNull(decoded);
            }
            Assert.DoesNotContain(photo.StorageFileName, response.Headers.ToString());
            Assert.DoesNotContain(test.Root, response.Headers.ToString());
        }
        Assert.Equal(before, await test.SnapshotAsync());
    }

    [Fact]
    public async Task SameImageUrl_RechecksVisibilityAndDoesNotHonorStaleCacheValidators()
    {
        await using var test = await PhotoHost.CreateAsync();
        var staff = await test.SignInAsync(ApplicationRoles.Staff);
        await test.SeedPhotoAsync(staff);
        using (var available = await test.Public.GetAsync(ImagePath)) AssertImageResponse(available, HttpStatusCode.OK);
        foreach (var status in new[] { ListingStatus.Draft, ListingStatus.Missing, ListingStatus.Collected, ListingStatus.Archived })
        {
            await test.StatusAsync(status);
            using var request = new HttpRequestMessage(HttpMethod.Get, ImagePath);
            request.Headers.TryAddWithoutValidation("If-None-Match", "*");
            using var hidden = await test.Public.SendAsync(request);
            AssertImageResponse(hidden, HttpStatusCode.NotFound);
        }
        await test.StatusAsync(ListingStatus.Available);
        using var visible = await test.Public.GetAsync(ImagePath);
        AssertImageResponse(visible, HttpStatusCode.OK);
    }

    [Fact]
    public async Task InvalidUploadAndStaleForms_KeepTheSavedPhotoAndExplainFailure()
    {
        await using var test = await PhotoHost.CreateAsync();
        var staff = await test.SignInAsync(ApplicationRoles.Staff);
        var original = await test.SeedPhotoAsync(staff);
        var edit = await staff.Client.GetStringAsync(EditPath);
        var form = Form(edit, "UploadPhoto");
        var before = await test.SnapshotAsync();
        foreach (var (bytes, message) in new[]
        {
            ("<html>not a photo</html>"u8.ToArray(), "Choose a complete, valid JPEG or PNG photo."),
            (new byte[ListingImageProcessor.MaximumUploadBytes + 1], "Choose a photo no larger than 5 MiB.")
        })
        {
            using var body = Upload(form, bytes);
            using var response = await staff.Client.PostAsync(Attribute(form, "action"), body);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var html = await response.Content.ReadAsStringAsync();
            Assert.Contains(message, WebUtility.HtmlDecode(html));
            Assert.Contains("src=\"/Thrift/Image/1\"", html);
            Assert.Matches("<option\\b[^>]*value=\"1\"", html);
            Assert.Equal(before, await test.SnapshotAsync());
        }
        using (var body = Upload(form, Png(9, 8)))
        using (var success = await staff.Client.PostAsync(Attribute(form, "action"), body)) AssertRedirect(success);
        var replacement = await test.PhotoAsync();
        Assert.NotEqual(original.Id, replacement.Id);
        var committed = await test.SnapshotAsync();
        foreach (var handler in new[] { "UploadPhoto", "RemovePhoto" })
        {
            var stale = Form(edit, handler);
            using HttpContent body = handler == "UploadPhoto" ? Upload(stale, Png()) : new FormUrlEncodedContent(Fields(stale));
            using var response = await staff.Client.PostAsync(Attribute(stale, "action"), body);
            Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
            var html = await response.Content.ReadAsStringAsync();
            Assert.Contains("The listing photo changed. Refresh before trying again.", html);
            Assert.Equal(replacement.Id.ToString(), Fields(Form(html, "RemovePhoto"))["ExpectedImageId"]);
            Assert.Equal(committed, await test.SnapshotAsync());
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UploadBeyondMultipartLimit_ShowsSizeGuidanceAndPreservesSavedPhoto(bool unknownContentLength)
    {
        await using var test = await PhotoHost.CreateAsync();
        var staff = await test.SignInAsync(ApplicationRoles.Staff);
        var photo = await test.SeedPhotoAsync(staff);
        var form = Form(await staff.Client.GetStringAsync(EditPath), "UploadPhoto");
        var before = await test.SnapshotAsync();
        using var multipart = Upload(form, new byte[7 * 1024 * 1024]);
        using HttpContent body = unknownContentLength ? new UnknownLengthContent(multipart) : multipart;
        if (unknownContentLength) Assert.Null(body.Headers.ContentLength);

        using var response = await staff.Client.PostAsync(Attribute(form, "action"), body);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore);
        var html = WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync());
        Assert.Contains("Photo upload could not be completed", html);
        Assert.Contains("JPEG or PNG photo no larger than 5 MiB", html);
        Assert.Contains("refresh the page", html);
        Assert.Contains($"href=\"{EditPath}\"", html);
        Assert.DoesNotContain(test.Root, html);
        Assert.DoesNotContain(photo.StorageFileName, html);
        Assert.DoesNotContain("not-a-real-photo.html", html);
        Assert.Equal(before, await test.SnapshotAsync());
    }

    [Theory]
    [InlineData("/Staff/Inventory/Edit?id=1")]
    [InlineData("/Staff/Inventory/Edit?id=1&handler=RemovePhoto")]
    [InlineData("/Staff/Inventory/Edit?id=1&handler=RemovePhoto&handler=UploadPhoto")]
    public async Task NonUploadBadRequest_KeepsGenericErrorResponse(string path)
    {
        await using var test = await PhotoHost.CreateAsync();
        var staff = await test.SignInAsync(ApplicationRoles.Staff);
        await test.SeedPhotoAsync(staff);
        var before = await test.SnapshotAsync();
        using var body = new FormUrlEncodedContent([]); // Missing antiforgery token.
        using var response = await staff.Client.PostAsync(path, body);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var html = await response.Content.ReadAsStringAsync();
        Assert.Contains("Request could not be completed", html);
        Assert.DoesNotContain("Photo upload could not be completed", html);
        Assert.Equal(before, await test.SnapshotAsync());
    }

    [Fact]
    public async Task DirectStatusRequest_CannotSelectUploadGuidanceThroughQueryParameters()
    {
        await using var test = await PhotoHost.CreateAsync();
        using var response = await test.Public.GetAsync("/Status/400?handler=UploadPhoto&id=1");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var html = await response.Content.ReadAsStringAsync();
        Assert.Contains("Request could not be completed", html);
        Assert.DoesNotContain("Photo upload could not be completed", html);
    }

    [Theory]
    [InlineData(ListingStatus.Held)]
    [InlineData(ListingStatus.Collected)]
    [InlineData(ListingStatus.Archived)]
    public async Task PhotoForms_CannotChangeNewlyHeldOrTerminalListings(ListingStatus status)
    {
        await using var test = await PhotoHost.CreateAsync();
        var staff = await test.SignInAsync(ApplicationRoles.Staff);
        await test.SeedPhotoAsync(staff);
        var html = await staff.Client.GetStringAsync(EditPath);
        await test.StatusAsync(status);
        var before = await test.SnapshotAsync();
        foreach (var handler in new[] { "UploadPhoto", "RemovePhoto" })
        {
            var form = Form(html, handler);
            using HttpContent body = handler == "UploadPhoto" ? Upload(form, Png()) : new FormUrlEncodedContent(Fields(form));
            using var response = await staff.Client.PostAsync(Attribute(form, "action"), body);
            Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
            Assert.Contains("This listing cannot be edited", await response.Content.ReadAsStringAsync());
            Assert.Equal(before, await test.SnapshotAsync());
        }
    }

    [Fact]
    public async Task UnknownOrMissingFile_ReturnsSafe404AndNeverExposesTheStoragePath()
    {
        await using var test = await PhotoHost.CreateAsync();
        var staff = await test.SignInAsync(ApplicationRoles.Staff);
        var photo = await test.SeedPhotoAsync(staff);
        File.Delete(Path.Combine(test.Root, photo.StorageFileName));
        foreach (var path in new[] { ImagePath, "/Thrift/Image/2", "/Thrift/Image/2147483647" })
        {
            using var response = await test.Public.GetAsync(path);
            AssertImageResponse(response, HttpStatusCode.NotFound);
            var body = await response.Content.ReadAsStringAsync();
            Assert.DoesNotContain(test.Root, body);
            Assert.DoesNotContain(photo.StorageFileName, body);
        }
    }

    private static void AssertImageResponse(HttpResponseMessage response, HttpStatusCode status)
    {
        Assert.Equal(status, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore);
        Assert.Equal("nosniff", Assert.Single(response.Headers.GetValues("X-Content-Type-Options")));
        Assert.Null(response.Headers.ETag);
        Assert.Null(response.Content.Headers.LastModified);
        Assert.Null(response.Content.Headers.ContentDisposition);
        if (status == HttpStatusCode.OK) Assert.Equal("image/png", response.Content.Headers.ContentType!.MediaType);
    }

    private static void AssertRedirect(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal(EditPath, response.Headers.Location!.OriginalString);
    }

    private static string Form(string html, string handler) => Assert.Single(
        Regex.Matches(html, @"<form\b[^>]*>[\s\S]*?</form>", RegexOptions.IgnoreCase).Cast<Match>(),
        match => Attribute(match.Value, "action").Contains("handler=" + handler, StringComparison.Ordinal)).Value;
    private static string Attribute(string html, string name)
        => WebUtility.HtmlDecode(Regex.Match(html, "\\b" + name + "=\"([^\"]*)\"", RegexOptions.IgnoreCase).Groups[1].Value);
    private static Dictionary<string, string> Fields(string form) => Regex.Matches(form, @"<input\b[^>]*>", RegexOptions.IgnoreCase)
        .Cast<Match>().Where(match => Attribute(match.Value, "type") == "hidden")
        .ToDictionary(match => Attribute(match.Value, "name"), match => Attribute(match.Value, "value"));
    private static MultipartFormDataContent Upload(string form, byte[] bytes, bool token = true)
    {
        var content = new MultipartFormDataContent();
        foreach (var pair in Fields(form).Where(pair => token || pair.Key != "__RequestVerificationToken"))
            content.Add(new StringContent(pair.Value), pair.Key);
        var file = new ByteArrayContent(bytes);
        file.Headers.ContentType = new MediaTypeHeaderValue("text/html"); // Actual decoded content is authoritative.
        content.Add(file, "Photo", "../../not-a-real-photo.html");
        return content;
    }
    private static byte[] Png(int width = 7, int height = 5)
    {
        using var bitmap = new SKBitmap(width, height);
        bitmap.Erase(SKColors.Coral);
        using var image = SKImage.FromBitmap(bitmap);
        using var encoded = image.Encode(SKEncodedImageFormat.Png, 100);
        return encoded.ToArray();
    }

    private sealed class UnknownLengthContent(HttpContent inner) : HttpContent
    {
        public UnknownLengthContent(MultipartFormDataContent inner) : this((HttpContent)inner)
        {
            Headers.ContentType = inner.Headers.ContentType;
        }

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context)
            => inner.CopyToAsync(stream);
    }

    private sealed record Session(HttpClient Client, ApplicationUser User, ClaimsPrincipal Principal);
    private sealed class PhotoHost(AuthorizationWebApplicationFactory original,
        WebApplicationFactory<ApplicationDbContext> factory, string root) : IAsyncDisposable
    {
        public string Root { get; } = root;
        private readonly List<HttpClient> clients = [];
        public HttpClient Public { get; private set; } = null!;

        public static Task<PhotoHost> CreateAsync()
        {
            var root = Path.Combine(Path.GetTempPath(), "luthrift-photo-http-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            var original = new AuthorizationWebApplicationFactory();
            var factory = original.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
                services.PostConfigure<ImageStorageOptions>(options => options.RootPath = root)));
            var host = new PhotoHost(original, factory, root);
            host.Public = host.NewClient();
            return Task.FromResult(host);
        }
        private HttpClient NewClient()
        {
            var client = factory.CreateClient(new WebApplicationFactoryClientOptions
            {
                AllowAutoRedirect = false, BaseAddress = new Uri("https://localhost")
            });
            clients.Add(client);
            return client;
        }
        public async Task<Session> SignInAsync(string role)
        {
            await using var scope = factory.Services.CreateAsyncScope();
            var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            var email = IdentityTestContext.NewEmail();
            var user = new ApplicationUser { DisplayName = "Synthetic photo editor", Email = email, UserName = email };
            Assert.True((await users.CreateAsync(user)).Succeeded);
            Assert.True((await users.AddToRoleAsync(user, role)).Succeeded);
            var principal = await scope.ServiceProvider.GetRequiredService<SignInManager<ApplicationUser>>().CreateUserPrincipalAsync(user);
            var cookie = scope.ServiceProvider.GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>().Get(IdentityConstants.ApplicationScheme);
            var now = cookie.TimeProvider!.GetUtcNow();
            var ticket = new AuthenticationTicket(principal, new AuthenticationProperties
            {
                IssuedUtc = now, ExpiresUtc = now.AddHours(1)
            }, IdentityConstants.ApplicationScheme);
            var client = NewClient();
            client.DefaultRequestHeaders.Add("Cookie", $"{cookie.Cookie.Name}={cookie.TicketDataFormat.Protect(ticket)}");
            return new(client, user, principal);
        }
        public async Task DatabaseAsync(Func<ApplicationDbContext, Task> action)
        {
            await using var scope = factory.Services.CreateAsyncScope();
            await action(scope.ServiceProvider.GetRequiredService<ApplicationDbContext>());
        }
        private async Task<T> ReadAsync<T>(Func<ApplicationDbContext, Task<T>> action)
        {
            await using var scope = factory.Services.CreateAsyncScope();
            return await action(scope.ServiceProvider.GetRequiredService<ApplicationDbContext>());
        }
        public Task StatusAsync(ListingStatus status) => DatabaseAsync(async db =>
            await db.Listings.Where(row => row.Id == 1).ExecuteUpdateAsync(setters => setters.SetProperty(row => row.Status, status)));
        public async Task<ListingImage> SeedPhotoAsync(Session staff)
        {
            await using var scope = factory.Services.CreateAsyncScope();
            using var bytes = new MemoryStream(Png());
            var result = await scope.ServiceProvider.GetRequiredService<ImageService>().AddOrReplaceAsync(1, bytes, staff.Principal);
            Assert.True(result.Succeeded);
            return await PhotoAsync();
        }
        public Task<ListingImage> PhotoAsync() => ReadAsync(db => db.ListingImages.AsNoTracking().SingleAsync());
        public Task<AuditEvent[]> AuditsAsync() => ReadAsync(db => db.AuditEvents.AsNoTracking().OrderBy(row => row.Id).ToArrayAsync());
        public Task<string> InventoryAsync() => ReadAsync(async db => JsonSerializer.Serialize(new
        {
            Listings = await db.Listings.AsNoTracking().OrderBy(row => row.Id).Select(row => new
            { row.Id, row.Title, row.Description, row.Condition, row.Size, row.CategoryId, row.Status, row.CreatedAtUtc }).ToArrayAsync(),
            Reservations = await db.Reservations.AsNoTracking().OrderBy(row => row.Id).Select(row => new
            { row.Id, row.UserId, row.ListingId, row.Status, row.CreatedAtUtc, row.ExpiresAtUtc, row.FulfilledAtUtc, row.CancellationReason }).ToArrayAsync()
        }));
        public async Task<string> SnapshotAsync() => JsonSerializer.Serialize(new
        {
            Inventory = await InventoryAsync(),
            Photos = await ReadAsync(db => db.ListingImages.AsNoTracking().OrderBy(row => row.Id).Select(row => new
            { row.Id, row.ListingId, row.StorageFileName, row.ContentType, row.ByteLength, row.Width, row.Height, row.CreatedAtUtc }).ToArrayAsync()),
            Audits = (await AuditsAsync()).Select(row => new { row.Id, row.ListingId, row.ActorUserId, row.Action, row.OccurredAtUtc, row.Details }),
            Files = Directory.GetFiles(Root).OrderBy(path => path).Select(path => new { Name = Path.GetFileName(path), Bytes = Convert.ToBase64String(File.ReadAllBytes(path)) })
        });
        public async ValueTask DisposeAsync()
        {
            foreach (var client in clients) client.Dispose();
            await factory.DisposeAsync();
            await original.DisposeAsync();
            Directory.Delete(Root, recursive: true);
        }
    }
}

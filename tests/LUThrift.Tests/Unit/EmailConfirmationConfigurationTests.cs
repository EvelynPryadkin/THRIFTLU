using LUThrift.Web.Configuration;
using LUThrift.Web.Services;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace LUThrift.Tests.Unit;

public sealed class EmailConfirmationConfigurationTests
{
    [Theory]
    [InlineData("https://thrift.example.test", false, true)]
    [InlineData("http://thrift.example.test", true, false)]
    [InlineData("http://localhost:5242", true, true)]
    [InlineData("http://localhost:5242", false, false)]
    [InlineData("https://user:password@example.test", false, false)]
    [InlineData("https://example.test/path", false, false)]
    [InlineData("https://example.test/?next=evil", false, false)]
    [InlineData("https://example.test/#fragment", false, false)]
    [InlineData("//example.test", false, false)]
    [InlineData("", false, false)]
    public void CallbackOrigin_MustBeExplicitSafeConfiguration(string origin, bool development, bool valid)
        => Assert.Equal(valid, new EmailConfirmationOptions { PublicBaseUrl = origin }.TryGetBaseUri(development, out _));

    [Theory]
    [InlineData("synthetic@lawrence.edu", true)]
    [InlineData("SYNTHETIC@LAWRENCE.EDU", true)]
    [InlineData("synthetic+fixture@lawrence.edu", true)]
    [InlineData("synthetic@other.edu", false)]
    [InlineData("synthetic@sub.lawrence.edu", false)]
    [InlineData("synthetic@lawrence.edu.evil.invalid", false)]
    [InlineData("synthetic@lawrence.edu.", false)]
    [InlineData("Synthetic <synthetic@lawrence.edu>", false)]
    public void CampusPolicy_UsesExactSuppliedDomain(string email, bool allowed)
        => Assert.Equal(allowed, new CampusAccountPolicy().CanRegister(email));

    [Theory]
    [InlineData("Production")]
    [InlineData("Testing")]
    [InlineData("Staging")]
    public async Task LocalSender_CannotRunOutsideDevelopment(string environment)
    {
        var directory = Path.Combine(Path.GetTempPath(), "thrift-confirm-" + Guid.NewGuid().ToString("N"));
        var sender = new DevelopmentPickupEmailSender(new TestEnvironment(environment),
            Options.Create(new EmailConfirmationOptions { DevelopmentPickupDirectory = directory }));
        await Assert.ThrowsAsync<InvalidOperationException>(() => sender.SendEmailAsync("synthetic@example.invalid", "Synthetic", "safe fixture"));
        Assert.False(Directory.Exists(directory));
    }

    [Fact]
    public async Task LocalSender_WritesPrivateFilesOnlyOutsideRepository()
    {
        if (OperatingSystem.IsWindows()) return;
        var directory = Path.Combine(Path.GetTempPath(), "thrift-confirm-" + Guid.NewGuid().ToString("N"));
        // macOS temp roots can contain symlinked ancestors; use the physical path.
        if (directory.StartsWith("/var/", StringComparison.Ordinal)) directory = "/private" + directory;
        try
        {
            var sender = new DevelopmentPickupEmailSender(new TestEnvironment("Development"),
                Options.Create(new EmailConfirmationOptions { DevelopmentPickupDirectory = directory }));
            await sender.SendEmailAsync("synthetic@example.invalid", "Synthetic confirmation", "<p>Local fixture</p>");
            var file = Assert.Single(Directory.GetFiles(directory));
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(file));
            Assert.Contains("Local fixture", await File.ReadAllTextAsync(file));
            var blocked = new DevelopmentPickupEmailSender(new TestEnvironment("Development"),
                Options.Create(new EmailConfirmationOptions { DevelopmentPickupDirectory = AppContext.BaseDirectory }));
            await Assert.ThrowsAsync<InvalidOperationException>(() => blocked.SendEmailAsync("synthetic@example.invalid", "Synthetic", "body"));
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    private sealed class TestEnvironment(string environment) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = environment;
        public string ApplicationName { get; set; } = "Synthetic confirmation tests";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}

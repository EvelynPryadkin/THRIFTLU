using System.Text.Encodings.Web;
using LUThrift.Web.Configuration;
using Microsoft.AspNetCore.Identity.UI.Services;
using Microsoft.Extensions.Options;

namespace LUThrift.Web.Services;

public sealed class UnconfiguredEmailSender : IEmailSender
{
    public Task SendEmailAsync(string email, string subject, string htmlMessage)
        => Task.FromException(new InvalidOperationException("An email provider has not been configured."));
}

// An explicit local mailbox, never a web endpoint or log sink. Production cannot
// use this sender, even if its directory setting is accidentally carried over.
public sealed class DevelopmentPickupEmailSender(
    IHostEnvironment environment, IOptions<EmailConfirmationOptions> options) : IEmailSender
{
    public async Task SendEmailAsync(string email, string subject, string htmlMessage)
    {
        if (!environment.IsDevelopment()) throw new InvalidOperationException("Local email pickup is Development-only.");
        var configured = options.Value.DevelopmentPickupDirectory;
        if (string.IsNullOrWhiteSpace(configured) || !Path.IsPathFullyQualified(configured))
            throw new InvalidOperationException("A private local email directory is required.");
        var path = Path.GetFullPath(configured);
        var repository = FindRepositoryRoot(environment.ContentRootPath);
        if (IsInside(path, repository) || IsInside(path, Path.Combine(environment.ContentRootPath, "wwwroot")))
            throw new InvalidOperationException("Local email pickup must be outside the repository and web root.");
        for (var parent = new DirectoryInfo(path); parent is not null; parent = parent.Parent)
            if (parent.Exists && parent.Attributes.HasFlag(FileAttributes.ReparsePoint))
                throw new InvalidOperationException("Local email pickup cannot use symbolic links.");
        if (OperatingSystem.IsWindows())
            throw new InvalidOperationException("Local email pickup requires a private Unix directory; configure a provider on Windows.");
        Directory.CreateDirectory(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        if ((File.GetUnixFileMode(path) & (UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute
            | UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute)) != 0)
            throw new InvalidOperationException("Local email pickup directory permissions must be private.");
        var file = Path.Combine(path, Guid.NewGuid().ToString("N") + ".html");
        await using var stream = new FileStream(file, new FileStreamOptions
        {
            Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None,
            UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite
        });
        await using var writer = new StreamWriter(stream);
        await writer.WriteAsync("<!doctype html><meta charset=\"utf-8\"><title>THRIFTLU development email</title><p>To: "
            + HtmlEncoder.Default.Encode(email) + "</p><h1>" + HtmlEncoder.Default.Encode(subject) + "</h1>" + htmlMessage);
    }

    private static bool IsInside(string path, string root)
        => string.Equals(path.TrimEnd(Path.DirectorySeparatorChar), root.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase)
            || path.StartsWith(root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    private static string FindRepositoryRoot(string contentRoot)
    {
        for (var directory = new DirectoryInfo(contentRoot); directory is not null; directory = directory.Parent)
            if (Directory.Exists(Path.Combine(directory.FullName, ".git")) || File.Exists(Path.Combine(directory.FullName, ".git")))
                return directory.FullName;
        return Path.GetFullPath(contentRoot);
    }
}

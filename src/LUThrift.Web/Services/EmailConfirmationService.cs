using System.Text;
using System.Text.Encodings.Web;
using LUThrift.Web.Configuration;
using LUThrift.Web.Models;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.UI.Services;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Options;

namespace LUThrift.Web.Services;

public sealed class EmailConfirmationService(
    UserManager<ApplicationUser> users, IEmailSender sender, IOptions<EmailConfirmationOptions> settings,
    IHostEnvironment environment, ILogger<EmailConfirmationService> logger)
{
    public async Task<bool> SendAsync(ApplicationUser user, string? localReturnUrl, CancellationToken cancellationToken = default)
    {
        if (!user.IsActive || user.EmailConfirmed || string.IsNullOrWhiteSpace(user.Email)) return false;
        if (!settings.Value.TryGetBaseUri(environment.IsDevelopment(), out var origin)) return false;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var token = await users.GenerateEmailConfirmationTokenAsync(user);
            var callback = QueryHelpers.AddQueryString(new Uri(origin!, "/Identity/Account/ConfirmEmail").AbsoluteUri,
                new Dictionary<string, string?>
                {
                    ["userId"] = user.Id, ["code"] = WebEncoders.Base64UrlEncode(Encoding.UTF8.GetBytes(token)),
                    ["returnUrl"] = SafeReturnUrl(localReturnUrl)
                });
            await sender.SendEmailAsync(user.Email, "Confirm your THRIFTLU email",
                "<p>Confirm your email address for THRIFTLU.</p><p><a href=\"" + HtmlEncoder.Default.Encode(callback)
                + "\">Confirm email</a></p><p>This link expires after 24 hours. If you did not request it, you can ignore this email.</p>");
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception)
        {
            // Provider exception text may contain recipients, credentials or tokens.
            logger.LogWarning(new EventId(2100, "ConfirmationDeliveryFailed"), "Confirmation email delivery failed. Use the resend form to retry.");
            return false;
        }
    }

    public async Task ResendAsync(string email, string? localReturnUrl, CancellationToken cancellationToken = default)
    {
        var user = await users.FindByEmailAsync(email.Trim());
        if (user is not null && user.IsActive && !user.EmailConfirmed)
            await SendAsync(user, localReturnUrl, cancellationToken);
    }

    public async Task<bool> ConfirmAsync(string? userId, string? code, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(userId) || userId.Length > 450 || string.IsNullOrWhiteSpace(code) || code.Length > 4096)
            return false;
        string decoded;
        try { decoded = new UTF8Encoding(false, true).GetString(WebEncoders.Base64UrlDecode(code)); }
        catch (Exception exception) when (exception is FormatException or DecoderFallbackException) { return false; }
        cancellationToken.ThrowIfCancellationRequested();
        var user = await users.FindByIdAsync(userId);
        if (user is null || !user.IsActive) return false;
        if (user.EmailConfirmed)
            return await users.VerifyUserTokenAsync(user, users.Options.Tokens.EmailConfirmationTokenProvider,
                UserManager<ApplicationUser>.ConfirmEmailTokenPurpose, decoded);
        return (await users.ConfirmEmailAsync(user, decoded)).Succeeded;
    }

    public static string SafeReturnUrl(string? value)
        => value is { Length: > 0 and <= 2048 } && value[0] == '/'
            && (value.Length == 1 || value[1] != '/') && !value.Contains('\\') && !value.Any(char.IsControl)
                ? value : "/";
}

namespace LUThrift.Web.Configuration;

public sealed class EmailConfirmationOptions
{
    public const string SectionName = "EmailConfirmation";
    public string? PublicBaseUrl { get; set; }
    public string? DevelopmentPickupDirectory { get; set; }

    public bool TryGetBaseUri(bool development, out Uri? uri)
    {
        uri = null;
        if (string.IsNullOrWhiteSpace(PublicBaseUrl) || PublicBaseUrl.Length > 2048
            || !Uri.TryCreate(PublicBaseUrl, UriKind.Absolute, out var candidate)
            || !string.IsNullOrEmpty(candidate.UserInfo) || candidate.AbsolutePath != "/"
            || !string.IsNullOrEmpty(candidate.Query) || !string.IsNullOrEmpty(candidate.Fragment)
            || !(candidate.Scheme == "https" || development && candidate.Scheme == "http" && candidate.IsLoopback))
            return false;
        uri = candidate;
        return true;
    }
}

namespace LUThrift.Web.Configuration;

public sealed class ImageStorageOptions
{
    public const string SectionName = "ImageStorage";

    // An absolute, operator-owned directory outside both the checkout and wwwroot.
    // Missing/invalid configuration disables image writes, not application startup.
    public string? RootPath { get; set; }
}

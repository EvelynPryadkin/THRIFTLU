using System.Net;
using System.Reflection;
using System.Security.Claims;
using System.Text.RegularExpressions;
using LUThrift.Web.Authorization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.ApplicationModels;
using Microsoft.AspNetCore.Mvc.Authorization;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace LUThrift.Tests.Integration;

public class AuthorizationTests(AuthorizationWebApplicationFactory factory)
    : IClassFixture<AuthorizationWebApplicationFactory>
{
    [Theory]
    [InlineData(null, "/Staff", "/Identity/Account/Login")]
    [InlineData(null, "/Admin", "/Identity/Account/Login")]
    [InlineData(ApplicationRoles.Student, "/Staff", "/Identity/Account/AccessDenied")]
    [InlineData(ApplicationRoles.Student, "/Admin", "/Identity/Account/AccessDenied")]
    [InlineData(ApplicationRoles.Staff, "/Staff", null)]
    [InlineData(ApplicationRoles.Staff, "/Admin", "/Identity/Account/AccessDenied")]
    [InlineData(ApplicationRoles.Administrator, "/Staff", null)]
    [InlineData(ApplicationRoles.Administrator, "/Admin", null)]
    public async Task ProtectedPages_EnforceRoleMatrix(string? role, string path, string? redirectPath)
    {
        using var client = await factory.CreateIdentityClientAsync(role);

        using var response = await client.GetAsync(path);

        if (redirectPath is null)
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }
        else
        {
            Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
            Assert.NotNull(response.Headers.Location);
            var location = new Uri(client.BaseAddress!, response.Headers.Location);
            Assert.Equal(redirectPath, location.AbsolutePath);
            Assert.Equal(path, QueryHelpers.ParseQuery(location.Query)["ReturnUrl"].ToString());
        }
    }

    [Theory]
    [InlineData(null, false, false)]
    [InlineData(ApplicationRoles.Student, false, false)]
    [InlineData(ApplicationRoles.Staff, true, false)]
    [InlineData(ApplicationRoles.Administrator, true, true)]
    public async Task PublicHome_ShowsOnlyPermittedStaffAndAdminLinks(
        string? role, bool expectedStaffLink, bool expectedAdminLink)
    {
        using var client = await factory.CreateIdentityClientAsync(role);

        using var response = await client.GetAsync("/");
        var html = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(expectedStaffLink, HasLink(html, "/Staff"));
        Assert.Equal(expectedAdminLink, HasLink(html, "/Admin"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData(ApplicationRoles.Student)]
    public async Task AccessDenied_IsTerminalAndDoesNotRequirePrivilegedRole(string? role)
    {
        using var client = await factory.CreateIdentityClientAsync(role);

        using var response = await client.GetAsync("/Identity/Account/AccessDenied");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Null(response.Headers.Location);
        Assert.Contains("Access denied", await response.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("/Staff/Inventory/Edit", false, true, true, false)]
    [InlineData("/Staff/Future/NestedPage", false, true, true, false)]
    [InlineData("/Admin/Settings/Index", false, false, true, false)]
    [InlineData("/Admin/Future/NestedPage", false, false, true, false)]
    [InlineData("/Account/Profile", true, true, true, true)]
    [InlineData("/Account/Future/NestedPage", true, true, true, true)]
    public async Task FolderConventions_ProtectNestedAndFuturePages(
        string pagePath, bool studentAllowed, bool staffAllowed, bool adminAllowed, bool rolelessAllowed)
    {
        var page = ApplyPageConventions(pagePath);
        var filters = page.Filters.OfType<AuthorizeFilter>().ToArray();
        var authorizationData = page.EndpointMetadata.OfType<IAuthorizeData>()
            .Concat(filters.SelectMany(filter => filter.AuthorizeData ?? []));
        var policies = filters.Where(filter => filter.Policy is not null).Select(filter => filter.Policy!);
        var policyProvider = factory.Services.GetRequiredService<IAuthorizationPolicyProvider>();
        var policy = await AuthorizationPolicy.CombineAsync(policyProvider, authorizationData, policies);
        Assert.NotNull(policy);
        Assert.False(AllowsAnonymous(page));

        var authorization = factory.Services.GetRequiredService<IAuthorizationService>();
        Assert.False((await authorization.AuthorizeAsync(new ClaimsPrincipal(new ClaimsIdentity()), null, policy)).Succeeded);

        (string? Role, bool Allowed)[] cases =
        [
            (ApplicationRoles.Student, studentAllowed),
            (ApplicationRoles.Staff, staffAllowed),
            (ApplicationRoles.Administrator, adminAllowed),
            (null, rolelessAllowed)
        ];
        foreach (var (role, expected) in cases)
        {
            var identity = new ClaimsIdentity("Test");
            if (role is not null)
            {
                identity.AddClaim(new Claim(ClaimTypes.Role, role));
            }

            var result = await authorization.AuthorizeAsync(new ClaimsPrincipal(identity), null, policy);
            Assert.Equal(expected, result.Succeeded);
        }
    }

    [Theory]
    [InlineData("/Thrift/Index")]
    [InlineData("/Thrift/Future/Details")]
    public void ThriftFolderConventions_KeepBrowsingAnonymous(string pagePath)
    {
        var page = ApplyPageConventions(pagePath);

        Assert.True(AllowsAnonymous(page));
    }

    private PageApplicationModel ApplyPageConventions(string pagePath)
    {
        var descriptor = new PageActionDescriptor
        {
            ViewEnginePath = pagePath,
            RelativePath = $"/Pages{pagePath}.cshtml"
        };
        var page = new PageApplicationModel(descriptor, typeof(PageModel).GetTypeInfo(), []);
        var options = factory.Services.GetRequiredService<IOptions<RazorPagesOptions>>().Value;
        foreach (var convention in options.Conventions.OfType<IPageApplicationModelConvention>())
        {
            convention.Apply(page);
        }

        return page;
    }

    private static bool AllowsAnonymous(PageApplicationModel page)
        => page.EndpointMetadata.OfType<IAllowAnonymous>().Any()
            || page.Filters.OfType<IAllowAnonymousFilter>().Any();

    private static bool HasLink(string html, string path)
        => Regex.IsMatch(html, $"<a\\b[^>]*href=\"{Regex.Escape(path)}(?:/Index)?\"[^>]*>", RegexOptions.IgnoreCase);
}

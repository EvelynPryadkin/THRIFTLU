using LUThrift.Web.Authorization;
using LUThrift.Web.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace LUThrift.Tests.Integration;

public class RoleInitializerTests
{
    [Fact]
    public async Task Initialize_CreatesAllRolesAndIsSafeToRepeat()
    {
        await using var context = await IdentityTestContext.CreateAsync();
        var initializer = context.CreateInitializer();

        await initializer.InitializeAsync();
        await initializer.InitializeAsync();

        var roles = await context.Database.Roles.Select(role => role.Name).OrderBy(name => name).ToListAsync();
        Assert.Equal(new[]
        {
            ApplicationRoles.Administrator, ApplicationRoles.Staff, ApplicationRoles.Student
        }, roles);
        Assert.Empty(await context.Database.Users.ToListAsync());
        Assert.Empty(await context.Database.UserRoles.ToListAsync());
    }

    [Theory]
    [InlineData("Production")]
    [InlineData("Staging")]
    public async Task Initialize_OutsideDevelopmentNeverBootstrapsConfiguredUser(string environment)
    {
        await using var context = await IdentityTestContext.CreateAsync();
        var user = await context.CreateUserAsync();

        await context.CreateInitializer(environment, user.Email).InitializeAsync();

        Assert.Equal(3, await context.Database.Roles.CountAsync());
        Assert.Empty(await context.Users.GetRolesAsync(user));
        Assert.Equal(1, await context.Database.Users.CountAsync());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" \t ")]
    public async Task Initialize_WithoutBootstrapEmailCreatesNoUsers(string? email)
    {
        await using var context = await IdentityTestContext.CreateAsync();

        await context.CreateInitializer(email: email).InitializeAsync();

        Assert.Equal(3, await context.Database.Roles.CountAsync());
        Assert.Empty(await context.Database.Users.ToListAsync());
    }

    [Fact]
    public async Task Initialize_InDevelopmentAddsAdministratorToExistingUserOnlyOnce()
    {
        await using var context = await IdentityTestContext.CreateAsync();
        await context.CreateInitializer().InitializeAsync();
        var user = await context.CreateUserAsync();
        Assert.True((await context.Users.AddToRoleAsync(user, ApplicationRoles.Student)).Succeeded);
        var initializer = context.CreateInitializer(email: user.Email!.ToUpperInvariant());

        await initializer.InitializeAsync();
        await initializer.InitializeAsync();

        Assert.Equal(new[] { ApplicationRoles.Administrator, ApplicationRoles.Student },
            (await context.Users.GetRolesAsync(user)).OrderBy(role => role));
        Assert.Equal(1, await context.Database.Users.CountAsync());
        Assert.Equal(2, await context.Database.UserRoles.CountAsync());
    }

    [Fact]
    public async Task Initialize_WhenConfiguredUserDoesNotExistWarnsWithoutCreatingAccount()
    {
        await using var context = await IdentityTestContext.CreateAsync();
        var logger = new RecordingLogger<RoleInitializer>();
        var email = IdentityTestContext.NewEmail();

        await context.CreateInitializer(email: email, logger: logger).InitializeAsync();

        Assert.Empty(await context.Database.Users.ToListAsync());
        Assert.Empty(await context.Database.UserRoles.ToListAsync());
        Assert.Contains(logger.Entries, entry => entry.Level == LogLevel.Warning);
        Assert.All(logger.Entries, entry => Assert.DoesNotContain(email, entry.Message));
    }

    [Fact]
    public async Task Initialize_WhenRoleCreationFailsThrows()
    {
        var failureDetail = Guid.NewGuid().ToString("N");
        await using var context = await IdentityTestContext.CreateAsync(roleCreationFailure: failureDetail);

        await Assert.ThrowsAsync<InvalidOperationException>(() => context.CreateInitializer().InitializeAsync());

        Assert.Empty(await context.Database.Roles.ToListAsync());
    }

    [Fact]
    public async Task Initialize_WhenBootstrapAssignmentFailsThrowsWithoutLeakingDetails()
    {
        var failureDetail = Guid.NewGuid().ToString("N");
        await using var context = await IdentityTestContext.CreateAsync(roleAssignmentFailure: failureDetail);
        var user = await context.CreateUserAsync();

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => context.CreateInitializer(email: user.Email).InitializeAsync());

        Assert.DoesNotContain(failureDetail, exception.ToString());
        Assert.DoesNotContain(user.Email!, exception.ToString());
        Assert.Empty(await context.Users.GetRolesAsync(user));
        Assert.Equal(1, await context.Database.Users.CountAsync());
    }
}

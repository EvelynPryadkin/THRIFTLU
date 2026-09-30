using System.ComponentModel.DataAnnotations;
using LUThrift.Web.Areas.Identity.Pages.Account;
using LUThrift.Web.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace LUThrift.Tests.Integration;

public class StudentRegistrationTests
{
    [Fact]
    public async Task Register_CreatesStudentOnlyBeforeSigningIn()
    {
        await using var context = await IdentityTestContext.CreateAsync();
        await context.CreateInitializer().InitializeAsync();
        var input = NewInput();
        input.DisplayName = "  Test student  ";
        var model = context.CreateRegistration(input);

        var result = await model.OnPostAsync("/Thrift");

        Assert.Equal("/Thrift", Assert.IsType<LocalRedirectResult>(result).Url);
        var user = await context.Database.Users.AsNoTracking().SingleAsync();
        Assert.Equal("Test student", user.DisplayName);
        Assert.Equal(input.Email, user.Email);
        Assert.Equal(input.Email, user.UserName);
        Assert.Equal(input.Email.ToUpperInvariant(), user.NormalizedEmail);
        Assert.True(user.IsActive);
        Assert.True(await context.Users.CheckPasswordAsync(user, input.Password));
        Assert.Equal(new[] { ApplicationRoles.Student }, await context.Users.GetRolesAsync(user));
        var signIn = Assert.Single(context.SignIns.Calls);
        Assert.Equal(user.Id, signIn.User.Id);
        Assert.False(signIn.IsPersistent);
        Assert.Equal(new[] { ApplicationRoles.Student }, signIn.Roles);
        Assert.False(signIn.HasActiveTransaction);
    }

    [Fact]
    public async Task Register_WhenStudentAssignmentFailsRollsBackAccountAndDoesNotSignIn()
    {
        var failureDetail = Guid.NewGuid().ToString("N");
        await using var context = await IdentityTestContext.CreateAsync(roleAssignmentFailure: failureDetail);
        await context.CreateInitializer().InitializeAsync();
        var model = context.CreateRegistration(NewInput());

        var result = await model.OnPostAsync();

        Assert.IsType<PageResult>(result);
        Assert.False(model.ModelState.IsValid);
        Assert.All(model.ModelState.Values.SelectMany(value => value.Errors),
            error => Assert.DoesNotContain(failureDetail, error.ErrorMessage));
        context.Database.ChangeTracker.Clear();
        Assert.Empty(await context.Database.Users.ToListAsync());
        Assert.Empty(await context.Database.UserRoles.ToListAsync());
        Assert.Empty(context.SignIns.Calls);
    }

    [Fact]
    public async Task Register_WhenStudentRoleIsMissingRollsBackAccountAndDoesNotSignIn()
    {
        await using var context = await IdentityTestContext.CreateAsync();
        var model = context.CreateRegistration(NewInput());

        var result = await model.OnPostAsync();

        Assert.IsType<PageResult>(result);
        Assert.False(model.ModelState.IsValid);
        context.Database.ChangeTracker.Clear();
        Assert.Empty(await context.Database.Users.ToListAsync());
        Assert.Empty(await context.Database.UserRoles.ToListAsync());
        Assert.Empty(context.SignIns.Calls);
    }

    [Fact]
    public async Task Register_WhenEmailIsAlreadyUsedPreservesExistingUserAndDoesNotSignIn()
    {
        await using var context = await IdentityTestContext.CreateAsync();
        await context.CreateInitializer().InitializeAsync();
        var existingUser = await context.CreateUserAsync();
        var input = NewInput();
        input.Email = existingUser.Email!.ToUpperInvariant();
        var model = context.CreateRegistration(input);

        var result = await model.OnPostAsync();

        Assert.IsType<PageResult>(result);
        Assert.False(model.ModelState.IsValid);
        Assert.Equal(existingUser.Id, (await context.Database.Users.AsNoTracking().SingleAsync()).Id);
        Assert.Empty(await context.Users.GetRolesAsync(existingUser));
        Assert.Empty(context.SignIns.Calls);
    }

    [Theory]
    [InlineData("BlankDisplayName")]
    [InlineData("LongDisplayName")]
    [InlineData("InvalidEmail")]
    [InlineData("ShortPassword")]
    [InlineData("MismatchedConfirmation")]
    public async Task Register_InvalidInputDoesNotCreateAccountOrSignIn(string invalidField)
    {
        await using var context = await IdentityTestContext.CreateAsync();
        var input = NewInput();
        switch (invalidField)
        {
            case "BlankDisplayName": input.DisplayName = "   "; break;
            case "LongDisplayName": input.DisplayName = new string('a', 101); break;
            case "InvalidEmail": input.Email = Guid.NewGuid().ToString("N"); break;
            case "ShortPassword": input.Password = input.Password[..7]; input.ConfirmPassword = input.Password; break;
            case "MismatchedConfirmation": input.ConfirmPassword = IdentityTestContext.NewPassword(); break;
        }

        var model = context.CreateRegistration(input);
        var validationErrors = new List<ValidationResult>();
        Assert.False(Validator.TryValidateObject(input, new ValidationContext(input), validationErrors, true));
        foreach (var error in validationErrors)
        {
            model.ModelState.AddModelError("Input", error.ErrorMessage!);
        }

        Assert.IsType<PageResult>(await model.OnPostAsync());

        Assert.Empty(await context.Database.Users.ToListAsync());
        Assert.Empty(context.SignIns.Calls);
    }

    private static RegisterModel.InputModel NewInput()
    {
        var password = IdentityTestContext.NewPassword();
        return new RegisterModel.InputModel
        {
            DisplayName = "Test student",
            Email = IdentityTestContext.NewEmail(),
            Password = password,
            ConfirmPassword = password
        };
    }
}

using LUThrift.Web.Data;
using LUThrift.Web.Models;
using Microsoft.EntityFrameworkCore;

namespace LUThrift.Tests;

public class DatabaseModelTests
{
    [Fact]
    public void ReservationRelationships_AreRequiredAndPreventCascadeDeletion()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseNpgsql("Host=localhost;Database=luthrift_model_test")
            .Options;
        using var context = new ApplicationDbContext(options);

        var reservation = context.Model.FindEntityType(typeof(Reservation));
        Assert.NotNull(reservation);

        var foreignKeys = reservation.GetForeignKeys().ToList();
        Assert.Equal(2, foreignKeys.Count);
        Assert.Contains(foreignKeys, key => key.PrincipalEntityType.ClrType == typeof(Listing));
        Assert.Contains(foreignKeys, key => key.PrincipalEntityType.ClrType == typeof(ApplicationUser));
        Assert.All(foreignKeys, key =>
        {
            Assert.True(key.IsRequired);
            Assert.Equal(DeleteBehavior.Restrict, key.DeleteBehavior);
        });
    }
}

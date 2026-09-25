using LUThrift.Web.Models;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace LUThrift.Web.Data;

public class ApplicationDbContext(DbContextOptions<ApplicationDbContext> options)
    : IdentityDbContext<ApplicationUser>(options)
{
    public DbSet<Category> Categories => Set<Category>();
    public DbSet<Listing> Listings => Set<Listing>();
    public DbSet<Reservation> Reservations => Set<Reservation>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);
        builder.ApplyConfigurationsFromAssembly(typeof(ApplicationDbContext).Assembly);

        builder.Entity<ApplicationUser>(user =>
        {
            user.Property(u => u.DisplayName).IsRequired().HasMaxLength(100);
            user.Property(u => u.IsActive).IsRequired().HasDefaultValue(true);
            user.Property(u => u.CreatedAtUtc).IsRequired().HasColumnType("timestamp with time zone");
        });
    }
}

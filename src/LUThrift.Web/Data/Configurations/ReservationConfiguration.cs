using LUThrift.Web.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace LUThrift.Web.Data.Configurations;

public class ReservationConfiguration : IEntityTypeConfiguration<Reservation>
{
    public void Configure(EntityTypeBuilder<Reservation> builder)
    {
        builder.ToTable("Reservations");
        builder.HasKey(r => r.Id);
        builder.Property(r => r.UserId).IsRequired();
        builder.Property(r => r.CreatedAtUtc).IsRequired().HasColumnType("timestamp with time zone");
        builder.Property(r => r.ExpiresAtUtc).IsRequired().HasColumnType("timestamp with time zone");
        builder.Property(r => r.FulfilledAtUtc).HasColumnType("timestamp with time zone");
        builder.Property(r => r.Status).IsRequired().HasConversion<string>().HasMaxLength(20);
        builder.Property(r => r.CancellationReason).HasMaxLength(500);

        builder.HasOne(r => r.Listing)
            .WithMany(l => l.Reservations)
            .HasForeignKey(r => r.ListingId)
            .IsRequired()
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne(r => r.User)
            .WithMany(u => u.Reservations)
            .HasForeignKey(r => r.UserId)
            .IsRequired()
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(r => r.ListingId);
        builder.HasIndex(r => new { r.UserId, r.Status });
        builder.HasIndex(r => new { r.Status, r.ExpiresAtUtc });
    }
}

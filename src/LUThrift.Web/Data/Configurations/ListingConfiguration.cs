using LUThrift.Web.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace LUThrift.Web.Data.Configurations;

public class ListingConfiguration : IEntityTypeConfiguration<Listing>
{
    public void Configure(EntityTypeBuilder<Listing> builder)
    {
        builder.ToTable("Listings");
        builder.HasKey(l => l.Id);
        builder.Property(l => l.Title).IsRequired().HasMaxLength(150);
        builder.Property(l => l.Description).IsRequired().HasMaxLength(2000);
        builder.Property(l => l.Condition).IsRequired().HasMaxLength(100);
        builder.Property(l => l.Size).HasMaxLength(50);
        builder.Property(l => l.Status).IsRequired().HasConversion<string>().HasMaxLength(20);
        builder.Property(l => l.CreatedAtUtc).IsRequired().HasColumnType("timestamp with time zone");

        builder.HasOne(l => l.Category)
            .WithMany(c => c.Listings)
            .HasForeignKey(l => l.CategoryId)
            .IsRequired()
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(l => l.CategoryId);
        builder.HasIndex(l => l.Status);

        var seededAtUtc = new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc);

        builder.HasData(
            new
            {
                Id = 1,
                Title = "Blue hoodie",
                Description = "Blue pullover hoodie with a front pocket. Adult medium.",
                Condition = "Good",
                Size = "M",
                CategoryId = 1,
                Status = ListingStatus.Available,
                CreatedAtUtc = seededAtUtc
            },
            new
            {
                Id = 2,
                Title = "Desk lamp",
                Description = "Small adjustable desk lamp for a dorm room or study space.",
                Condition = "Good",
                Size = (string?)null,
                CategoryId = 2,
                Status = ListingStatus.Available,
                CreatedAtUtc = seededAtUtc
            },
            new
            {
                Id = 3,
                Title = "Ceramic mug",
                Description = "Plain ceramic mug with a handle and no visible chips.",
                Condition = "Good",
                Size = (string?)null,
                CategoryId = 2,
                Status = ListingStatus.Available,
                CreatedAtUtc = seededAtUtc
            },
            new
            {
                Id = 4,
                Title = "Introductory calculus textbook",
                Description = "Introductory calculus textbook with some handwritten study notes.",
                Condition = "Used",
                Size = (string?)null,
                CategoryId = 3,
                Status = ListingStatus.Available,
                CreatedAtUtc = seededAtUtc
            });
    }
}

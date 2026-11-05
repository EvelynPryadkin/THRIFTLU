using LUThrift.Web.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace LUThrift.Web.Data.Configurations;

public sealed class ListingImageConfiguration : IEntityTypeConfiguration<ListingImage>
{
    public void Configure(EntityTypeBuilder<ListingImage> builder)
    {
        builder.ToTable("ListingImages");
        builder.HasKey(image => image.Id);
        builder.Property(image => image.Id).ValueGeneratedNever();
        builder.Property(image => image.StorageFileName).IsRequired().HasMaxLength(40);
        builder.Property(image => image.ContentType).IsRequired().HasMaxLength(20);
        builder.Property(image => image.CreatedAtUtc).HasColumnType("timestamp with time zone");
        builder.HasIndex(image => image.StorageFileName).IsUnique();
        builder.HasOne(image => image.Listing).WithOne(listing => listing.Image)
            .HasForeignKey<ListingImage>(image => image.ListingId)
            .IsRequired().OnDelete(DeleteBehavior.Restrict);
    }
}

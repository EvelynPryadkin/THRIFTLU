using LUThrift.Web.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace LUThrift.Web.Data.Configurations;

public class CategoryConfiguration : IEntityTypeConfiguration<Category>
{
    public void Configure(EntityTypeBuilder<Category> builder)
    {
        builder.ToTable("Categories");
        builder.HasKey(c => c.Id);
        builder.Property(c => c.Name).IsRequired().HasMaxLength(80);
        builder.Property(c => c.DisplayOrder).IsRequired();
        builder.HasIndex(c => c.Name).IsUnique();

        builder.HasData(
            new { Id = 1, Name = "Clothing", DisplayOrder = 1 },
            new { Id = 2, Name = "Dorm supplies", DisplayOrder = 2 },
            new { Id = 3, Name = "Books", DisplayOrder = 3 });
    }
}

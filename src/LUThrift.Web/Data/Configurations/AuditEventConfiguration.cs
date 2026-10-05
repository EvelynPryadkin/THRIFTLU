using LUThrift.Web.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace LUThrift.Web.Data.Configurations;

public class AuditEventConfiguration : IEntityTypeConfiguration<AuditEvent>
{
    public void Configure(EntityTypeBuilder<AuditEvent> builder)
    {
        builder.ToTable("AuditEvents");
        builder.HasKey(a => a.Id);
        builder.Property(a => a.ActorUserId).IsRequired().HasMaxLength(450);
        builder.Property(a => a.Action).IsRequired().HasMaxLength(50);
        builder.Property(a => a.OccurredAtUtc).IsRequired().HasColumnType("timestamp with time zone");
        builder.Property(a => a.Details).HasMaxLength(500);

        builder.HasOne(a => a.Listing)
            .WithMany()
            .HasForeignKey(a => a.ListingId)
            .IsRequired()
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(a => new { a.ListingId, a.OccurredAtUtc });
    }
}

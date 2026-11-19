using LUThrift.Web.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace LUThrift.Web.Data.Configurations;

public class AdministrativeAuditEventConfiguration : IEntityTypeConfiguration<AdministrativeAuditEvent>
{
    public void Configure(EntityTypeBuilder<AdministrativeAuditEvent> builder)
    {
        builder.ToTable("AdministrativeAuditEvents");
        builder.HasKey(a => a.Id);
        builder.Property(a => a.ActorUserId).IsRequired().HasMaxLength(450);
        builder.Property(a => a.TargetUserId).IsRequired().HasMaxLength(450);
        builder.Property(a => a.Action).IsRequired().HasMaxLength(50);
        builder.Property(a => a.OccurredAtUtc).IsRequired().HasColumnType("timestamp with time zone");

        builder.HasIndex(a => new { a.TargetUserId, a.OccurredAtUtc });
    }
}

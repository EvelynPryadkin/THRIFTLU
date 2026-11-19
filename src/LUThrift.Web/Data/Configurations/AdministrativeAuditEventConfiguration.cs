using LUThrift.Web.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace LUThrift.Web.Data.Configurations;

public class AdministrativeAuditEventConfiguration : IEntityTypeConfiguration<AdministrativeAuditEvent>
{
    public void Configure(EntityTypeBuilder<AdministrativeAuditEvent> builder)
    {
        builder.ToTable("AdministrativeAuditEvents", table => table.HasCheckConstraint(
            "CK_AdministrativeAuditEvents_OneTarget",
            "(\"TargetUserId\" IS NOT NULL AND \"TargetScheduleId\" IS NULL) OR " +
            "(\"TargetUserId\" IS NULL AND \"TargetScheduleId\" IS NOT NULL AND \"TargetScheduleId\" > 0)"));
        builder.HasKey(a => a.Id);
        builder.Property(a => a.ActorUserId).IsRequired().HasMaxLength(450);
        builder.Property(a => a.TargetUserId).HasMaxLength(450);
        builder.Property(a => a.Action).IsRequired().HasMaxLength(50);
        builder.Property(a => a.OccurredAtUtc).IsRequired().HasColumnType("timestamp with time zone");

        builder.HasIndex(a => new { a.TargetUserId, a.OccurredAtUtc });
        builder.HasIndex(a => new { a.TargetScheduleId, a.OccurredAtUtc });
    }
}

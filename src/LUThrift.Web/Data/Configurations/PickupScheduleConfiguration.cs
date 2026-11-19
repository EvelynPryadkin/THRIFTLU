using LUThrift.Web.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace LUThrift.Web.Data.Configurations;

public sealed class PickupScheduleConfiguration : IEntityTypeConfiguration<PickupSchedule>
{
    public void Configure(EntityTypeBuilder<PickupSchedule> builder)
    {
        builder.ToTable("PickupSchedules", table =>
            table.HasCheckConstraint("CK_PickupSchedules_Singleton", "\"Id\" = 1"));
        builder.HasKey(schedule => schedule.Id);
        builder.Property(schedule => schedule.Id).ValueGeneratedNever();
        builder.Property(schedule => schedule.Version).IsRequired().IsConcurrencyToken();
        builder.Property(schedule => schedule.CampusTimeZone).IsRequired().HasMaxLength(100);
    }
}

public sealed class PickupScheduleIntervalConfiguration : IEntityTypeConfiguration<PickupScheduleInterval>
{
    public void Configure(EntityTypeBuilder<PickupScheduleInterval> builder)
    {
        builder.ToTable("PickupScheduleIntervals", table =>
            table.HasCheckConstraint("CK_PickupScheduleIntervals_PositiveSpan", "\"OpensAt\" < \"ClosesAt\""));
        builder.HasKey(interval => new { interval.ScheduleId, interval.Day, interval.OpensAt });
        builder.Property(interval => interval.Day).HasConversion<string>().HasMaxLength(9).IsRequired();
        builder.Property(interval => interval.OpensAt).HasColumnType("time without time zone");
        builder.Property(interval => interval.ClosesAt).HasColumnType("time without time zone");
        builder.HasOne(interval => interval.Schedule).WithMany(schedule => schedule.WeeklyIntervals)
            .HasForeignKey(interval => interval.ScheduleId).IsRequired().OnDelete(DeleteBehavior.Cascade);
    }
}

public sealed class PickupScheduleClosedDateConfiguration : IEntityTypeConfiguration<PickupScheduleClosedDate>
{
    public void Configure(EntityTypeBuilder<PickupScheduleClosedDate> builder)
    {
        builder.ToTable("PickupScheduleClosedDates");
        builder.HasKey(closed => new { closed.ScheduleId, closed.Date });
        builder.Property(closed => closed.Date).HasColumnType("date");
        builder.HasOne(closed => closed.Schedule).WithMany(schedule => schedule.ClosedDates)
            .HasForeignKey(closed => closed.ScheduleId).IsRequired().OnDelete(DeleteBehavior.Cascade);
    }
}

using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LUThrift.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class PersistPickupSchedules : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<string>(
                name: "TargetUserId",
                table: "AdministrativeAuditEvents",
                type: "character varying(450)",
                maxLength: 450,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "character varying(450)",
                oldMaxLength: 450);

            migrationBuilder.AddColumn<int>(
                name: "TargetScheduleId",
                table: "AdministrativeAuditEvents",
                type: "integer",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "PickupSchedules",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false),
                    Version = table.Column<Guid>(type: "uuid", nullable: false),
                    CampusTimeZone = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PickupSchedules", x => x.Id);
                    table.CheckConstraint("CK_PickupSchedules_Singleton", "\"Id\" = 1");
                });

            migrationBuilder.CreateTable(
                name: "PickupScheduleClosedDates",
                columns: table => new
                {
                    ScheduleId = table.Column<int>(type: "integer", nullable: false),
                    Date = table.Column<DateOnly>(type: "date", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PickupScheduleClosedDates", x => new { x.ScheduleId, x.Date });
                    table.ForeignKey(
                        name: "FK_PickupScheduleClosedDates_PickupSchedules_ScheduleId",
                        column: x => x.ScheduleId,
                        principalTable: "PickupSchedules",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "PickupScheduleIntervals",
                columns: table => new
                {
                    ScheduleId = table.Column<int>(type: "integer", nullable: false),
                    Day = table.Column<string>(type: "character varying(9)", maxLength: 9, nullable: false),
                    OpensAt = table.Column<TimeOnly>(type: "time without time zone", nullable: false),
                    ClosesAt = table.Column<TimeOnly>(type: "time without time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PickupScheduleIntervals", x => new { x.ScheduleId, x.Day, x.OpensAt });
                    table.CheckConstraint("CK_PickupScheduleIntervals_PositiveSpan", "\"OpensAt\" < \"ClosesAt\"");
                    table.ForeignKey(
                        name: "FK_PickupScheduleIntervals_PickupSchedules_ScheduleId",
                        column: x => x.ScheduleId,
                        principalTable: "PickupSchedules",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AdministrativeAuditEvents_TargetScheduleId_OccurredAtUtc",
                table: "AdministrativeAuditEvents",
                columns: new[] { "TargetScheduleId", "OccurredAtUtc" });

            migrationBuilder.AddCheckConstraint(
                name: "CK_AdministrativeAuditEvents_OneTarget",
                table: "AdministrativeAuditEvents",
                sql: "(\"TargetUserId\" IS NOT NULL AND \"TargetScheduleId\" IS NULL) OR (\"TargetUserId\" IS NULL AND \"TargetScheduleId\" IS NOT NULL AND \"TargetScheduleId\" > 0)");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // The older schema cannot represent a schedule audit. Refuse to lose
            // its target or manufacture an account ID when rolling back.
            migrationBuilder.Sql("""
                DO $$
                BEGIN
                    IF EXISTS (SELECT 1 FROM "AdministrativeAuditEvents" WHERE "TargetScheduleId" IS NOT NULL) THEN
                        RAISE EXCEPTION 'Cannot revert pickup schedule storage while schedule audit history exists. Preserve that history before planning a schema rollback.';
                    END IF;
                END $$;
                """);

            migrationBuilder.DropTable(
                name: "PickupScheduleClosedDates");

            migrationBuilder.DropTable(
                name: "PickupScheduleIntervals");

            migrationBuilder.DropTable(
                name: "PickupSchedules");

            migrationBuilder.DropIndex(
                name: "IX_AdministrativeAuditEvents_TargetScheduleId_OccurredAtUtc",
                table: "AdministrativeAuditEvents");

            migrationBuilder.DropCheckConstraint(
                name: "CK_AdministrativeAuditEvents_OneTarget",
                table: "AdministrativeAuditEvents");

            migrationBuilder.DropColumn(
                name: "TargetScheduleId",
                table: "AdministrativeAuditEvents");

            migrationBuilder.AlterColumn<string>(
                name: "TargetUserId",
                table: "AdministrativeAuditEvents",
                type: "character varying(450)",
                maxLength: 450,
                nullable: false,
                defaultValue: "",
                oldClrType: typeof(string),
                oldType: "character varying(450)",
                oldMaxLength: 450,
                oldNullable: true);
        }
    }
}

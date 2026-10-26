using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LUThrift.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class EnforceSingleActiveReservationPerListing : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            if (ActiveProvider == "Npgsql.EntityFrameworkCore.PostgreSQL")
            {
                migrationBuilder.Sql("""
                    DO $audit$
                    DECLARE conflicting_listing_ids text;
                    BEGIN
                        SELECT string_agg("ListingId"::text, ', ' ORDER BY "ListingId")
                        INTO conflicting_listing_ids
                        FROM (
                            SELECT "ListingId" FROM "Reservations"
                            WHERE "Status" = 'Active'
                            GROUP BY "ListingId" HAVING COUNT(*) > 1
                            ORDER BY "ListingId" LIMIT 20
                        ) conflicts;
                        IF conflicting_listing_ids IS NOT NULL THEN
                            RAISE EXCEPTION 'Cannot enforce one Active reservation per listing. Conflicting listing IDs: % (up to 20 shown). Resolve these records before retrying; no reservation data was changed.', conflicting_listing_ids;
                        END IF;
                    END $audit$;
                    """);
            }

            migrationBuilder.CreateIndex(
                name: "IX_Reservations_ActiveListing",
                table: "Reservations",
                column: "ListingId",
                unique: true,
                filter: "\"Status\" = 'Active'");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Reservations_ActiveListing",
                table: "Reservations");
        }
    }
}

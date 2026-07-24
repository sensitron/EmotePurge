using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EmotePurge.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class ConvertUsageStatDateToDateOnly : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_UsageStats_EmoteId_Date",
                table: "UsageStats");

            // Explicit UTC-anchored cast instead of a bare ALTER COLUMN TYPE: Postgres' built-in
            // timestamptz->date assignment cast is evaluated against the session's TimeZone GUC
            // (not pinned to UTC in docker-compose.yml), which could silently shift rows by a day.
            migrationBuilder.Sql(@"
                ALTER TABLE ""UsageStats""
                ALTER COLUMN ""Date"" TYPE date
                USING ((""Date"" AT TIME ZONE 'UTC')::date);
            ");

            migrationBuilder.CreateIndex(
                name: "IX_UsageStats_EmoteId_Date",
                table: "UsageStats",
                columns: new[] { "EmoteId", "Date" },
                unique: true)
                .Annotation("Npgsql:IndexInclude", new[] { "UseCount" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_UsageStats_EmoteId_Date",
                table: "UsageStats");

            migrationBuilder.Sql(@"
                ALTER TABLE ""UsageStats""
                ALTER COLUMN ""Date"" TYPE timestamp with time zone
                USING (""Date""::timestamp AT TIME ZONE 'UTC');
            ");

            migrationBuilder.CreateIndex(
                name: "IX_UsageStats_EmoteId_Date",
                table: "UsageStats",
                columns: new[] { "EmoteId", "Date" },
                unique: true);
        }
    }
}

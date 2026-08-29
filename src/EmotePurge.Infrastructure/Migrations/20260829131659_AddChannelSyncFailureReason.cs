using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EmotePurge.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddChannelSyncFailureReason : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "LastSyncAttemptAtUtc",
                table: "Channels",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LastSyncFailureReason",
                table: "Channels",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "LastSyncAttemptAtUtc",
                table: "Channels");

            migrationBuilder.DropColumn(
                name: "LastSyncFailureReason",
                table: "Channels");
        }
    }
}

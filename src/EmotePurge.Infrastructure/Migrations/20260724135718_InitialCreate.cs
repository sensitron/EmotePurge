using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace EmotePurge.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Channels",
                columns: table => new
                {
                    Id = table.Column<string>(type: "text", nullable: false),
                    TwitchChannelId = table.Column<string>(type: "text", nullable: true),
                    ChannelName = table.Column<string>(type: "text", nullable: false),
                    ActiveEmoteSetId = table.Column<string>(type: "text", nullable: false),
                    IsBotActive = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Channels", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Emotes",
                columns: table => new
                {
                    Id = table.Column<string>(type: "text", nullable: false),
                    SevenTvEmoteId = table.Column<string>(type: "text", nullable: false),
                    ChannelId = table.Column<string>(type: "text", nullable: false),
                    Name = table.Column<string>(type: "text", nullable: false),
                    ImageUrl = table.Column<string>(type: "text", nullable: false),
                    IsArchived = table.Column<bool>(type: "boolean", nullable: false),
                    LastSyncedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Emotes", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Emotes_Channels_ChannelId",
                        column: x => x.ChannelId,
                        principalTable: "Channels",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "UsageStats",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    EmoteId = table.Column<string>(type: "text", nullable: false),
                    Date = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UseCount = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UsageStats", x => x.Id);
                    table.ForeignKey(
                        name: "FK_UsageStats_Emotes_EmoteId",
                        column: x => x.EmoteId,
                        principalTable: "Emotes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Channels_ChannelName",
                table: "Channels",
                column: "ChannelName",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Channels_TwitchChannelId",
                table: "Channels",
                column: "TwitchChannelId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Emotes_ChannelId_SevenTvEmoteId",
                table: "Emotes",
                columns: new[] { "ChannelId", "SevenTvEmoteId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_UsageStats_EmoteId_Date",
                table: "UsageStats",
                columns: new[] { "EmoteId", "Date" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "UsageStats");

            migrationBuilder.DropTable(
                name: "Emotes");

            migrationBuilder.DropTable(
                name: "Channels");
        }
    }
}

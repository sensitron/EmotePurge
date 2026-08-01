using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EmotePurge.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddVoteSessionEmote : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "VoteSessionEmotes",
                columns: table => new
                {
                    VoteSessionId = table.Column<long>(type: "bigint", nullable: false),
                    EmoteId = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_VoteSessionEmotes", x => new { x.VoteSessionId, x.EmoteId });
                    table.ForeignKey(
                        name: "FK_VoteSessionEmotes_Emotes_EmoteId",
                        column: x => x.EmoteId,
                        principalTable: "Emotes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_VoteSessionEmotes_VoteSessions_VoteSessionId",
                        column: x => x.VoteSessionId,
                        principalTable: "VoteSessions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_VoteSessionEmotes_EmoteId",
                table: "VoteSessionEmotes",
                column: "EmoteId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "VoteSessionEmotes");
        }
    }
}

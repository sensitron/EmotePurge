using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EmotePurge.Infrastructure.Migrations
{
    /// <summary>
    /// Every FirstSeenAt written before 2026-08-03 came from the v3 payload's timestamp field,
    /// which is the emote's 7TV upload date, not the set-entry date. Reset to NULL: active emotes
    /// are refilled with the real v4 addedAt by the next resync tick; archived emotes are never
    /// resynced, and for them an honest "unknown" beats a confidently wrong date.
    /// </summary>
    public partial class ResetMisattributedFirstSeenAt : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""UPDATE "Emotes" SET "FirstSeenAt" = NULL;""");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // The overwritten values are not recoverable — and were wrong anyway.
        }
    }
}

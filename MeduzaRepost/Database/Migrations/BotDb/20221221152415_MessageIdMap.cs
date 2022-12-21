using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MeduzaRepost.Database.Migrations.BotDb
{
    /// <inheritdoc />
    public partial class MessageIdMap : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "MessageMaps",
                columns: table => new
                {
                    TelegramId = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    MastodonId = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MessageMaps", x => x.TelegramId);
                });

            migrationBuilder.CreateIndex(
                name: "IX_MessageMaps_MastodonId",
                table: "MessageMaps",
                column: "MastodonId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "MessageMaps");
        }
    }
}

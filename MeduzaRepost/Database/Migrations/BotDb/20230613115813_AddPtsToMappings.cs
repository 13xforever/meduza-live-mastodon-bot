using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MeduzaRepost.Database.Migrations.BotDb
{
    /// <inheritdoc />
    public partial class AddPtsToMappings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "Pts",
                table: "MessageMaps",
                type: "INTEGER",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Pts",
                table: "MessageMaps");
        }
    }
}

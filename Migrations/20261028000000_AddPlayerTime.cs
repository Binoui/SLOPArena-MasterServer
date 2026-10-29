using MasterServer.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace MasterServer.Migrations;

[DbContext(typeof(AppDbContext))]
[Migration("20261028000000_AddPlayerTime")]
public sealed class AddPlayerTime : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "PlayerSessions",
            columns: table => new
            {
                Id = table.Column<long>(type: "bigint", nullable: false)
                    .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                SteamId = table.Column<long>(type: "bigint", nullable: false),
                ConnectedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                DisconnectedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
            },
            constraints: table => table.PrimaryKey("PK_PlayerSessions", x => x.Id));
        migrationBuilder.CreateIndex("IX_PlayerSessions_SteamId", "PlayerSessions", "SteamId");
        for (var slot = 1; slot <= 4; slot++)
            migrationBuilder.AddColumn<int>($"Player{slot}ConnectedSeconds", "Matches", type: "integer", nullable: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        for (var slot = 1; slot <= 4; slot++)
            migrationBuilder.DropColumn($"Player{slot}ConnectedSeconds", "Matches");
        migrationBuilder.DropTable("PlayerSessions");
    }
}

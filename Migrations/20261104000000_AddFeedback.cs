using System;
using MasterServer.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MasterServer.Migrations
{
    [DbContext(typeof(AppDbContext))]
    [Migration("20261104000000_AddFeedback")]
    public sealed class AddFeedback : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "FeedbackReports",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ReporterUserId = table.Column<long>(type: "bigint", nullable: false),
                    SubmissionId = table.Column<Guid>(type: "uuid", nullable: false),
                    ReceivedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Kind = table.Column<string>(type: "character varying(4)", maxLength: 4, nullable: false),
                    Message = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    ContextJson = table.Column<string>(type: "jsonb", nullable: false),
                    AppVersion = table.Column<string>(type: "text", nullable: false),
                    OnlineMatchId = table.Column<Guid>(type: "uuid", nullable: true),
                    HasScreenshot = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FeedbackReports", x => x.Id);
                    table.ForeignKey(
                        name: "FK_FeedbackReports_Users_ReporterUserId",
                        column: x => x.ReporterUserId,
                        principalTable: "Users",
                        principalColumn: "SteamId",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "FeedbackScreenshots",
                columns: table => new
                {
                    ReportId = table.Column<Guid>(type: "uuid", nullable: false),
                    Jpeg = table.Column<byte[]>(type: "bytea", nullable: false),
                    Bytes = table.Column<int>(type: "integer", nullable: false),
                    Sha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FeedbackScreenshots", x => x.ReportId);
                    table.CheckConstraint("CK_FeedbackScreenshots_Size", "octet_length(\"Jpeg\") BETWEEN 3 AND 1048576 AND \"Bytes\" = octet_length(\"Jpeg\")");
                    table.ForeignKey(
                        name: "FK_FeedbackScreenshots_FeedbackReports_ReportId",
                        column: x => x.ReportId,
                        principalTable: "FeedbackReports",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_FeedbackReports_ReceivedAtUtc_Id",
                table: "FeedbackReports",
                columns: new[] { "ReceivedAtUtc", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_FeedbackReports_ReporterUserId_ReceivedAtUtc",
                table: "FeedbackReports",
                columns: new[] { "ReporterUserId", "ReceivedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_FeedbackReports_ReporterUserId_SubmissionId",
                table: "FeedbackReports",
                columns: new[] { "ReporterUserId", "SubmissionId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "FeedbackScreenshots");

            migrationBuilder.DropTable(
                name: "FeedbackReports");
        }
    }
}

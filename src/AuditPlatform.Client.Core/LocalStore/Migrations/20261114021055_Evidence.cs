using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AuditPlatform.Client.Core.LocalStore.Migrations
{
    /// <inheritdoc />
    public partial class Evidence : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "evidence",
                columns: table => new
                {
                    EngagementId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Hash = table.Column<string>(type: "TEXT", nullable: false),
                    FileName = table.Column<string>(type: "TEXT", nullable: false),
                    SizeBytes = table.Column<long>(type: "INTEGER", nullable: false),
                    AddedBy = table.Column<Guid>(type: "TEXT", nullable: false),
                    AddedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    Upload = table.Column<string>(type: "TEXT", nullable: false),
                    LastError = table.Column<string>(type: "TEXT", nullable: true),
                    Attempts = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_evidence", x => new { x.EngagementId, x.Hash });
                });

            migrationBuilder.CreateIndex(
                name: "IX_evidence_EngagementId_Upload",
                table: "evidence",
                columns: new[] { "EngagementId", "Upload" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "evidence");
        }
    }
}

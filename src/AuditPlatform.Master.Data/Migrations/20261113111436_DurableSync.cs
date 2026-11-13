using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AuditPlatform.Master.Data.Migrations
{
    /// <inheritdoc />
    public partial class DurableSync : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "notifications",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    kind = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    recipient_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    engagement_id = table.Column<Guid>(type: "uuid", nullable: false),
                    document_id = table.Column<Guid>(type: "uuid", nullable: false),
                    change_id = table.Column<Guid>(type: "uuid", nullable: false),
                    actor_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    attempts = table.Column<int>(type: "integer", nullable: false),
                    last_attempt_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    last_error = table.Column<string>(type: "character varying(400)", maxLength: 400, nullable: true),
                    sent_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_notifications", x => x.id);
                    table.ForeignKey(
                        name: "fk_notifications_engagements_engagement_id",
                        column: x => x.engagement_id,
                        principalTable: "engagements",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "sync_documents",
                columns: table => new
                {
                    document_id = table.Column<Guid>(type: "uuid", nullable: false),
                    engagement_id = table.Column<Guid>(type: "uuid", nullable: false),
                    template_code = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    preparer_id = table.Column<Guid>(type: "uuid", nullable: false),
                    status = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    content_hash = table.Column<string>(type: "character(64)", fixedLength: true, maxLength: 64, nullable: false),
                    open_conflicts = table.Column<int>(type: "integer", nullable: false),
                    last_sequence = table.Column<long>(type: "bigint", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    state = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_sync_documents", x => x.document_id);
                    table.ForeignKey(
                        name: "fk_sync_documents_engagements_engagement_id",
                        column: x => x.engagement_id,
                        principalTable: "engagements",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "sync_receipts",
                columns: table => new
                {
                    change_id = table.Column<Guid>(type: "uuid", nullable: false),
                    engagement_id = table.Column<Guid>(type: "uuid", nullable: false),
                    document_id = table.Column<Guid>(type: "uuid", nullable: false),
                    author_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    outcome = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    reason = table.Column<string>(type: "text", nullable: true),
                    sequence = table.Column<long>(type: "bigint", nullable: true),
                    received_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_sync_receipts", x => x.change_id);
                    table.ForeignKey(
                        name: "fk_sync_receipts_engagements_engagement_id",
                        column: x => x.engagement_id,
                        principalTable: "engagements",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_notifications_created_at",
                table: "notifications",
                column: "created_at",
                filter: "sent_at IS NULL");

            migrationBuilder.CreateIndex(
                name: "ix_notifications_engagement_id",
                table: "notifications",
                column: "engagement_id");

            migrationBuilder.CreateIndex(
                name: "ix_notifications_recipient_user_id_created_at",
                table: "notifications",
                columns: new[] { "recipient_user_id", "created_at" });

            migrationBuilder.CreateIndex(
                name: "ix_sync_documents_engagement_id",
                table: "sync_documents",
                column: "engagement_id");

            migrationBuilder.CreateIndex(
                name: "ix_sync_receipts_engagement_id",
                table: "sync_receipts",
                column: "engagement_id");

            // The change log is the system of record for working-paper content (ADR-0004): append-only, like audit_log.
            migrationBuilder.Sql("""
                CREATE FUNCTION changes_reject_mutation() RETURNS trigger
                LANGUAGE plpgsql AS $$
                BEGIN
                    RAISE EXCEPTION 'changes is append-only (% rejected)', TG_OP
                        USING ERRCODE = 'insufficient_privilege';
                END;
                $$;

                CREATE TRIGGER changes_no_update_delete
                    BEFORE UPDATE OR DELETE ON changes
                    FOR EACH ROW EXECUTE FUNCTION changes_reject_mutation();

                CREATE TRIGGER changes_no_truncate
                    BEFORE TRUNCATE ON changes
                    FOR EACH STATEMENT EXECUTE FUNCTION changes_reject_mutation();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DROP TRIGGER IF EXISTS changes_no_truncate ON changes;
                DROP TRIGGER IF EXISTS changes_no_update_delete ON changes;
                DROP FUNCTION IF EXISTS changes_reject_mutation();
                """);

            migrationBuilder.DropTable(
                name: "notifications");

            migrationBuilder.DropTable(
                name: "sync_documents");

            migrationBuilder.DropTable(
                name: "sync_receipts");
        }
    }
}

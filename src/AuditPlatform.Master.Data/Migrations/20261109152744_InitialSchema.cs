using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AuditPlatform.Master.Data.Migrations
{
    /// <inheritdoc />
    public partial class InitialSchema : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "audit_log",
                columns: table => new
                {
                    sequence = table.Column<long>(type: "bigint", nullable: false),
                    occurred_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    actor_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    action = table.Column<string>(type: "text", nullable: false),
                    entity_type = table.Column<string>(type: "text", nullable: false),
                    entity_id = table.Column<string>(type: "text", nullable: false),
                    engagement_id = table.Column<Guid>(type: "uuid", nullable: true),
                    payload_json = table.Column<string>(type: "text", nullable: false),
                    previous_hash = table.Column<string>(type: "character(64)", fixedLength: true, maxLength: 64, nullable: false),
                    hash = table.Column<string>(type: "character(64)", fixedLength: true, maxLength: 64, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_audit_log", x => x.sequence);
                });

            migrationBuilder.CreateTable(
                name: "engagements",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    code = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    client_name = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    contract_reference = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    period_start = table.Column<DateOnly>(type: "date", nullable: false),
                    period_end = table.Column<DateOnly>(type: "date", nullable: false),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    locked_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    locked_by = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_engagements", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "evidence_objects",
                columns: table => new
                {
                    hash = table.Column<string>(type: "character(64)", fixedLength: true, maxLength: 64, nullable: false),
                    size_bytes = table.Column<long>(type: "bigint", nullable: false),
                    first_received_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_evidence_objects", x => x.hash);
                });

            migrationBuilder.CreateTable(
                name: "procedure_templates",
                columns: table => new
                {
                    code = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    version = table.Column<int>(type: "integer", nullable: false),
                    language = table.Column<string>(type: "character varying(5)", maxLength: 5, nullable: false),
                    area_code = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    area_name = table.Column<string>(type: "text", nullable: false),
                    title = table.Column<string>(type: "text", nullable: false),
                    procedure = table.Column<string>(type: "text", nullable: false),
                    audit_content = table.Column<List<string>>(type: "text[]", nullable: false),
                    evidence_to_check = table.Column<List<string>>(type: "text[]", nullable: false),
                    is_approved = table.Column<bool>(type: "boolean", nullable: false),
                    approved_by = table.Column<Guid>(type: "uuid", nullable: true),
                    approved_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_procedure_templates", x => new { x.code, x.version, x.language });
                });

            migrationBuilder.CreateTable(
                name: "users",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    email = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false),
                    display_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    is_active = table.Column<bool>(type: "boolean", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_users", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "changes",
                columns: table => new
                {
                    engagement_id = table.Column<Guid>(type: "uuid", nullable: false),
                    sequence = table.Column<long>(type: "bigint", nullable: false),
                    change_id = table.Column<Guid>(type: "uuid", nullable: false),
                    document_id = table.Column<Guid>(type: "uuid", nullable: false),
                    author_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    client_id = table.Column<Guid>(type: "uuid", nullable: false),
                    client_created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    received_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    payload_kind = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    payload = table.Column<byte[]>(type: "bytea", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_changes", x => new { x.engagement_id, x.sequence });
                    table.ForeignKey(
                        name: "fk_changes_engagements_engagement_id",
                        column: x => x.engagement_id,
                        principalTable: "engagements",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "evidence_intake",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    hash = table.Column<string>(type: "character(64)", fixedLength: true, maxLength: 64, nullable: false),
                    engagement_id = table.Column<Guid>(type: "uuid", nullable: false),
                    original_file_name = table.Column<string>(type: "character varying(400)", maxLength: 400, nullable: false),
                    media_type = table.Column<string>(type: "text", nullable: true),
                    received_by = table.Column<Guid>(type: "uuid", nullable: false),
                    received_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    source = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_evidence_intake", x => x.id);
                    table.ForeignKey(
                        name: "fk_evidence_intake_evidence_objects_hash",
                        column: x => x.hash,
                        principalTable: "evidence_objects",
                        principalColumn: "hash",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "engagement_members",
                columns: table => new
                {
                    engagement_id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    role = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_engagement_members", x => new { x.engagement_id, x.user_id, x.role });
                    table.ForeignKey(
                        name: "fk_engagement_members_engagements_engagement_id",
                        column: x => x.engagement_id,
                        principalTable: "engagements",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_engagement_members_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_audit_log_engagement_id",
                table: "audit_log",
                column: "engagement_id");

            migrationBuilder.CreateIndex(
                name: "ix_changes_change_id",
                table: "changes",
                column: "change_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_changes_engagement_id_document_id",
                table: "changes",
                columns: new[] { "engagement_id", "document_id" });

            migrationBuilder.CreateIndex(
                name: "ix_engagement_members_user_id",
                table: "engagement_members",
                column: "user_id");

            migrationBuilder.CreateIndex(
                name: "ix_engagements_code",
                table: "engagements",
                column: "code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_evidence_intake_engagement_id_hash",
                table: "evidence_intake",
                columns: new[] { "engagement_id", "hash" });

            migrationBuilder.CreateIndex(
                name: "ix_evidence_intake_hash",
                table: "evidence_intake",
                column: "hash");

            migrationBuilder.CreateIndex(
                name: "ix_users_email",
                table: "users",
                column: "email",
                unique: true);
            // Append-only enforcement for the audit log (design doc 7.4): reject UPDATE, DELETE and TRUNCATE
            // regardless of the application role. Hash-chain verification detects anything done around this.
            migrationBuilder.Sql("""
                CREATE FUNCTION audit_log_reject_mutation() RETURNS trigger
                LANGUAGE plpgsql AS $$
                BEGIN
                    RAISE EXCEPTION 'audit_log is append-only (% rejected)', TG_OP
                        USING ERRCODE = 'insufficient_privilege';
                END;
                $$;

                CREATE TRIGGER audit_log_no_update_delete
                    BEFORE UPDATE OR DELETE ON audit_log
                    FOR EACH ROW EXECUTE FUNCTION audit_log_reject_mutation();

                CREATE TRIGGER audit_log_no_truncate
                    BEFORE TRUNCATE ON audit_log
                    FOR EACH STATEMENT EXECUTE FUNCTION audit_log_reject_mutation();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DROP TRIGGER IF EXISTS audit_log_no_truncate ON audit_log;
                DROP TRIGGER IF EXISTS audit_log_no_update_delete ON audit_log;
                DROP FUNCTION IF EXISTS audit_log_reject_mutation();
                """);

            migrationBuilder.DropTable(
                name: "audit_log");

            migrationBuilder.DropTable(
                name: "changes");

            migrationBuilder.DropTable(
                name: "engagement_members");

            migrationBuilder.DropTable(
                name: "evidence_intake");

            migrationBuilder.DropTable(
                name: "procedure_templates");

            migrationBuilder.DropTable(
                name: "engagements");

            migrationBuilder.DropTable(
                name: "users");

            migrationBuilder.DropTable(
                name: "evidence_objects");
        }
    }
}

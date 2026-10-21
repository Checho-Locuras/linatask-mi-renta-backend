using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

#pragma warning disable CA1814 // Prefer jagged arrays over multidimensional

namespace MiRenta.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "mi_renta");

            migrationBuilder.CreateTable(
                name: "audit_events",
                schema: "mi_renta",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    case_id = table.Column<Guid>(type: "uuid", nullable: true),
                    user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    action = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    details_json = table.Column<string>(type: "jsonb", nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_audit_events", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "background_jobs",
                schema: "mi_renta",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    kind = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    payload_json = table.Column<string>(type: "jsonb", nullable: false),
                    dedup_key = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: true),
                    status = table.Column<int>(type: "integer", nullable: false),
                    attempts = table.Column<int>(type: "integer", nullable: false),
                    max_attempts = table.Column<int>(type: "integer", nullable: false),
                    next_run_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    locked_until = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    last_error = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    completed_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_background_jobs", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "consent_records",
                schema: "mi_renta",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    purpose = table.Column<string>(type: "text", nullable: false),
                    notice_version = table.Column<string>(type: "text", nullable: false),
                    accepted_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_consent_records", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "email_tokens",
                schema: "mi_renta",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    purpose = table.Column<int>(type: "integer", nullable: false),
                    token_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    expires_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    used_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    failed_attempts = table.Column<int>(type: "integer", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_email_tokens", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "outbound_emails",
                schema: "mi_renta",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    case_id = table.Column<Guid>(type: "uuid", nullable: true),
                    to_address = table.Column<string>(type: "character varying(254)", maxLength: 254, nullable: false),
                    kind = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    subject = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    body = table.Column<string>(type: "text", nullable: true),
                    dedup_key = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                    status = table.Column<int>(type: "integer", nullable: false),
                    scheduled_for = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    sent_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    last_error = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_outbound_emails", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "refresh_tokens",
                schema: "mi_renta",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    token_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    expires_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    revoked_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    replaced_by_id = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_refresh_tokens", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "tax_rules",
                schema: "mi_renta",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    code = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    tax_year = table.Column<int>(type: "integer", nullable: false),
                    rule_set_version = table.Column<string>(type: "text", nullable: false),
                    description = table.Column<string>(type: "text", nullable: false),
                    parameters_json = table.Column<string>(type: "jsonb", nullable: false),
                    legal_reference = table.Column<string>(type: "text", nullable: false),
                    source_url = table.Column<string>(type: "text", nullable: true),
                    status = table.Column<int>(type: "integer", nullable: false),
                    approved_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    approved_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    approval_note = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_tax_rules", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "taxpayers",
                schema: "mi_renta",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    owner_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    document_type = table.Column<int>(type: "integer", nullable: false),
                    document_number = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_taxpayers", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "user_accounts",
                schema: "mi_renta",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    email = table.Column<string>(type: "character varying(254)", maxLength: 254, nullable: false),
                    email_verified_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    document_type = table.Column<int>(type: "integer", nullable: false),
                    document_number = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    password_hash = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    role = table.Column<int>(type: "integer", nullable: false),
                    failed_login_count = table.Column<int>(type: "integer", nullable: false),
                    lockout_until = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    password_changed_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    reminders_opt_out = table.Column<bool>(type: "boolean", nullable: false),
                    is_disabled = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_user_accounts", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "tax_cases",
                schema: "mi_renta",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    taxpayer_id = table.Column<Guid>(type: "uuid", nullable: false),
                    owner_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    tax_year = table.Column<int>(type: "integer", nullable: false),
                    return_type = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    status = table.Column<int>(type: "integer", nullable: false),
                    blocked_reason = table.Column<string>(type: "text", nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    last_user_activity_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false),
                    deleted_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_tax_cases", x => x.id);
                    table.ForeignKey(
                        name: "fk_tax_cases_taxpayers_taxpayer_id",
                        column: x => x.taxpayer_id,
                        principalSchema: "mi_renta",
                        principalTable: "taxpayers",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "assistant_tasks",
                schema: "mi_renta",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    case_id = table.Column<Guid>(type: "uuid", nullable: false),
                    kind = table.Column<int>(type: "integer", nullable: false),
                    code = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    prompt = table.Column<string>(type: "text", nullable: false),
                    help = table.Column<string>(type: "text", nullable: true),
                    answer_type = table.Column<int>(type: "integer", nullable: false),
                    options_json = table.Column<string>(type: "jsonb", nullable: true),
                    blocks_calculation = table.Column<bool>(type: "boolean", nullable: false),
                    state = table.Column<int>(type: "integer", nullable: false),
                    related_json = table.Column<string>(type: "jsonb", nullable: true),
                    sort_order = table.Column<int>(type: "integer", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    closed_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_assistant_tasks", x => x.id);
                    table.ForeignKey(
                        name: "fk_assistant_tasks_tax_cases_case_id",
                        column: x => x.case_id,
                        principalSchema: "mi_renta",
                        principalTable: "tax_cases",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "client_authorizations",
                schema: "mi_renta",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    case_id = table.Column<Guid>(type: "uuid", nullable: false),
                    accountant_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    granted_by_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    granted_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    revoked_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_client_authorizations", x => x.id);
                    table.ForeignKey(
                        name: "fk_client_authorizations_tax_cases_case_id",
                        column: x => x.case_id,
                        principalSchema: "mi_renta",
                        principalTable: "tax_cases",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "conversations",
                schema: "mi_renta",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    case_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_conversations", x => x.id);
                    table.ForeignKey(
                        name: "fk_conversations_tax_cases_case_id",
                        column: x => x.case_id,
                        principalSchema: "mi_renta",
                        principalTable: "tax_cases",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "documents",
                schema: "mi_renta",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    case_id = table.Column<Guid>(type: "uuid", nullable: false),
                    uploaded_by_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    original_file_name = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    content_type = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    format = table.Column<int>(type: "integer", nullable: false),
                    size_bytes = table.Column<long>(type: "bigint", nullable: false),
                    sha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    storage_key = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    kind = table.Column<int>(type: "integer", nullable: false),
                    status = table.Column<int>(type: "integer", nullable: false),
                    processing_attempts = table.Column<int>(type: "integer", nullable: false),
                    last_error = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    requested_by_task_id = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    processed_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    deleted_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_documents", x => x.id);
                    table.ForeignKey(
                        name: "fk_documents_tax_cases_case_id",
                        column: x => x.case_id,
                        principalSchema: "mi_renta",
                        principalTable: "tax_cases",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "financial_accounts",
                schema: "mi_renta",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    case_id = table.Column<Guid>(type: "uuid", nullable: false),
                    institution = table.Column<string>(type: "text", nullable: false),
                    masked_number = table.Column<string>(type: "text", nullable: true),
                    kind = table.Column<string>(type: "text", nullable: false),
                    currency = table.Column<string>(type: "text", nullable: false),
                    balance_at_year_end = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: true),
                    source_document_id = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_financial_accounts", x => x.id);
                    table.ForeignKey(
                        name: "fk_financial_accounts_tax_cases_case_id",
                        column: x => x.case_id,
                        principalSchema: "mi_renta",
                        principalTable: "tax_cases",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "financial_transactions",
                schema: "mi_renta",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    case_id = table.Column<Guid>(type: "uuid", nullable: false),
                    account_id = table.Column<Guid>(type: "uuid", nullable: true),
                    source_document_id = table.Column<Guid>(type: "uuid", nullable: false),
                    source_locator = table.Column<string>(type: "text", nullable: false),
                    date = table.Column<DateOnly>(type: "date", nullable: false),
                    amount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    currency = table.Column<string>(type: "text", nullable: false),
                    counterparty = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    description = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    bank_category = table.Column<string>(type: "text", nullable: true),
                    tax_treatment = table.Column<int>(type: "integer", nullable: false),
                    review_status = table.Column<int>(type: "integer", nullable: false),
                    dedup_key = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_financial_transactions", x => x.id);
                    table.ForeignKey(
                        name: "fk_financial_transactions_tax_cases_case_id",
                        column: x => x.case_id,
                        principalSchema: "mi_renta",
                        principalTable: "tax_cases",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "reconciliations",
                schema: "mi_renta",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    case_id = table.Column<Guid>(type: "uuid", nullable: false),
                    kind = table.Column<int>(type: "integer", nullable: false),
                    transaction_ids_json = table.Column<string>(type: "jsonb", nullable: false),
                    explanation = table.Column<string>(type: "text", nullable: false),
                    is_open = table.Column<bool>(type: "boolean", nullable: false),
                    resolution = table.Column<string>(type: "text", nullable: true),
                    resolved_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    resolved_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    dedup_key = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_reconciliations", x => x.id);
                    table.ForeignKey(
                        name: "fk_reconciliations_tax_cases_case_id",
                        column: x => x.case_id,
                        principalSchema: "mi_renta",
                        principalTable: "tax_cases",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "return_drafts",
                schema: "mi_renta",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    case_id = table.Column<Guid>(type: "uuid", nullable: false),
                    calculation_id = table.Column<Guid>(type: "uuid", nullable: false),
                    version = table.Column<int>(type: "integer", nullable: false),
                    status = table.Column<int>(type: "integer", nullable: false),
                    form_version = table.Column<string>(type: "text", nullable: false),
                    reviewed_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    reviewed_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    review_comment = table.Column<string>(type: "text", nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_return_drafts", x => x.id);
                    table.ForeignKey(
                        name: "fk_return_drafts_tax_cases_case_id",
                        column: x => x.case_id,
                        principalSchema: "mi_renta",
                        principalTable: "tax_cases",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "tax_calculations",
                schema: "mi_renta",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    case_id = table.Column<Guid>(type: "uuid", nullable: false),
                    engine_version = table.Column<string>(type: "text", nullable: false),
                    rule_set_version = table.Column<string>(type: "text", nullable: false),
                    facts_snapshot_json = table.Column<string>(type: "jsonb", nullable: false),
                    result_json = table.Column<string>(type: "jsonb", nullable: false),
                    status = table.Column<int>(type: "integer", nullable: false),
                    blocking_reasons_json = table.Column<string>(type: "jsonb", nullable: false),
                    requested_by_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_tax_calculations", x => x.id);
                    table.ForeignKey(
                        name: "fk_tax_calculations_tax_cases_case_id",
                        column: x => x.case_id,
                        principalSchema: "mi_renta",
                        principalTable: "tax_cases",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "tax_facts",
                schema: "mi_renta",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    case_id = table.Column<Guid>(type: "uuid", nullable: false),
                    code = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    amount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: true),
                    text_value = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    source = table.Column<int>(type: "integer", nullable: false),
                    evidence_ids_json = table.Column<string>(type: "jsonb", nullable: false),
                    status = table.Column<int>(type: "integer", nullable: false),
                    version = table.Column<int>(type: "integer", nullable: false),
                    superseded_by_id = table.Column<Guid>(type: "uuid", nullable: true),
                    created_by_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    note = table.Column<string>(type: "text", nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_tax_facts", x => x.id);
                    table.ForeignKey(
                        name: "fk_tax_facts_tax_cases_case_id",
                        column: x => x.case_id,
                        principalSchema: "mi_renta",
                        principalTable: "tax_cases",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "validation_issues",
                schema: "mi_renta",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    case_id = table.Column<Guid>(type: "uuid", nullable: false),
                    code = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    severity = table.Column<int>(type: "integer", nullable: false),
                    message = table.Column<string>(type: "text", nullable: false),
                    is_open = table.Column<bool>(type: "boolean", nullable: false),
                    related_json = table.Column<string>(type: "text", nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    resolved_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_validation_issues", x => x.id);
                    table.ForeignKey(
                        name: "fk_validation_issues_tax_cases_case_id",
                        column: x => x.case_id,
                        principalSchema: "mi_renta",
                        principalTable: "tax_cases",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "user_responses",
                schema: "mi_renta",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    task_id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    value_json = table.Column<string>(type: "jsonb", nullable: false),
                    idempotency_key = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_user_responses", x => x.id);
                    table.ForeignKey(
                        name: "fk_user_responses_assistant_tasks_task_id",
                        column: x => x.task_id,
                        principalSchema: "mi_renta",
                        principalTable: "assistant_tasks",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "conversation_messages",
                schema: "mi_renta",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    conversation_id = table.Column<Guid>(type: "uuid", nullable: false),
                    role = table.Column<int>(type: "integer", nullable: false),
                    content = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: false),
                    idempotency_key = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: true),
                    author_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_conversation_messages", x => x.id);
                    table.ForeignKey(
                        name: "fk_conversation_messages_conversations_conversation_id",
                        column: x => x.conversation_id,
                        principalSchema: "mi_renta",
                        principalTable: "conversations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "document_extractions",
                schema: "mi_renta",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    document_id = table.Column<Guid>(type: "uuid", nullable: false),
                    version = table.Column<int>(type: "integer", nullable: false),
                    method = table.Column<string>(type: "text", nullable: false),
                    extractor_version = table.Column<string>(type: "text", nullable: true),
                    page_count = table.Column<int>(type: "integer", nullable: true),
                    row_count = table.Column<int>(type: "integer", nullable: true),
                    proposed_kind = table.Column<int>(type: "integer", nullable: false),
                    confidence = table.Column<decimal>(type: "numeric(5,4)", precision: 5, scale: 4, nullable: false),
                    structured_json = table.Column<string>(type: "jsonb", nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_document_extractions", x => x.id);
                    table.ForeignKey(
                        name: "fk_document_extractions_documents_document_id",
                        column: x => x.document_id,
                        principalSchema: "mi_renta",
                        principalTable: "documents",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "evidence_items",
                schema: "mi_renta",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    case_id = table.Column<Guid>(type: "uuid", nullable: false),
                    document_id = table.Column<Guid>(type: "uuid", nullable: false),
                    extraction_id = table.Column<Guid>(type: "uuid", nullable: false),
                    field = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    raw_text = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    extracted_amount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: true),
                    extracted_value = table.Column<string>(type: "text", nullable: true),
                    confirmed_amount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: true),
                    locator = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    confidence = table.Column<decimal>(type: "numeric(5,4)", precision: 5, scale: 4, nullable: false),
                    status = table.Column<int>(type: "integer", nullable: false),
                    reviewed_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    reviewed_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_evidence_items", x => x.id);
                    table.ForeignKey(
                        name: "fk_evidence_items_documents_document_id",
                        column: x => x.document_id,
                        principalSchema: "mi_renta",
                        principalTable: "documents",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "draft_review_notes",
                schema: "mi_renta",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    draft_id = table.Column<Guid>(type: "uuid", nullable: false),
                    author_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    text = table.Column<string>(type: "text", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_draft_review_notes", x => x.id);
                    table.ForeignKey(
                        name: "fk_draft_review_notes_return_drafts_draft_id",
                        column: x => x.draft_id,
                        principalSchema: "mi_renta",
                        principalTable: "return_drafts",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "return_lines",
                schema: "mi_renta",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    draft_id = table.Column<Guid>(type: "uuid", nullable: false),
                    concept = table.Column<string>(type: "text", nullable: false),
                    box_number = table.Column<int>(type: "integer", nullable: true),
                    label = table.Column<string>(type: "text", nullable: false),
                    value = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    sources_json = table.Column<string>(type: "jsonb", nullable: false),
                    rule_code = table.Column<string>(type: "text", nullable: false),
                    formula = table.Column<string>(type: "text", nullable: false),
                    review_status = table.Column<string>(type: "text", nullable: false),
                    warnings_json = table.Column<string>(type: "jsonb", nullable: false),
                    sort_order = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_return_lines", x => x.id);
                    table.ForeignKey(
                        name: "fk_return_lines_return_drafts_draft_id",
                        column: x => x.draft_id,
                        principalSchema: "mi_renta",
                        principalTable: "return_drafts",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.InsertData(
                schema: "mi_renta",
                table: "tax_rules",
                columns: new[] { "id", "approval_note", "approved_at", "approved_by_user_id", "code", "description", "legal_reference", "parameters_json", "rule_set_version", "source_url", "status", "tax_year" },
                values: new object[,]
                {
                    { new Guid("0227a33d-0cf8-b70f-1a9d-a89356b510d3"), null, null, null, "exempt.labor_25", "Renta exenta del 25% de los pagos laborales, calculada después de restar ingresos no constitutivos, deducciones y demás rentas exentas; tope anual 790 UVT.", "Estatuto Tributario art. 206 num. 10 (modificado por la Ley 2277 de 2022, art. 9).", "{\"rate\":0.25,\"annualCapUvt\":790}", "AG2025-r1", null, 1, 2025 },
                    { new Guid("182020ef-6b05-9257-c0af-1b69769d2362"), null, null, null, "rounding.thousand_577", "Los valores de las declaraciones se aproximan al múltiplo de mil más cercano.", "Estatuto Tributario art. 577.", "{\"multiple\":1000}", "AG2025-r1", null, 1, 2025 },
                    { new Guid("37c7bea0-4fea-4593-ec8f-c673d85aa687"), null, null, null, "obligation.thresholds", "Obligación de declarar: patrimonio bruto > 4.500 UVT; ingresos brutos ≥ 1.400 UVT; consumos con tarjeta de crédito, compras y consumos, o consignaciones y depósitos > 1.400 UVT; o ser responsable de IVA.", "Estatuto Tributario arts. 592, 593 y 594-3; Decreto 1625 de 2016, art. 1.6.1.13.2.7 (modificado por el Decreto 2229 de 2023).", "{\"grossAssetsUvt\":4500,\"grossIncomeUvt\":1400,\"creditCardUvt\":1400,\"purchasesUvt\":1400,\"depositsUvt\":1400}", "AG2025-r1", "https://normograma.dian.gov.co/dian/compilacion/docs/estatuto_tributario_pr024.htm", 1, 2025 },
                    { new Guid("5816e93c-cbbb-2fba-094e-2408da5b5d9b"), null, null, null, "deduction.housing_interest_119", "Intereses de créditos de vivienda del contribuyente: deducibles hasta 1.200 UVT anuales.", "Estatuto Tributario art. 119.", "{\"annualCapUvt\":1200}", "AG2025-r1", null, 1, 2025 },
                    { new Guid("5a11bfbf-3cbe-c411-da37-d65686f4e3e9"), null, null, null, "scope.non_resident", "No residentes fiscales. Fuera del alcance del formulario 210 y de este MVP.", "Estatuto Tributario arts. 10 y 592.", "{}", "AG2025-r1", null, 0, 2025 },
                    { new Guid("7dcf7e9d-2d79-5c04-1a2f-2a79dd1d88a5"), null, null, null, "income.fees", "Honorarios y compensación de servicios personales (rentas de trabajo no laborales). Sin implementar en el MVP.", "Estatuto Tributario arts. 103, 206 par. 5 y 336.", "{}", "AG2025-r1", null, 0, 2025 },
                    { new Guid("8c6634b0-46f0-fd09-22c5-6b4707ae61c3"), null, null, null, "tax.rates_241", "Tabla de tarifas marginales para personas naturales residentes (cédula general).", "Estatuto Tributario art. 241 (texto vigente, Ley 2010 de 2019, art. 34).", "{\"brackets\":[{\"fromUvt\":0,\"toUvt\":1090,\"rate\":0,\"baseUvt\":0},{\"fromUvt\":1090,\"toUvt\":1700,\"rate\":0.19,\"baseUvt\":0},{\"fromUvt\":1700,\"toUvt\":4100,\"rate\":0.28,\"baseUvt\":116},{\"fromUvt\":4100,\"toUvt\":8670,\"rate\":0.33,\"baseUvt\":788},{\"fromUvt\":8670,\"toUvt\":18970,\"rate\":0.35,\"baseUvt\":2296},{\"fromUvt\":18970,\"toUvt\":31000,\"rate\":0.37,\"baseUvt\":5901},{\"fromUvt\":31000,\"toUvt\":null,\"rate\":0.39,\"baseUvt\":10352}]}", "AG2025-r1", null, 1, 2025 },
                    { new Guid("999ffd7d-7848-fef1-d823-f1ecafcc85e4"), null, null, null, "deduction.dependents_additional_336", "Deducción adicional de 72 UVT por dependiente (hasta 4) para rentas de trabajo, fuera del límite del 40%. Sin implementar: su interacción con la deducción del art. 387 debe definirla un profesional.", "Estatuto Tributario art. 336 num. 3 (adicionado por la Ley 2277 de 2022, art. 7).", "{\"perDependentUvt\":72,\"maxDependents\":4}", "AG2025-r1", null, 0, 2025 },
                    { new Guid("c95eba4c-3f26-3654-48ac-729a467da221"), null, null, null, "advance.807", "Anticipo del impuesto del año siguiente. Sin implementar: depende de los años declarados y del procedimiento elegido; debe ingresarlo un contador.", "Estatuto Tributario arts. 807 a 809.", "{}", "AG2025-r1", null, 0, 2025 },
                    { new Guid("d092dd27-1877-a99c-fe6e-6009c0968782"), null, null, null, "uvt", "Valor de la UVT para 2025: $49.799.", "Resolución DIAN 000193 del 4 de diciembre de 2024, art. 1 (compilada en el art. 1.2.2 de la Resolución DIAN 000227 de 2025).", "{\"value\":49799}", "AG2025-r1", "https://normograma.dian.gov.co/dian/compilacion/docs/resolucion_dian_0193_2024.htm", 1, 2025 },
                    { new Guid("d5895143-90ed-4991-6a89-9891252bbbbf"), null, null, null, "income.incr_mandatory_contributions", "Aportes obligatorios del trabajador a pensión y a salud: ingresos no constitutivos de renta.", "Estatuto Tributario arts. 55 y 56.", "{}", "AG2025-r1", null, 1, 2025 },
                    { new Guid("ed7f17c5-f21e-256f-e558-ccde2bd77687"), null, null, null, "deduction.dependents_387", "Deducción por dependientes: hasta el 10% del ingreso bruto por rentas de trabajo, máximo 32 UVT mensuales (384 UVT al año).", "Estatuto Tributario art. 387 (modificado por la Ley 1819 de 2016).", "{\"rate\":0.10,\"annualCapUvt\":384}", "AG2025-r1", null, 1, 2025 },
                    { new Guid("f0a0bfd9-003b-9944-0e6d-2df4c5df54d3"), null, null, null, "limit.exempt_and_deductions_336", "Las rentas exentas y deducciones de la cédula general no pueden superar el 40% de los ingresos menos ingresos no constitutivos, ni 1.340 UVT anuales.", "Estatuto Tributario art. 336 num. 3 (modificado por la Ley 2277 de 2022, art. 7).", "{\"rate\":0.40,\"annualCapUvt\":1340}", "AG2025-r1", null, 1, 2025 },
                    { new Guid("f5e8c954-65f6-6211-e2b0-dd91a313b391"), null, null, null, "deduction.prepaid_health_387", "Pagos de medicina prepagada y seguros de salud: hasta 16 UVT mensuales (192 UVT al año).", "Estatuto Tributario art. 387.", "{\"annualCapUvt\":192}", "AG2025-r1", null, 1, 2025 }
                });

            migrationBuilder.CreateIndex(
                name: "ix_assistant_tasks_case_id_code",
                schema: "mi_renta",
                table: "assistant_tasks",
                columns: new[] { "case_id", "code" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_audit_events_case_id_created_at",
                schema: "mi_renta",
                table: "audit_events",
                columns: new[] { "case_id", "created_at" });

            migrationBuilder.CreateIndex(
                name: "ix_background_jobs_dedup_key",
                schema: "mi_renta",
                table: "background_jobs",
                column: "dedup_key",
                unique: true,
                filter: "dedup_key IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_background_jobs_status_next_run_at",
                schema: "mi_renta",
                table: "background_jobs",
                columns: new[] { "status", "next_run_at" });

            migrationBuilder.CreateIndex(
                name: "ix_client_authorizations_case_id_accountant_user_id",
                schema: "mi_renta",
                table: "client_authorizations",
                columns: new[] { "case_id", "accountant_user_id" });

            migrationBuilder.CreateIndex(
                name: "ix_consent_records_user_id",
                schema: "mi_renta",
                table: "consent_records",
                column: "user_id");

            migrationBuilder.CreateIndex(
                name: "ix_conversation_messages_conversation_id_created_at",
                schema: "mi_renta",
                table: "conversation_messages",
                columns: new[] { "conversation_id", "created_at" });

            migrationBuilder.CreateIndex(
                name: "ix_conversation_messages_conversation_id_idempotency_key",
                schema: "mi_renta",
                table: "conversation_messages",
                columns: new[] { "conversation_id", "idempotency_key" },
                unique: true,
                filter: "idempotency_key IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_conversations_case_id",
                schema: "mi_renta",
                table: "conversations",
                column: "case_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_document_extractions_document_id_version",
                schema: "mi_renta",
                table: "document_extractions",
                columns: new[] { "document_id", "version" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_documents_case_id_sha256",
                schema: "mi_renta",
                table: "documents",
                columns: new[] { "case_id", "sha256" },
                unique: true,
                filter: "deleted_at IS NULL");

            migrationBuilder.CreateIndex(
                name: "ix_draft_review_notes_draft_id",
                schema: "mi_renta",
                table: "draft_review_notes",
                column: "draft_id");

            migrationBuilder.CreateIndex(
                name: "ix_email_tokens_token_hash",
                schema: "mi_renta",
                table: "email_tokens",
                column: "token_hash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_email_tokens_user_id_purpose",
                schema: "mi_renta",
                table: "email_tokens",
                columns: new[] { "user_id", "purpose" });

            migrationBuilder.CreateIndex(
                name: "ix_evidence_items_case_id_status",
                schema: "mi_renta",
                table: "evidence_items",
                columns: new[] { "case_id", "status" });

            migrationBuilder.CreateIndex(
                name: "ix_evidence_items_document_id",
                schema: "mi_renta",
                table: "evidence_items",
                column: "document_id");

            migrationBuilder.CreateIndex(
                name: "ix_financial_accounts_case_id",
                schema: "mi_renta",
                table: "financial_accounts",
                column: "case_id");

            migrationBuilder.CreateIndex(
                name: "ix_financial_transactions_case_id_date",
                schema: "mi_renta",
                table: "financial_transactions",
                columns: new[] { "case_id", "date" });

            migrationBuilder.CreateIndex(
                name: "ix_financial_transactions_case_id_dedup_key",
                schema: "mi_renta",
                table: "financial_transactions",
                columns: new[] { "case_id", "dedup_key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_outbound_emails_dedup_key",
                schema: "mi_renta",
                table: "outbound_emails",
                column: "dedup_key",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_outbound_emails_status_scheduled_for",
                schema: "mi_renta",
                table: "outbound_emails",
                columns: new[] { "status", "scheduled_for" });

            migrationBuilder.CreateIndex(
                name: "ix_outbound_emails_user_id_kind_created_at",
                schema: "mi_renta",
                table: "outbound_emails",
                columns: new[] { "user_id", "kind", "created_at" });

            migrationBuilder.CreateIndex(
                name: "ix_reconciliations_case_id_dedup_key",
                schema: "mi_renta",
                table: "reconciliations",
                columns: new[] { "case_id", "dedup_key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_refresh_tokens_token_hash",
                schema: "mi_renta",
                table: "refresh_tokens",
                column: "token_hash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_refresh_tokens_user_id",
                schema: "mi_renta",
                table: "refresh_tokens",
                column: "user_id");

            migrationBuilder.CreateIndex(
                name: "ix_return_drafts_case_id_version",
                schema: "mi_renta",
                table: "return_drafts",
                columns: new[] { "case_id", "version" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_return_lines_draft_id_sort_order",
                schema: "mi_renta",
                table: "return_lines",
                columns: new[] { "draft_id", "sort_order" });

            migrationBuilder.CreateIndex(
                name: "ix_tax_calculations_case_id_created_at",
                schema: "mi_renta",
                table: "tax_calculations",
                columns: new[] { "case_id", "created_at" });

            migrationBuilder.CreateIndex(
                name: "ix_tax_cases_owner_user_id",
                schema: "mi_renta",
                table: "tax_cases",
                column: "owner_user_id");

            migrationBuilder.CreateIndex(
                name: "ix_tax_cases_taxpayer_id_tax_year_return_type",
                schema: "mi_renta",
                table: "tax_cases",
                columns: new[] { "taxpayer_id", "tax_year", "return_type" },
                unique: true,
                filter: "deleted_at IS NULL");

            migrationBuilder.CreateIndex(
                name: "ix_tax_facts_case_id_code_version",
                schema: "mi_renta",
                table: "tax_facts",
                columns: new[] { "case_id", "code", "version" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_tax_rules_tax_year_rule_set_version_code",
                schema: "mi_renta",
                table: "tax_rules",
                columns: new[] { "tax_year", "rule_set_version", "code" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_taxpayers_owner_user_id_document_type_document_number",
                schema: "mi_renta",
                table: "taxpayers",
                columns: new[] { "owner_user_id", "document_type", "document_number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_user_accounts_document_type_document_number",
                schema: "mi_renta",
                table: "user_accounts",
                columns: new[] { "document_type", "document_number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_user_accounts_email",
                schema: "mi_renta",
                table: "user_accounts",
                column: "email",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_user_responses_task_id_idempotency_key",
                schema: "mi_renta",
                table: "user_responses",
                columns: new[] { "task_id", "idempotency_key" },
                unique: true,
                filter: "idempotency_key IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_validation_issues_case_id_code_is_open",
                schema: "mi_renta",
                table: "validation_issues",
                columns: new[] { "case_id", "code", "is_open" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "audit_events",
                schema: "mi_renta");

            migrationBuilder.DropTable(
                name: "background_jobs",
                schema: "mi_renta");

            migrationBuilder.DropTable(
                name: "client_authorizations",
                schema: "mi_renta");

            migrationBuilder.DropTable(
                name: "consent_records",
                schema: "mi_renta");

            migrationBuilder.DropTable(
                name: "conversation_messages",
                schema: "mi_renta");

            migrationBuilder.DropTable(
                name: "document_extractions",
                schema: "mi_renta");

            migrationBuilder.DropTable(
                name: "draft_review_notes",
                schema: "mi_renta");

            migrationBuilder.DropTable(
                name: "email_tokens",
                schema: "mi_renta");

            migrationBuilder.DropTable(
                name: "evidence_items",
                schema: "mi_renta");

            migrationBuilder.DropTable(
                name: "financial_accounts",
                schema: "mi_renta");

            migrationBuilder.DropTable(
                name: "financial_transactions",
                schema: "mi_renta");

            migrationBuilder.DropTable(
                name: "outbound_emails",
                schema: "mi_renta");

            migrationBuilder.DropTable(
                name: "reconciliations",
                schema: "mi_renta");

            migrationBuilder.DropTable(
                name: "refresh_tokens",
                schema: "mi_renta");

            migrationBuilder.DropTable(
                name: "return_lines",
                schema: "mi_renta");

            migrationBuilder.DropTable(
                name: "tax_calculations",
                schema: "mi_renta");

            migrationBuilder.DropTable(
                name: "tax_facts",
                schema: "mi_renta");

            migrationBuilder.DropTable(
                name: "tax_rules",
                schema: "mi_renta");

            migrationBuilder.DropTable(
                name: "user_accounts",
                schema: "mi_renta");

            migrationBuilder.DropTable(
                name: "user_responses",
                schema: "mi_renta");

            migrationBuilder.DropTable(
                name: "validation_issues",
                schema: "mi_renta");

            migrationBuilder.DropTable(
                name: "conversations",
                schema: "mi_renta");

            migrationBuilder.DropTable(
                name: "documents",
                schema: "mi_renta");

            migrationBuilder.DropTable(
                name: "return_drafts",
                schema: "mi_renta");

            migrationBuilder.DropTable(
                name: "assistant_tasks",
                schema: "mi_renta");

            migrationBuilder.DropTable(
                name: "tax_cases",
                schema: "mi_renta");

            migrationBuilder.DropTable(
                name: "taxpayers",
                schema: "mi_renta");
        }
    }
}

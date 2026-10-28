using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace DataScope.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialSchema : Migration
    {
        private static readonly string[] AppendOnlyTables = ["review_items", "review_item_users", "review_decisions", "review_events"];

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterDatabase()
                .Annotation("Npgsql:PostgresExtension:pg_trgm", ",,");

            migrationBuilder.CreateTable(
                name: "app_audit_log",
                columns: table => new
                {
                    sequence = table.Column<long>(type: "bigint", nullable: false),
                    at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    actor_sid = table.Column<string>(type: "character varying(184)", maxLength: 184, nullable: false),
                    actor_name = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    action = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    target = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    details = table.Column<string>(type: "json", nullable: false),
                    previous_hash = table.Column<string>(type: "character(64)", fixedLength: true, maxLength: 64, nullable: false),
                    hash = table.Column<string>(type: "character(64)", fixedLength: true, maxLength: 64, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_app_audit_log", x => x.sequence);
                });

            migrationBuilder.CreateTable(
                name: "compliance_standards",
                columns: table => new
                {
                    standard_id = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    enabled = table.Column<bool>(type: "boolean", nullable: false),
                    categories = table.Column<string[]>(type: "text[]", nullable: false),
                    min_confidence = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: false),
                    reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_by_sid = table.Column<string>(type: "character varying(184)", maxLength: 184, nullable: false),
                    updated_by_name = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_compliance_standards", x => x.standard_id);
                    table.CheckConstraint("ck_compliance_standards_categories", "cardinality(categories) BETWEEN 1 AND 16");
                });

            migrationBuilder.CreateTable(
                name: "custom_detection_rules",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    name = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    description = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    kind = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    pattern = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    keywords = table.Column<string[]>(type: "text[]", nullable: false),
                    case_sensitive = table.Column<bool>(type: "boolean", nullable: false),
                    near_words = table.Column<string[]>(type: "text[]", nullable: false),
                    min_matches = table.Column<int>(type: "integer", nullable: false),
                    effect = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    enabled = table.Column<bool>(type: "boolean", nullable: false),
                    reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_by_name = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_by_sid = table.Column<string>(type: "character varying(184)", maxLength: 184, nullable: false),
                    updated_by_name = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_custom_detection_rules", x => x.id);
                    table.CheckConstraint("ck_custom_detection_rules_kind", "(kind = 'Pattern' AND pattern IS NOT NULL AND cardinality(keywords) = 0) OR (kind = 'Keywords' AND pattern IS NULL AND cardinality(keywords) BETWEEN 1 AND 100)");
                    table.CheckConstraint("ck_custom_detection_rules_min_matches", "min_matches BETWEEN 1 AND 1000");
                });

            migrationBuilder.CreateTable(
                name: "directory_snapshots",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    state = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    domain = table.Column<string>(type: "character varying(253)", maxLength: 253, nullable: true),
                    domain_sid = table.Column<string>(type: "character varying(184)", maxLength: 184, nullable: true),
                    naming_context = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    server_name = table.Column<string>(type: "character varying(253)", maxLength: 253, nullable: true),
                    requested_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    requested_by_sid = table.Column<string>(type: "character varying(184)", maxLength: 184, nullable: true),
                    started_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    completed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    lease_owner = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    lease_expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    attempts = table.Column<int>(type: "integer", nullable: false),
                    users = table.Column<int>(type: "integer", nullable: false),
                    disabled_users = table.Column<int>(type: "integer", nullable: false),
                    groups = table.Column<int>(type: "integer", nullable: false),
                    computers = table.Column<int>(type: "integer", nullable: false),
                    foreign_principals = table.Column<int>(type: "integer", nullable: false),
                    memberships = table.Column<int>(type: "integer", nullable: false),
                    unresolved_members = table.Column<int>(type: "integer", nullable: false),
                    cycles = table.Column<int>(type: "integer", nullable: false),
                    truncated = table.Column<bool>(type: "boolean", nullable: false),
                    warnings = table.Column<string[]>(type: "text[]", nullable: false),
                    last_error = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_directory_snapshots", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "managed_servers",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    hostname = table.Column<string>(type: "character varying(253)", maxLength: 253, nullable: false),
                    kind = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false, defaultValue: "FileServer"),
                    state = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    last_seen_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    last_error = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    local_groups_state = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false, defaultValue: "NotRead"),
                    local_groups_read_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    scan_frequency = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false, defaultValue: "Off"),
                    scan_start_hour = table.Column<int>(type: "integer", nullable: false, defaultValue: 1),
                    scan_start_minute = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    scan_day = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true),
                    scan_stop_hour = table.Column<int>(type: "integer", nullable: true),
                    scan_every_hours = table.Column<int>(type: "integer", nullable: true),
                    last_scheduled_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    full_scan_frequency = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false, defaultValue: "Off"),
                    full_scan_day = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true),
                    last_full_scheduled_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_by_sid = table.Column<string>(type: "character varying(184)", maxLength: 184, nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_managed_servers", x => x.id);
                    table.CheckConstraint("ck_managed_servers_full_scan_day", "full_scan_frequency = 'Off' OR full_scan_day IS NOT NULL");
                    table.CheckConstraint("ck_managed_servers_scan_day", "scan_frequency <> 'Weekly' OR scan_day IS NOT NULL");
                    table.CheckConstraint("ck_managed_servers_scan_every", "scan_frequency <> 'Hourly' OR scan_every_hours IN (1, 2, 3, 4, 6, 8, 12)");
                    table.CheckConstraint("ck_managed_servers_scan_hours", "scan_start_hour BETWEEN 0 AND 23 AND (scan_stop_hour IS NULL OR (scan_stop_hour BETWEEN 0 AND 23 AND scan_stop_hour <> scan_start_hour))");
                    table.CheckConstraint("ck_managed_servers_scan_minute", "scan_start_minute BETWEEN 0 AND 59");
                });

            migrationBuilder.CreateTable(
                name: "notification_outbox",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    idempotency_key = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false),
                    kind = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    review_id = table.Column<Guid>(type: "uuid", nullable: true),
                    recipient_sid = table.Column<string>(type: "character varying(184)", maxLength: 184, nullable: false),
                    recipient_name = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    recipient_email = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    subject = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    body = table.Column<string>(type: "character varying(8000)", maxLength: 8000, nullable: false),
                    state = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    attempts = table.Column<int>(type: "integer", nullable: false),
                    next_attempt_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    last_error = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    sent_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_notification_outbox", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "posture_snapshots",
                columns: table => new
                {
                    day = table.Column<DateOnly>(type: "date", nullable: false),
                    taken_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    last_scan_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    risks_high = table.Column<int>(type: "integer", nullable: false),
                    risks_medium = table.Column<int>(type: "integer", nullable: false),
                    risks_low = table.Column<int>(type: "integer", nullable: false),
                    risks_accepted = table.Column<int>(type: "integer", nullable: false),
                    risks_snoozed = table.Column<int>(type: "integer", nullable: false),
                    risks_limited = table.Column<bool>(type: "boolean", nullable: false),
                    files = table.Column<long>(type: "bigint", nullable: false),
                    files_not_read = table.Column<long>(type: "bigint", nullable: false),
                    restricted = table.Column<long>(type: "bigint", nullable: false),
                    confidential = table.Column<long>(type: "bigint", nullable: false),
                    sensitive_open = table.Column<long>(type: "bigint", nullable: false),
                    shares_unowned = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_posture_snapshots", x => x.day);
                });

            migrationBuilder.CreateTable(
                name: "review_campaigns",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    description = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    due_on = table.Column<DateOnly>(type: "date", nullable: false),
                    state = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    scope = table.Column<string>(type: "jsonb", nullable: false),
                    skipped = table.Column<string>(type: "jsonb", nullable: false),
                    directory_snapshot_id = table.Column<Guid>(type: "uuid", nullable: true),
                    directory_synced_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    domain = table.Column<string>(type: "character varying(253)", maxLength: 253, nullable: true),
                    created_by_sid = table.Column<string>(type: "character varying(184)", maxLength: 184, nullable: false),
                    created_by_name = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    closed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    closed_by_sid = table.Column<string>(type: "character varying(184)", maxLength: 184, nullable: true),
                    closed_by_name = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_review_campaigns", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "review_exemptions",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    sid = table.Column<string>(type: "character varying(184)", maxLength: 184, nullable: false),
                    name = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    kind = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    added_by_sid = table.Column<string>(type: "character varying(184)", maxLength: 184, nullable: false),
                    added_by_name = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    added_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ended_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ended_by_sid = table.Column<string>(type: "character varying(184)", maxLength: 184, nullable: true),
                    ended_by_name = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_review_exemptions", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "risk_decisions",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    risk_id = table.Column<string>(type: "character varying(400)", maxLength: 400, nullable: false),
                    rule = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    title = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    where = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: false),
                    severity = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    kind = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    note = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    until = table.Column<DateOnly>(type: "date", nullable: false),
                    decided_by_sid = table.Column<string>(type: "character varying(184)", maxLength: 184, nullable: false),
                    decided_by_name = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    decided_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ended_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ended_by_sid = table.Column<string>(type: "character varying(184)", maxLength: 184, nullable: true),
                    ended_by_name = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_risk_decisions", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "security_descriptors",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    hash = table.Column<byte[]>(type: "bytea", maxLength: 32, nullable: false),
                    raw = table.Column<byte[]>(type: "bytea", nullable: false),
                    owner_sid = table.Column<string>(type: "character varying(184)", maxLength: 184, nullable: true),
                    control = table.Column<int>(type: "integer", nullable: false),
                    dacl_present = table.Column<bool>(type: "boolean", nullable: false),
                    is_protected = table.Column<bool>(type: "boolean", nullable: false),
                    has_explicit_entries = table.Column<bool>(type: "boolean", nullable: false),
                    is_canonical = table.Column<bool>(type: "boolean", nullable: false),
                    ace_count = table.Column<int>(type: "integer", nullable: false),
                    first_seen_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_security_descriptors", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "service_heartbeats",
                columns: table => new
                {
                    service = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    machine = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    version = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    started_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    seen_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_service_heartbeats", x => x.service);
                });

            migrationBuilder.CreateTable(
                name: "system_settings",
                columns: table => new
                {
                    key = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    value = table.Column<string>(type: "character varying(16000)", maxLength: 16000, nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_by_sid = table.Column<string>(type: "character varying(184)", maxLength: 184, nullable: false),
                    updated_by_name = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_system_settings", x => x.key);
                });

            migrationBuilder.CreateTable(
                name: "directory_memberships",
                columns: table => new
                {
                    snapshot_id = table.Column<Guid>(type: "uuid", nullable: false),
                    group_sid = table.Column<string>(type: "character varying(184)", maxLength: 184, nullable: false),
                    member_sid = table.Column<string>(type: "character varying(184)", maxLength: 184, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_directory_memberships", x => new { x.snapshot_id, x.group_sid, x.member_sid });
                    table.ForeignKey(
                        name: "fk_directory_memberships_directory_snapshots_snapshot_id",
                        column: x => x.snapshot_id,
                        principalTable: "directory_snapshots",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "directory_principals",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    snapshot_id = table.Column<Guid>(type: "uuid", nullable: false),
                    sid = table.Column<string>(type: "character varying(184)", maxLength: 184, nullable: false),
                    kind = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    name = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    display_name = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    distinguished_name = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: false),
                    enabled = table.Column<bool>(type: "boolean", nullable: false),
                    is_security_group = table.Column<bool>(type: "boolean", nullable: false),
                    scope = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true),
                    primary_group_sid = table.Column<string>(type: "character varying(184)", maxLength: 184, nullable: true),
                    user_principal_name = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: true),
                    mail = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    department = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    title = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    last_logon = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    when_changed = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    sid_history = table.Column<string[]>(type: "text[]", nullable: false),
                    members_incomplete = table.Column<bool>(type: "boolean", nullable: false),
                    direct_member_count = table.Column<int>(type: "integer", nullable: false),
                    manager_sid = table.Column<string>(type: "character varying(184)", maxLength: 184, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_directory_principals", x => x.id);
                    table.ForeignKey(
                        name: "fk_directory_principals_directory_snapshots_snapshot_id",
                        column: x => x.snapshot_id,
                        principalTable: "directory_snapshots",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "activity_baselines",
                columns: table => new
                {
                    server_id = table.Column<Guid>(type: "uuid", nullable: false),
                    person_key = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false),
                    person_name = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: true),
                    learned_on = table.Column<DateOnly>(type: "date", nullable: false),
                    active_days = table.Column<int>(type: "integer", nullable: false),
                    busy_day = table.Column<int>(type: "integer", nullable: false),
                    hours = table.Column<int>(type: "integer", nullable: false),
                    places = table.Column<List<string>>(type: "text[]", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_activity_baselines", x => new { x.server_id, x.person_key });
                    table.ForeignKey(
                        name: "fk_activity_baselines_managed_servers_server_id",
                        column: x => x.server_id,
                        principalTable: "managed_servers",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "activity_gaps",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    server_id = table.Column<Guid>(type: "uuid", nullable: false),
                    channel = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    log_generation = table.Column<int>(type: "integer", nullable: false),
                    reason = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    first_record_id = table.Column<long>(type: "bigint", nullable: false),
                    last_record_id = table.Column<long>(type: "bigint", nullable: true),
                    detected_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    log_oldest_event_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_activity_gaps", x => x.id);
                    table.ForeignKey(
                        name: "fk_activity_gaps_managed_servers_server_id",
                        column: x => x.server_id,
                        principalTable: "managed_servers",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "activity_sources",
                columns: table => new
                {
                    server_id = table.Column<Guid>(type: "uuid", nullable: false),
                    channel = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    enabled = table.Column<bool>(type: "boolean", nullable: false),
                    state = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    log_generation = table.Column<int>(type: "integer", nullable: false),
                    last_record_id = table.Column<long>(type: "bigint", nullable: true),
                    last_read_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    last_event_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    next_read_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    last_error = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    last_error_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    events_stored = table.Column<long>(type: "bigint", nullable: false),
                    events_ignored = table.Column<long>(type: "bigint", nullable: false),
                    events_unreadable = table.Column<long>(type: "bigint", nullable: false),
                    log_oldest_record_id = table.Column<long>(type: "bigint", nullable: true),
                    log_newest_record_id = table.Column<long>(type: "bigint", nullable: true),
                    log_record_count = table.Column<long>(type: "bigint", nullable: true),
                    log_oldest_event_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    log_size_bytes = table.Column<long>(type: "bigint", nullable: true),
                    log_max_size_bytes = table.Column<long>(type: "bigint", nullable: true),
                    log_full = table.Column<bool>(type: "boolean", nullable: true),
                    log_checked_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    alerts_checked_event_id = table.Column<long>(type: "bigint", nullable: false, defaultValue: 0L),
                    kinds_checked_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    lease_owner = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    lease_expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    changed_by_sid = table.Column<string>(type: "character varying(184)", maxLength: 184, nullable: true),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_activity_sources", x => new { x.server_id, x.channel });
                    table.ForeignKey(
                        name: "fk_activity_sources_managed_servers_server_id",
                        column: x => x.server_id,
                        principalTable: "managed_servers",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "scan_jobs",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    server_id = table.Column<Guid>(type: "uuid", nullable: false),
                    state = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    kind = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false, defaultValue: "Quick"),
                    policy_version = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    items_total = table.Column<long>(type: "bigint", nullable: false),
                    items_done = table.Column<long>(type: "bigint", nullable: false),
                    checkpoint = table.Column<string>(type: "jsonb", nullable: true),
                    attempts = table.Column<int>(type: "integer", nullable: false),
                    lease_owner = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    lease_expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    last_error = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    requested_state = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true),
                    requested_by_sid = table.Column<string>(type: "character varying(184)", maxLength: 184, nullable: false),
                    scheduled = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                    keep_to_window = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    started_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    finished_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_scan_jobs", x => x.id);
                    table.ForeignKey(
                        name: "fk_scan_jobs_managed_servers_server_id",
                        column: x => x.server_id,
                        principalTable: "managed_servers",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "server_local_groups",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    server_id = table.Column<Guid>(type: "uuid", nullable: false),
                    sid = table.Column<string>(type: "character varying(184)", maxLength: 184, nullable: false),
                    name = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    members_incomplete = table.Column<bool>(type: "boolean", nullable: false),
                    job_id = table.Column<Guid>(type: "uuid", nullable: false),
                    read_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_server_local_groups", x => x.id);
                    table.ForeignKey(
                        name: "fk_server_local_groups_managed_servers_server_id",
                        column: x => x.server_id,
                        principalTable: "managed_servers",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "security_aces",
                columns: table => new
                {
                    descriptor_id = table.Column<long>(type: "bigint", nullable: false),
                    ordinal = table.Column<int>(type: "integer", nullable: false),
                    kind = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    raw_type = table.Column<int>(type: "integer", nullable: false),
                    flags = table.Column<int>(type: "integer", nullable: false),
                    mask = table.Column<long>(type: "bigint", nullable: false),
                    sid = table.Column<string>(type: "character varying(184)", maxLength: 184, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_security_aces", x => new { x.descriptor_id, x.ordinal });
                    table.ForeignKey(
                        name: "fk_security_aces_security_descriptors_descriptor_id",
                        column: x => x.descriptor_id,
                        principalTable: "security_descriptors",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "shares",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    server_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    name_key = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    unc_path = table.Column<string>(type: "character varying(400)", maxLength: 400, nullable: false),
                    remark = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    kind = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    included = table.Column<bool>(type: "boolean", nullable: false),
                    state = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    last_scan_job_id = table.Column<Guid>(type: "uuid", nullable: true),
                    last_scanned_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    folder_count = table.Column<long>(type: "bigint", nullable: false),
                    file_count = table.Column<long>(type: "bigint", nullable: false),
                    total_bytes = table.Column<long>(type: "bigint", nullable: false),
                    gap_count = table.Column<int>(type: "integer", nullable: false),
                    security_id = table.Column<long>(type: "bigint", nullable: true),
                    security_state = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false, defaultValue: "NotRead"),
                    security_read_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    local_path = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: true),
                    local_path_source = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true),
                    local_path_set_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    exclusion_reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    excluded_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    excluded_by_sid = table.Column<string>(type: "character varying(184)", maxLength: 184, nullable: true),
                    excluded_by_name = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_shares", x => x.id);
                    table.ForeignKey(
                        name: "fk_shares_managed_servers_server_id",
                        column: x => x.server_id,
                        principalTable: "managed_servers",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_shares_security_descriptors_security_id",
                        column: x => x.security_id,
                        principalTable: "security_descriptors",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "activity_alerts",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    rule = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    severity = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    state = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    server_id = table.Column<Guid>(type: "uuid", nullable: false),
                    principal_sid = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: true),
                    principal_name = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: true),
                    first_event_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    last_event_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    event_count = table.Column<int>(type: "integer", nullable: false),
                    first_event_id = table.Column<long>(type: "bigint", nullable: true),
                    last_event_id = table.Column<long>(type: "bigint", nullable: true),
                    gap_id = table.Column<long>(type: "bigint", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    acknowledged_by_sid = table.Column<string>(type: "character varying(184)", maxLength: 184, nullable: true),
                    acknowledged_by_name = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    acknowledged_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    note = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    usual = table.Column<int>(type: "integer", nullable: true),
                    detail = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_activity_alerts", x => x.id);
                    table.ForeignKey(
                        name: "fk_activity_alerts_activity_gaps_gap_id",
                        column: x => x.gap_id,
                        principalTable: "activity_gaps",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "fk_activity_alerts_managed_servers_server_id",
                        column: x => x.server_id,
                        principalTable: "managed_servers",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "server_local_group_members",
                columns: table => new
                {
                    local_group_id = table.Column<long>(type: "bigint", nullable: false),
                    member_sid = table.Column<string>(type: "character varying(184)", maxLength: 184, nullable: false),
                    kind = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_server_local_group_members", x => new { x.local_group_id, x.member_sid });
                    table.ForeignKey(
                        name: "fk_server_local_group_members_server_local_groups_local_group_",
                        column: x => x.local_group_id,
                        principalTable: "server_local_groups",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "file_audit_events",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    server_id = table.Column<Guid>(type: "uuid", nullable: false),
                    channel = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    record_id = table.Column<long>(type: "bigint", nullable: false),
                    log_generation = table.Column<int>(type: "integer", nullable: false),
                    event_id = table.Column<int>(type: "integer", nullable: false),
                    provider = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    event_time = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ingested_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    principal_sid = table.Column<string>(type: "character varying(184)", maxLength: 184, nullable: true),
                    principal_name = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    logon_id = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    process_id = table.Column<long>(type: "bigint", nullable: true),
                    process_name = table.Column<string>(type: "character varying(520)", maxLength: 520, nullable: true),
                    handle_id = table.Column<long>(type: "bigint", nullable: true),
                    client_address = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    object_path = table.Column<string>(type: "text", nullable: true),
                    event_share_name = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: true),
                    event_share_local_path = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: true),
                    share_id = table.Column<Guid>(type: "uuid", nullable: true),
                    relative_path = table.Column<string>(type: "text", nullable: true),
                    object_kind = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    path_source = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    correlated_record_id = table.Column<long>(type: "bigint", nullable: true),
                    operation = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    certainty = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    outcome = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: false),
                    access_mask = table.Column<long>(type: "bigint", nullable: true),
                    old_security = table.Column<string>(type: "character varying(8192)", maxLength: 8192, nullable: true),
                    new_security = table.Column<string>(type: "character varying(8192)", maxLength: 8192, nullable: true),
                    parse_error = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    raw_xml = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_file_audit_events", x => x.id);
                    table.ForeignKey(
                        name: "fk_file_audit_events_managed_servers_server_id",
                        column: x => x.server_id,
                        principalTable: "managed_servers",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_file_audit_events_shares_share_id",
                        column: x => x.share_id,
                        principalTable: "shares",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "folders",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    share_id = table.Column<Guid>(type: "uuid", nullable: false),
                    parent_id = table.Column<Guid>(type: "uuid", nullable: true),
                    path = table.Column<string>(type: "text", nullable: false),
                    path_hash = table.Column<byte[]>(type: "bytea", maxLength: 32, nullable: false),
                    depth = table.Column<int>(type: "integer", nullable: false),
                    modified_at_source = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    attributes = table.Column<int>(type: "integer", nullable: false),
                    first_seen_job_id = table.Column<Guid>(type: "uuid", nullable: false),
                    last_seen_job_id = table.Column<Guid>(type: "uuid", nullable: false),
                    last_walked_job_id = table.Column<Guid>(type: "uuid", nullable: true),
                    removed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    security_id = table.Column<long>(type: "bigint", nullable: true),
                    security_state = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false, defaultValue: "NotRead"),
                    security_read_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_folders", x => x.id);
                    table.ForeignKey(
                        name: "fk_folders_folders_parent_id",
                        column: x => x.parent_id,
                        principalTable: "folders",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_folders_security_descriptors_security_id",
                        column: x => x.security_id,
                        principalTable: "security_descriptors",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_folders_shares_share_id",
                        column: x => x.share_id,
                        principalTable: "shares",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "scan_exclusions",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    kind = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    effect = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    share_id = table.Column<Guid>(type: "uuid", nullable: true),
                    path = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: true),
                    extension = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    enabled = table.Column<bool>(type: "boolean", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_by_sid = table.Column<string>(type: "character varying(184)", maxLength: 184, nullable: false),
                    created_by_name = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_by_sid = table.Column<string>(type: "character varying(184)", maxLength: 184, nullable: false),
                    updated_by_name = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_scan_exclusions", x => x.id);
                    table.CheckConstraint("ck_scan_exclusions_target", "(kind = 'Folder' AND share_id IS NOT NULL AND path IS NOT NULL AND extension IS NULL) OR (kind = 'FileType' AND extension IS NOT NULL AND path IS NULL AND effect = 'ListOnly')");
                    table.ForeignKey(
                        name: "fk_scan_exclusions_shares_share_id",
                        column: x => x.share_id,
                        principalTable: "shares",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "scan_gaps",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    job_id = table.Column<Guid>(type: "uuid", nullable: false),
                    share_id = table.Column<Guid>(type: "uuid", nullable: true),
                    path = table.Column<string>(type: "text", nullable: false),
                    kind = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    message = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_scan_gaps", x => x.id);
                    table.ForeignKey(
                        name: "fk_scan_gaps_scan_jobs_job_id",
                        column: x => x.job_id,
                        principalTable: "scan_jobs",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_scan_gaps_shares_share_id",
                        column: x => x.share_id,
                        principalTable: "shares",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "share_scan_history",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    share_id = table.Column<Guid>(type: "uuid", nullable: false),
                    job_id = table.Column<Guid>(type: "uuid", nullable: false),
                    completed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    state = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    files = table.Column<long>(type: "bigint", nullable: false),
                    bytes = table.Column<long>(type: "bigint", nullable: false),
                    restricted_files = table.Column<long>(type: "bigint", nullable: false),
                    confidential_files = table.Column<long>(type: "bigint", nullable: false),
                    files_not_read = table.Column<long>(type: "bigint", nullable: false),
                    gaps = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_share_scan_history", x => x.id);
                    table.ForeignKey(
                        name: "fk_share_scan_history_scan_jobs_job_id",
                        column: x => x.job_id,
                        principalTable: "scan_jobs",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_share_scan_history_shares_share_id",
                        column: x => x.share_id,
                        principalTable: "shares",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "access_reviews",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    campaign_id = table.Column<Guid>(type: "uuid", nullable: false),
                    folder_id = table.Column<Guid>(type: "uuid", nullable: false),
                    share_id = table.Column<Guid>(type: "uuid", nullable: false),
                    server_id = table.Column<Guid>(type: "uuid", nullable: false),
                    resource = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: false),
                    owner_sid = table.Column<string>(type: "character varying(184)", maxLength: 184, nullable: false),
                    owner_name = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    owner_email = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    backup_sid = table.Column<string>(type: "character varying(184)", maxLength: 184, nullable: true),
                    backup_name = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    backup_email = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    delegate_sid = table.Column<string>(type: "character varying(184)", maxLength: 184, nullable: true),
                    delegate_name = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    delegate_email = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    state = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    share_state = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    folder_state = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    share_security_id = table.Column<long>(type: "bigint", nullable: true),
                    folder_security_id = table.Column<long>(type: "bigint", nullable: true),
                    permissions_read_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    notes = table.Column<string[]>(type: "text[]", nullable: false),
                    item_count = table.Column<int>(type: "integer", nullable: false),
                    decisions_required = table.Column<int>(type: "integer", nullable: false),
                    evidence_hash = table.Column<string>(type: "character(64)", fixedLength: true, maxLength: 64, nullable: false),
                    frozen_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    escalated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    submitted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    submitted_by_sid = table.Column<string>(type: "character varying(184)", maxLength: 184, nullable: true),
                    submitted_by_name = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_access_reviews", x => x.id);
                    table.ForeignKey(
                        name: "fk_access_reviews_folders_folder_id",
                        column: x => x.folder_id,
                        principalTable: "folders",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_access_reviews_review_campaigns_campaign_id",
                        column: x => x.campaign_id,
                        principalTable: "review_campaigns",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "files",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    share_id = table.Column<Guid>(type: "uuid", nullable: false),
                    folder_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    name_key = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    extension = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    size = table.Column<long>(type: "bigint", nullable: false),
                    created_at_source = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    modified_at_source = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    attributes = table.Column<int>(type: "integer", nullable: false),
                    first_seen_job_id = table.Column<Guid>(type: "uuid", nullable: false),
                    last_seen_job_id = table.Column<Guid>(type: "uuid", nullable: false),
                    removed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    sensitivity = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false, defaultValue: "Unknown"),
                    override_sensitivity = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true),
                    content_state = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false, defaultValue: "NotRead"),
                    content_note = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    classified_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    classified_job_id = table.Column<Guid>(type: "uuid", nullable: true),
                    classified_policy = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    classified_size = table.Column<long>(type: "bigint", nullable: true),
                    classified_modified_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_files", x => x.id);
                    table.ForeignKey(
                        name: "fk_files_folders_folder_id",
                        column: x => x.folder_id,
                        principalTable: "folders",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_files_shares_share_id",
                        column: x => x.share_id,
                        principalTable: "shares",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "folder_security_changes",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    folder_id = table.Column<Guid>(type: "uuid", nullable: false),
                    job_id = table.Column<Guid>(type: "uuid", nullable: false),
                    from_descriptor_id = table.Column<long>(type: "bigint", nullable: true),
                    to_descriptor_id = table.Column<long>(type: "bigint", nullable: true),
                    at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_folder_security_changes", x => x.id);
                    table.ForeignKey(
                        name: "fk_folder_security_changes_folders_folder_id",
                        column: x => x.folder_id,
                        principalTable: "folders",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_folder_security_changes_scan_jobs_job_id",
                        column: x => x.job_id,
                        principalTable: "scan_jobs",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_folder_security_changes_security_descriptors_from_descripto",
                        column: x => x.from_descriptor_id,
                        principalTable: "security_descriptors",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_folder_security_changes_security_descriptors_to_descriptor_",
                        column: x => x.to_descriptor_id,
                        principalTable: "security_descriptors",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "owner_assignments",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    folder_id = table.Column<Guid>(type: "uuid", nullable: false),
                    share_id = table.Column<Guid>(type: "uuid", nullable: false),
                    owner_sid = table.Column<string>(type: "character varying(184)", maxLength: 184, nullable: false),
                    owner_name = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    owner_email = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    backup_sid = table.Column<string>(type: "character varying(184)", maxLength: 184, nullable: true),
                    backup_name = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    backup_email = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    department = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    purpose = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    review_frequency = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    retention_category = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    notes = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    assigned_by_sid = table.Column<string>(type: "character varying(184)", maxLength: 184, nullable: false),
                    assigned_by_name = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    assigned_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ended_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ended_by_sid = table.Column<string>(type: "character varying(184)", maxLength: 184, nullable: true),
                    end_reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_owner_assignments", x => x.id);
                    table.ForeignKey(
                        name: "fk_owner_assignments_folders_folder_id",
                        column: x => x.folder_id,
                        principalTable: "folders",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_owner_assignments_shares_share_id",
                        column: x => x.share_id,
                        principalTable: "shares",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "scan_frontier",
                columns: table => new
                {
                    seq = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    job_id = table.Column<Guid>(type: "uuid", nullable: false),
                    folder_id = table.Column<Guid>(type: "uuid", nullable: false),
                    share_id = table.Column<Guid>(type: "uuid", nullable: false),
                    depth = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_scan_frontier", x => x.seq);
                    table.ForeignKey(
                        name: "fk_scan_frontier_folders_folder_id",
                        column: x => x.folder_id,
                        principalTable: "folders",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_scan_frontier_scan_jobs_job_id",
                        column: x => x.job_id,
                        principalTable: "scan_jobs",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "review_events",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    review_id = table.Column<Guid>(type: "uuid", nullable: false),
                    kind = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    actor_sid = table.Column<string>(type: "character varying(184)", maxLength: 184, nullable: false),
                    actor_name = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    detail = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_review_events", x => x.id);
                    table.ForeignKey(
                        name: "fk_review_events_access_reviews_review_id",
                        column: x => x.review_id,
                        principalTable: "access_reviews",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "review_items",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    review_id = table.Column<Guid>(type: "uuid", nullable: false),
                    ordinal = table.Column<int>(type: "integer", nullable: false),
                    trustee_sid = table.Column<string>(type: "character varying(184)", maxLength: 184, nullable: false),
                    trustee_name = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    trustee_kind = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    effect = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    rights = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    applies_to = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    inherited = table.Column<bool>(type: "boolean", nullable: false),
                    broad = table.Column<bool>(type: "boolean", nullable: false),
                    administrative = table.Column<bool>(type: "boolean", nullable: false),
                    requires_decision = table.Column<bool>(type: "boolean", nullable: false),
                    users_certain = table.Column<int>(type: "integer", nullable: false),
                    users_possible = table.Column<int>(type: "integer", nullable: false),
                    users_disabled = table.Column<int>(type: "integer", nullable: false),
                    users_with_other_access = table.Column<int>(type: "integer", nullable: false),
                    users_truncated = table.Column<bool>(type: "boolean", nullable: false),
                    flags = table.Column<string[]>(type: "text[]", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_review_items", x => x.id);
                    table.ForeignKey(
                        name: "fk_review_items_access_reviews_review_id",
                        column: x => x.review_id,
                        principalTable: "access_reviews",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "classification_findings",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    file_id = table.Column<Guid>(type: "uuid", nullable: false),
                    job_id = table.Column<Guid>(type: "uuid", nullable: false),
                    policy_version = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    rule_id = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    category = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    confidence = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: false),
                    match_count = table.Column<int>(type: "integer", nullable: false),
                    evidence = table.Column<string[]>(type: "text[]", nullable: false),
                    discovered_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_classification_findings", x => x.id);
                    table.ForeignKey(
                        name: "fk_classification_findings_files_file_id",
                        column: x => x.file_id,
                        principalTable: "files",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_classification_findings_scan_jobs_job_id",
                        column: x => x.job_id,
                        principalTable: "scan_jobs",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "classification_overrides",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    file_id = table.Column<Guid>(type: "uuid", nullable: false),
                    sensitivity = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true),
                    policy_sensitivity = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    reviewer_sid = table.Column<string>(type: "character varying(184)", maxLength: 184, nullable: false),
                    reviewer_name = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_classification_overrides", x => x.id);
                    table.ForeignKey(
                        name: "fk_classification_overrides_files_file_id",
                        column: x => x.file_id,
                        principalTable: "files",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "review_decisions",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    review_id = table.Column<Guid>(type: "uuid", nullable: false),
                    item_id = table.Column<long>(type: "bigint", nullable: false),
                    decision = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    justification = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    exception_expires_on = table.Column<DateOnly>(type: "date", nullable: true),
                    decided_by_sid = table.Column<string>(type: "character varying(184)", maxLength: 184, nullable: false),
                    decided_by_name = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    decided_as = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    decided_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_review_decisions", x => x.id);
                    table.ForeignKey(
                        name: "fk_review_decisions_access_reviews_review_id",
                        column: x => x.review_id,
                        principalTable: "access_reviews",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_review_decisions_review_items_item_id",
                        column: x => x.item_id,
                        principalTable: "review_items",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "review_item_users",
                columns: table => new
                {
                    item_id = table.Column<long>(type: "bigint", nullable: false),
                    ordinal = table.Column<int>(type: "integer", nullable: false),
                    sid = table.Column<string>(type: "character varying(184)", maxLength: 184, nullable: false),
                    name = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    enabled = table.Column<bool>(type: "boolean", nullable: false),
                    department = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    title = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    certain = table.Column<bool>(type: "boolean", nullable: false),
                    chain = table.Column<string[]>(type: "text[]", nullable: false),
                    rights = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    up_to = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    certainty = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    also_via = table.Column<string>(type: "character varying(800)", maxLength: 800, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_review_item_users", x => new { x.item_id, x.ordinal });
                    table.ForeignKey(
                        name: "fk_review_item_users_review_items_item_id",
                        column: x => x.item_id,
                        principalTable: "review_items",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_access_reviews_backup_sid",
                table: "access_reviews",
                column: "backup_sid");

            migrationBuilder.CreateIndex(
                name: "ix_access_reviews_campaign_id_folder_id",
                table: "access_reviews",
                columns: new[] { "campaign_id", "folder_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_access_reviews_delegate_sid",
                table: "access_reviews",
                column: "delegate_sid");

            migrationBuilder.CreateIndex(
                name: "ix_access_reviews_folder_id",
                table: "access_reviews",
                column: "folder_id");

            migrationBuilder.CreateIndex(
                name: "ix_access_reviews_owner_sid",
                table: "access_reviews",
                column: "owner_sid");

            migrationBuilder.CreateIndex(
                name: "ix_activity_alerts_gap_id",
                table: "activity_alerts",
                column: "gap_id");

            migrationBuilder.CreateIndex(
                name: "ix_activity_alerts_rule_server_id_principal_sid_last_event_at",
                table: "activity_alerts",
                columns: new[] { "rule", "server_id", "principal_sid", "last_event_at" });

            migrationBuilder.CreateIndex(
                name: "ix_activity_alerts_server_id",
                table: "activity_alerts",
                column: "server_id");

            migrationBuilder.CreateIndex(
                name: "ix_activity_alerts_state_last_event_at",
                table: "activity_alerts",
                columns: new[] { "state", "last_event_at" });

            migrationBuilder.CreateIndex(
                name: "ix_activity_gaps_server_id_detected_at",
                table: "activity_gaps",
                columns: new[] { "server_id", "detected_at" });

            migrationBuilder.CreateIndex(
                name: "ix_activity_sources_enabled_next_read_at",
                table: "activity_sources",
                columns: new[] { "enabled", "next_read_at" });

            migrationBuilder.CreateIndex(
                name: "ix_app_audit_log_at",
                table: "app_audit_log",
                column: "at");

            migrationBuilder.CreateIndex(
                name: "ix_classification_findings_category_confidence",
                table: "classification_findings",
                columns: new[] { "category", "confidence" });

            migrationBuilder.CreateIndex(
                name: "ix_classification_findings_file_id",
                table: "classification_findings",
                column: "file_id");

            migrationBuilder.CreateIndex(
                name: "ix_classification_findings_job_id",
                table: "classification_findings",
                column: "job_id");

            migrationBuilder.CreateIndex(
                name: "ix_classification_overrides_file_id_id",
                table: "classification_overrides",
                columns: new[] { "file_id", "id" });

            migrationBuilder.CreateIndex(
                name: "ix_custom_detection_rules_name",
                table: "custom_detection_rules",
                column: "name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_directory_memberships_snapshot_id_member_sid",
                table: "directory_memberships",
                columns: new[] { "snapshot_id", "member_sid" });

            migrationBuilder.CreateIndex(
                name: "ix_directory_principals_snapshot_id_kind",
                table: "directory_principals",
                columns: new[] { "snapshot_id", "kind" });

            migrationBuilder.CreateIndex(
                name: "ix_directory_principals_snapshot_id_sid",
                table: "directory_principals",
                columns: new[] { "snapshot_id", "sid" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_directory_snapshots_state_requested_at",
                table: "directory_snapshots",
                columns: new[] { "state", "requested_at" });

            migrationBuilder.CreateIndex(
                name: "ix_file_audit_events_event_time_id",
                table: "file_audit_events",
                columns: new[] { "event_time", "id" });

            migrationBuilder.CreateIndex(
                name: "ix_file_audit_events_principal_sid_event_time",
                table: "file_audit_events",
                columns: new[] { "principal_sid", "event_time" });

            migrationBuilder.CreateIndex(
                name: "ix_file_audit_events_server_id_channel_record_id_log_generation",
                table: "file_audit_events",
                columns: new[] { "server_id", "channel", "record_id", "log_generation" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_file_audit_events_server_id_event_share_name_record_id",
                table: "file_audit_events",
                columns: new[] { "server_id", "event_share_name", "record_id" },
                filter: "event_share_local_path IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_file_audit_events_server_id_handle_id_record_id",
                table: "file_audit_events",
                columns: new[] { "server_id", "handle_id", "record_id" },
                filter: "handle_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_file_audit_events_share_id_event_time",
                table: "file_audit_events",
                columns: new[] { "share_id", "event_time" });

            migrationBuilder.CreateIndex(
                name: "ix_file_audit_events_unmatched",
                table: "file_audit_events",
                column: "server_id",
                filter: "share_id IS NULL AND object_path IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_files_current_name_trgm",
                table: "files",
                column: "name",
                filter: "removed_at IS NULL")
                .Annotation("Npgsql:IndexMethod", "gin")
                .Annotation("Npgsql:IndexOperators", new[] { "gin_trgm_ops" });

            migrationBuilder.CreateIndex(
                name: "ix_files_current_size_name_key",
                table: "files",
                columns: new[] { "size", "name_key" },
                filter: "removed_at IS NULL");

            migrationBuilder.CreateIndex(
                name: "ix_files_folder_id_name_key",
                table: "files",
                columns: new[] { "folder_id", "name_key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_files_removed_at",
                table: "files",
                column: "removed_at",
                filter: "removed_at IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_files_share_id_extension",
                table: "files",
                columns: new[] { "share_id", "extension" });

            migrationBuilder.CreateIndex(
                name: "ix_files_share_id_sensitivity",
                table: "files",
                columns: new[] { "share_id", "sensitivity" });

            migrationBuilder.CreateIndex(
                name: "ix_folder_security_changes_folder_id_at",
                table: "folder_security_changes",
                columns: new[] { "folder_id", "at" });

            migrationBuilder.CreateIndex(
                name: "ix_folder_security_changes_from_descriptor_id",
                table: "folder_security_changes",
                column: "from_descriptor_id");

            migrationBuilder.CreateIndex(
                name: "ix_folder_security_changes_job_id",
                table: "folder_security_changes",
                column: "job_id");

            migrationBuilder.CreateIndex(
                name: "ix_folder_security_changes_to_descriptor_id",
                table: "folder_security_changes",
                column: "to_descriptor_id");

            migrationBuilder.CreateIndex(
                name: "ix_folders_current_path_trgm",
                table: "folders",
                column: "path",
                filter: "removed_at IS NULL")
                .Annotation("Npgsql:IndexMethod", "gin")
                .Annotation("Npgsql:IndexOperators", new[] { "gin_trgm_ops" });

            migrationBuilder.CreateIndex(
                name: "ix_folders_last_walked_job_id",
                table: "folders",
                column: "last_walked_job_id");

            migrationBuilder.CreateIndex(
                name: "ix_folders_parent_id",
                table: "folders",
                column: "parent_id");

            migrationBuilder.CreateIndex(
                name: "ix_folders_removed_at",
                table: "folders",
                column: "removed_at",
                filter: "removed_at IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_folders_security_id",
                table: "folders",
                column: "security_id");

            migrationBuilder.CreateIndex(
                name: "ix_folders_share_id_path_hash",
                table: "folders",
                columns: new[] { "share_id", "path_hash" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_managed_servers_hostname",
                table: "managed_servers",
                column: "hostname",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_notification_outbox_idempotency_key",
                table: "notification_outbox",
                column: "idempotency_key",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_notification_outbox_review_id",
                table: "notification_outbox",
                column: "review_id");

            migrationBuilder.CreateIndex(
                name: "ix_notification_outbox_state_next_attempt_at",
                table: "notification_outbox",
                columns: new[] { "state", "next_attempt_at" });

            migrationBuilder.CreateIndex(
                name: "ix_owner_assignments_backup_sid",
                table: "owner_assignments",
                column: "backup_sid");

            migrationBuilder.CreateIndex(
                name: "ix_owner_assignments_folder_id",
                table: "owner_assignments",
                column: "folder_id",
                unique: true,
                filter: "ended_at IS NULL");

            migrationBuilder.CreateIndex(
                name: "ix_owner_assignments_owner_sid",
                table: "owner_assignments",
                column: "owner_sid");

            migrationBuilder.CreateIndex(
                name: "ix_owner_assignments_share_id",
                table: "owner_assignments",
                column: "share_id");

            migrationBuilder.CreateIndex(
                name: "ix_review_campaigns_created_at",
                table: "review_campaigns",
                column: "created_at");

            migrationBuilder.CreateIndex(
                name: "ix_review_decisions_item_id_id",
                table: "review_decisions",
                columns: new[] { "item_id", "id" });

            migrationBuilder.CreateIndex(
                name: "ix_review_decisions_review_id",
                table: "review_decisions",
                column: "review_id");

            migrationBuilder.CreateIndex(
                name: "ix_review_events_review_id_at",
                table: "review_events",
                columns: new[] { "review_id", "at" });

            migrationBuilder.CreateIndex(
                name: "ix_review_exemptions_sid",
                table: "review_exemptions",
                column: "sid",
                unique: true,
                filter: "ended_at IS NULL");

            migrationBuilder.CreateIndex(
                name: "ix_review_items_review_id_ordinal",
                table: "review_items",
                columns: new[] { "review_id", "ordinal" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_risk_decisions_risk_id",
                table: "risk_decisions",
                column: "risk_id",
                unique: true,
                filter: "ended_at IS NULL");

            migrationBuilder.CreateIndex(
                name: "ix_scan_exclusions_share_id",
                table: "scan_exclusions",
                column: "share_id");

            migrationBuilder.CreateIndex(
                name: "ix_scan_frontier_folder_id",
                table: "scan_frontier",
                column: "folder_id");

            migrationBuilder.CreateIndex(
                name: "ix_scan_frontier_job_id_folder_id",
                table: "scan_frontier",
                columns: new[] { "job_id", "folder_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_scan_frontier_job_id_seq",
                table: "scan_frontier",
                columns: new[] { "job_id", "seq" });

            migrationBuilder.CreateIndex(
                name: "ix_scan_gaps_job_id",
                table: "scan_gaps",
                column: "job_id");

            migrationBuilder.CreateIndex(
                name: "ix_scan_gaps_share_id_job_id",
                table: "scan_gaps",
                columns: new[] { "share_id", "job_id" });

            migrationBuilder.CreateIndex(
                name: "ix_scan_jobs_server_id",
                table: "scan_jobs",
                column: "server_id");

            migrationBuilder.CreateIndex(
                name: "ix_scan_jobs_state_created_at",
                table: "scan_jobs",
                columns: new[] { "state", "created_at" });

            migrationBuilder.CreateIndex(
                name: "ix_security_aces_sid",
                table: "security_aces",
                column: "sid");

            migrationBuilder.CreateIndex(
                name: "ix_security_descriptors_hash",
                table: "security_descriptors",
                column: "hash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_server_local_groups_server_id_sid",
                table: "server_local_groups",
                columns: new[] { "server_id", "sid" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_share_scan_history_completed_at",
                table: "share_scan_history",
                column: "completed_at");

            migrationBuilder.CreateIndex(
                name: "ix_share_scan_history_job_id",
                table: "share_scan_history",
                column: "job_id");

            migrationBuilder.CreateIndex(
                name: "ix_share_scan_history_share_id_job_id",
                table: "share_scan_history",
                columns: new[] { "share_id", "job_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_shares_security_id",
                table: "shares",
                column: "security_id");

            migrationBuilder.CreateIndex(
                name: "ix_shares_server_id_name_key",
                table: "shares",
                columns: new[] { "server_id", "name_key" },
                unique: true);

            // The admin audit log is append-only: reject UPDATE, DELETE and TRUNCATE at the database level too.
            migrationBuilder.Sql("""
                CREATE FUNCTION app_audit_log_append_only() RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN
                    RAISE EXCEPTION 'app_audit_log is append-only';
                END $$;
                CREATE TRIGGER app_audit_log_no_update_delete
                    BEFORE UPDATE OR DELETE ON app_audit_log
                    FOR EACH ROW EXECUTE FUNCTION app_audit_log_append_only();
                CREATE TRIGGER app_audit_log_no_truncate
                    BEFORE TRUNCATE ON app_audit_log
                    FOR EACH STATEMENT EXECUTE FUNCTION app_audit_log_append_only();
                """);

            // Frozen review evidence and decision trails are append-only, like the audit log.
            migrationBuilder.Sql("""
                CREATE FUNCTION datascope_append_only() RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN
                    RAISE EXCEPTION '% is append-only', TG_TABLE_NAME;
                END $$;
                """);
            foreach (var table in AppendOnlyTables)
            {
                migrationBuilder.Sql($"""
                    CREATE TRIGGER {table}_no_update_delete BEFORE UPDATE OR DELETE ON {table}
                        FOR EACH ROW EXECUTE FUNCTION datascope_append_only();
                    CREATE TRIGGER {table}_no_truncate BEFORE TRUNCATE ON {table}
                        FOR EACH STATEMENT EXECUTE FUNCTION datascope_append_only();
                    """);
            }

            // Collected events are evidence: only which share and file they belong to may be filled in later.
            migrationBuilder.Sql("""
                CREATE FUNCTION datascope_file_audit_events_evidence() RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN
                    IF (to_jsonb(NEW) - 'share_id' - 'relative_path' - 'object_kind')
                        IS DISTINCT FROM (to_jsonb(OLD) - 'share_id' - 'relative_path' - 'object_kind') THEN
                        RAISE EXCEPTION 'file_audit_events keeps events exactly as they were collected';
                    END IF;
                    RETURN NEW;
                END $$;
                CREATE TRIGGER file_audit_events_evidence BEFORE UPDATE ON file_audit_events
                    FOR EACH ROW EXECUTE FUNCTION datascope_file_audit_events_evidence();
                """);

            // Retention for collected events: the services' login cannot delete evidence, so the purge is a function
            // owned by the schema owner that only removes what is older than the cutoff, never anything newer than 30
            // days, whoever calls it. Execution is granted to the services' login by DatabaseRoles.
            migrationBuilder.Sql("""
                CREATE FUNCTION datascope_purge_activity(cutoff timestamp with time zone, batch integer)
                RETURNS TABLE(events bigint, gaps bigint)
                LANGUAGE plpgsql SECURITY DEFINER SET search_path = public, pg_temp AS $$
                DECLARE removed_events bigint; removed_gaps bigint;
                BEGIN
                    IF cutoff IS NULL OR cutoff > now() - interval '30 days' THEN
                        RAISE EXCEPTION 'file activity is kept for at least 30 days';
                    END IF;
                    IF batch IS NULL OR batch < 1 OR batch > 50000 THEN
                        RAISE EXCEPTION 'batch must be from 1 to 50000';
                    END IF;
                    DELETE FROM file_audit_events WHERE id IN (
                        SELECT id FROM file_audit_events WHERE event_time < cutoff ORDER BY event_time, id LIMIT batch);
                    GET DIAGNOSTICS removed_events = ROW_COUNT;
                    DELETE FROM activity_gaps WHERE id IN (
                        SELECT id FROM activity_gaps WHERE detected_at < cutoff ORDER BY id LIMIT batch);
                    GET DIAGNOSTICS removed_gaps = ROW_COUNT;
                    RETURN QUERY SELECT removed_events, removed_gaps;
                END $$;
                REVOKE ALL ON FUNCTION datascope_purge_activity(timestamp with time zone, integer) FROM PUBLIC;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP FUNCTION IF EXISTS datascope_purge_activity(timestamp with time zone, integer);");
            migrationBuilder.Sql("DROP TRIGGER IF EXISTS file_audit_events_evidence ON file_audit_events; DROP FUNCTION IF EXISTS datascope_file_audit_events_evidence();");
            foreach (var table in AppendOnlyTables)
            {
                migrationBuilder.Sql($"DROP TRIGGER IF EXISTS {table}_no_truncate ON {table}; DROP TRIGGER IF EXISTS {table}_no_update_delete ON {table};");
            }
            migrationBuilder.Sql("DROP FUNCTION IF EXISTS datascope_append_only();");
            migrationBuilder.Sql("DROP TRIGGER IF EXISTS app_audit_log_no_truncate ON app_audit_log; DROP TRIGGER IF EXISTS app_audit_log_no_update_delete ON app_audit_log;");
            migrationBuilder.Sql("DROP FUNCTION IF EXISTS app_audit_log_append_only();");
            migrationBuilder.DropTable(
                name: "activity_alerts");

            migrationBuilder.DropTable(
                name: "activity_baselines");

            migrationBuilder.DropTable(
                name: "activity_sources");

            migrationBuilder.DropTable(
                name: "app_audit_log");

            migrationBuilder.DropTable(
                name: "classification_findings");

            migrationBuilder.DropTable(
                name: "classification_overrides");

            migrationBuilder.DropTable(
                name: "compliance_standards");

            migrationBuilder.DropTable(
                name: "custom_detection_rules");

            migrationBuilder.DropTable(
                name: "directory_memberships");

            migrationBuilder.DropTable(
                name: "directory_principals");

            migrationBuilder.DropTable(
                name: "file_audit_events");

            migrationBuilder.DropTable(
                name: "folder_security_changes");

            migrationBuilder.DropTable(
                name: "notification_outbox");

            migrationBuilder.DropTable(
                name: "owner_assignments");

            migrationBuilder.DropTable(
                name: "posture_snapshots");

            migrationBuilder.DropTable(
                name: "review_events");

            migrationBuilder.DropTable(
                name: "review_exemptions");

            migrationBuilder.DropTable(
                name: "review_item_users");

            migrationBuilder.DropTable(
                name: "risk_decisions");

            migrationBuilder.DropTable(
                name: "scan_exclusions");

            migrationBuilder.DropTable(
                name: "scan_frontier");

            migrationBuilder.DropTable(
                name: "scan_gaps");

            migrationBuilder.DropTable(
                name: "security_aces");

            migrationBuilder.DropTable(
                name: "server_local_group_members");

            migrationBuilder.DropTable(
                name: "service_heartbeats");

            migrationBuilder.DropTable(
                name: "share_scan_history");

            migrationBuilder.DropTable(
                name: "system_settings");

            migrationBuilder.DropTable(
                name: "activity_gaps");

            migrationBuilder.DropTable(
                name: "files");

            migrationBuilder.DropTable(
                name: "directory_snapshots");

            migrationBuilder.DropTable(
                name: "server_local_groups");

            migrationBuilder.DropTable(
                name: "scan_jobs");

            migrationBuilder.DropTable(
                name: "review_decisions");

            migrationBuilder.DropTable(
                name: "review_items");

            migrationBuilder.DropTable(
                name: "access_reviews");

            migrationBuilder.DropTable(
                name: "folders");

            migrationBuilder.DropTable(
                name: "review_campaigns");

            migrationBuilder.DropTable(
                name: "shares");

            migrationBuilder.DropTable(
                name: "managed_servers");

            migrationBuilder.DropTable(
                name: "security_descriptors");
        }
    }
}

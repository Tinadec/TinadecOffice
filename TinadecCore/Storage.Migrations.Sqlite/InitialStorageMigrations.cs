using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using TinadecCore.Lifecycle;
using TinadecCore.Memory;

namespace TinadecCore.Storage.Migrations.Sqlite;

[DbContext(typeof(MemoryDbContext))]
[Migration("202607160001_MemoryInitial")]
public sealed class MemoryInitial : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable("projects", table => new
        {
            id = table.Column<Guid>(nullable: false), name = table.Column<string>(maxLength: 256, nullable: false),
            root_path = table.Column<string>(maxLength: 4096, nullable: false), normalized_root_path = table.Column<string>(maxLength: 4096, nullable: false),
            kind = table.Column<string>(maxLength: 64, nullable: false), created_at = table.Column<DateTimeOffset>(nullable: false),
            updated_at = table.Column<DateTimeOffset>(nullable: false), archived = table.Column<bool>(nullable: false)
        }, constraints: table => table.PrimaryKey("PK_projects", x => x.id));
        migrationBuilder.CreateIndex("IX_projects_normalized_root_path", "projects", "normalized_root_path", unique: true);
        migrationBuilder.CreateIndex("IX_projects_archived_updated_at", "projects", new[] { "archived", "updated_at" });
        migrationBuilder.CreateTable("sessions", table => new
        {
            id = table.Column<Guid>(nullable: false), project_id = table.Column<Guid>(nullable: false), title = table.Column<string>(maxLength: 512, nullable: false),
            status = table.Column<string>(maxLength: 64, nullable: false), mode = table.Column<string>(maxLength: 64, nullable: false), summary = table.Column<string>(maxLength: 4096, nullable: true),
            history_revision = table.Column<long>(nullable: false), created_at = table.Column<DateTimeOffset>(nullable: false), updated_at = table.Column<DateTimeOffset>(nullable: false), archived = table.Column<bool>(nullable: false)
        }, constraints: table => table.PrimaryKey("PK_sessions", x => x.id));
        migrationBuilder.CreateIndex("IX_sessions_project_id_archived_updated_at", "sessions", new[] { "project_id", "archived", "updated_at" });
    }
    protected override void Down(MigrationBuilder migrationBuilder) { migrationBuilder.DropTable("sessions"); migrationBuilder.DropTable("projects"); }
}

[DbContext(typeof(LifecycleDbContext))]
[Migration("202607160002_LifecycleInitial")]
public sealed class LifecycleInitial : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable("runs", table => new
        {
            id = table.Column<Guid>(nullable: false), session_id = table.Column<Guid>(nullable: false), trigger_message_id = table.Column<Guid>(nullable: false),
            status = table.Column<string>(maxLength: 64, nullable: false), created_at = table.Column<DateTimeOffset>(nullable: false), updated_at = table.Column<DateTimeOffset>(nullable: false),
            completed_at = table.Column<DateTimeOffset>(nullable: true), summary = table.Column<string>(maxLength: 4096, nullable: true), task_revision = table.Column<long>(nullable: false),
            last_event_sequence = table.Column<long>(nullable: false), last_event_at = table.Column<DateTimeOffset>(nullable: true)
        }, constraints: table => table.PrimaryKey("PK_runs", x => x.id));
        migrationBuilder.CreateIndex("IX_runs_session_id_created_at", "runs", new[] { "session_id", "created_at" });
        migrationBuilder.CreateIndex("IX_runs_session_id_status_updated_at", "runs", new[] { "session_id", "status", "updated_at" });
        migrationBuilder.CreateTable("event_index", table => new
        {
            event_id = table.Column<Guid>(nullable: false), run_id = table.Column<Guid>(nullable: false), session_id = table.Column<Guid>(nullable: false), project_id = table.Column<Guid>(nullable: false),
            event_type = table.Column<string>(maxLength: 256, nullable: false), severity = table.Column<string>(maxLength: 32, nullable: false), sequence = table.Column<long>(nullable: false),
            task_id = table.Column<Guid>(nullable: true), approval_id = table.Column<Guid>(nullable: true), tool_id = table.Column<string>(nullable: true), summary = table.Column<string>(maxLength: 4096, nullable: false),
            schema_version = table.Column<string>(maxLength: 32, nullable: false), payload_hash = table.Column<string>(maxLength: 128, nullable: false), relative_file_path = table.Column<string>(maxLength: 512, nullable: false),
            byte_offset = table.Column<long>(nullable: false), byte_length = table.Column<int>(nullable: false), timestamp = table.Column<DateTimeOffset>(nullable: false)
        }, constraints: table => table.PrimaryKey("PK_event_index", x => x.event_id));
        migrationBuilder.CreateIndex("IX_event_index_run_id_sequence", "event_index", new[] { "run_id", "sequence" }, unique: true);
        migrationBuilder.CreateIndex("IX_event_index_session_id_timestamp", "event_index", new[] { "session_id", "timestamp" });
        migrationBuilder.CreateIndex("IX_event_index_project_id_timestamp", "event_index", new[] { "project_id", "timestamp" });
        migrationBuilder.CreateIndex("IX_event_index_approval_id_event_type_timestamp", "event_index", new[] { "approval_id", "event_type", "timestamp" });
    }
    protected override void Down(MigrationBuilder migrationBuilder) { migrationBuilder.DropTable("event_index"); migrationBuilder.DropTable("runs"); }
}

using Microsoft.EntityFrameworkCore;
using TinadecCore.Persistence;

namespace TinadecCore.Tools;

public sealed class ToolsSettingsDbContext(DbContextOptions<ToolsSettingsDbContext> options) : ConfigurationProjectionDbContext(options)
{
    public DbSet<ToolSettingsRecord> Settings => Set<ToolSettingsRecord>();
    public DbSet<ToolSettingsVersionRecord> SettingVersions => Set<ToolSettingsVersionRecord>();
    public DbSet<McpResourceRecord> McpResources => Set<McpResourceRecord>();
    public DbSet<McpImportRecord> McpImports => Set<McpImportRecord>();

    protected override void OnModelCreating(ModelBuilder model)
    {
        model.Entity<ToolSettingsRecord>(e => { e.ToTable("tool_settings"); e.HasKey(x => x.Id); e.HasIndex(x => new { x.TenantId, x.WorkspaceId, x.ScopeKey }).IsUnique(); e.Property(x => x.Revision).IsConcurrencyToken(); e.Property(x => x.ScopeKey).HasMaxLength(64); });
        model.Entity<ToolSettingsVersionRecord>(e => { e.ToTable("tool_settings_versions"); e.HasKey(x => x.Id); e.HasIndex(x => new { x.SettingsId, x.Revision }).IsUnique(); });
        model.Entity<McpResourceRecord>(e => { e.ToTable("tool_mcp_resources"); e.HasKey(x => x.Id); e.HasIndex(x => new { x.TenantId, x.WorkspaceId, x.ScopeKey, x.ServerId }).IsUnique(); e.Property(x => x.Revision).IsConcurrencyToken(); e.Property(x => x.ScopeKey).HasMaxLength(64); e.Property(x => x.ServerId).HasMaxLength(128); e.Property(x => x.Transport).HasMaxLength(16); e.Property(x => x.Url).HasMaxLength(2048); });
        model.Entity<McpImportRecord>(e => { e.ToTable("tool_mcp_imports"); e.HasKey(x => x.Id); e.HasIndex(x => new { x.TenantId, x.WorkspaceId, x.ScopeKey }).IsUnique(); });
        model.UseTinadecSnakeCase();
    }
}

public sealed class ToolSettingsRecord
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid WorkspaceId { get; set; }
    public string ScopeKey { get; set; } = "shared";
    public long Revision { get; set; }
    public string SettingsJson { get; set; } = "{}";
    public DateTimeOffset UpdatedAt { get; set; }
    public Guid UpdatedByPrincipalId { get; set; }
}
public sealed class ToolSettingsVersionRecord
{
    public Guid Id { get; set; }
    public Guid SettingsId { get; set; }
    public long Revision { get; set; }
    public string SettingsJson { get; set; } = "{}";
    public DateTimeOffset CreatedAt { get; set; }
}
public sealed class McpResourceRecord
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid WorkspaceId { get; set; }
    public Guid? ProjectId { get; set; }
    public string ScopeKey { get; set; } = "shared";
    public string ServerId { get; set; } = "";
    public string Name { get; set; } = "";
    public bool Enabled { get; set; }
    /// <summary><c>stdio</c>, <c>http</c> (Streamable HTTP) or <c>sse</c> (legacy SSE).</summary>
    public string Transport { get; set; } = "stdio";
    public string? Url { get; set; }
    public string Command { get; set; } = "";
    public string ArgsJson { get; set; } = "[]";
    public string SecretReferencesJson { get; set; } = "{}";
    /// <summary>Header name → secret reference. Header values are never stored in plaintext.</summary>
    public string HeaderReferencesJson { get; set; } = "{}";
    public string? Cwd { get; set; }
    public long Revision { get; set; }
    public string ConfigurationHash { get; set; } = "";
    public string? ImportSource { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public Guid UpdatedByPrincipalId { get; set; }
    public DateTimeOffset? DeletedAt { get; set; }
}
public sealed class McpImportRecord
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid WorkspaceId { get; set; }
    public string ScopeKey { get; set; } = "shared";
    public string SourcePath { get; set; } = "";
    public string ContentHash { get; set; } = "";
    public string? Error { get; set; }
    public DateTimeOffset ImportedAt { get; set; }
}

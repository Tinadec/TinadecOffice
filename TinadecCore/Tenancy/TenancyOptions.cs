namespace TinadecCore.Tenancy;

public sealed class TenancyOptions
{
    public const string SectionName = "TinadecIdentity";
    public bool EnableDevelopmentIdentity { get; set; } = true;
    public string DevelopmentIssuer { get; set; } = "tinadec-development";
    public string DevelopmentSubject { get; set; } = "desktop";
    public string DevelopmentTenantSlug { get; set; } = "local";
    public string DevelopmentWorkspaceSlug { get; set; } = "default";
}

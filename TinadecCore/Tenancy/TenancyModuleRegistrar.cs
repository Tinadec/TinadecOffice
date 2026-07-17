using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using TinadecCore.Abstractions;
using TinadecCore.Abstractions.Ports;
using TinadecCore.Persistence;

namespace TinadecCore.Tenancy;

public sealed class TenancyModuleRegistrar : IModuleRegistrar
{
    public string ModuleId => "tenancy";

    public void Register(ITinadecCoreBuilder builder)
    {
        builder.Services.AddOptions<TenancyOptions>().BindConfiguration(TenancyOptions.SectionName);
        builder.Services.AddDbContextFactory<TenancyDbContext>((sp, options) =>
        {
            options.UseTinadecDatabase(sp);
            options.ConfigureWarnings(w => w
                .Ignore(Microsoft.EntityFrameworkCore.Diagnostics.CoreEventId.QueryIterationFailed)
                .Ignore(Microsoft.EntityFrameworkCore.Diagnostics.RelationalEventId.CommandError));
        });
        builder.Services.AddSingleton<ITenantContextAccessor, DevelopmentTenantContextAccessor>();
        builder.Services.AddSingleton<TenancyStore>();
        builder.Services.AddSingleton<IStorageMigrationParticipant>(sp => sp.GetRequiredService<TenancyStore>());
        builder.RegisterModule(new ModuleDescriptor
        {
            ModuleId = ModuleId,
            Version = "0.1.0",
            Dependencies = ["abstractions", "persistence"],
            Capabilities = ["tenant_isolation", "workspace_membership", "external_identity"],
            Language = "C#",
            RegistrationStatus = ModuleRegistrationStatus.Registered
        });
    }
}

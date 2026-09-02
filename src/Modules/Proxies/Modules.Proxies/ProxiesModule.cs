using Asp.Versioning;
using FSH.Framework.Persistence;
using FSH.Framework.Shared.Constants;
using FSH.Framework.Web.Modules;
using FSH.Modules.Proxies.Contracts.Authorization;
using FSH.Modules.Proxies.Data;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;

[assembly: FshModule(typeof(FSH.Modules.Proxies.ProxiesModule), 650)]

namespace FSH.Modules.Proxies;

public sealed class ProxiesModule : IModule
{
    public void ConfigureServices(IHostApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        PermissionConstants.Register(ProxiesPermissions.All);

        builder.Services.AddHeroDbContext<ProxiesDbContext>();
        builder.Services.AddScoped<IDbInitializer, ProxiesDbInitializer>();

        builder.Services.AddHealthChecks()
            .AddDbContextCheck<ProxiesDbContext>(name: "db:proxies", failureStatus: HealthStatus.Unhealthy);
    }

    public void ConfigureMiddleware(IApplicationBuilder app)
    {
        // No custom middleware needed yet.
    }

    public void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        var versionSet = endpoints.NewApiVersionSet()
            .HasApiVersion(new ApiVersion(1))
            .ReportApiVersions()
            .Build();

        var group = endpoints
            .MapGroup("api/v{version:apiVersion}/proxies")
            .WithTags("Proxies")
            .WithApiVersionSet(versionSet)
            .RequireAuthorization();

        // Endpoint registrations added in later tasks.
    }
}

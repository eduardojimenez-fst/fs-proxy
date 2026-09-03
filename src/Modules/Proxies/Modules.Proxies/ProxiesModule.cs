using Asp.Versioning;
using FSH.Framework.Persistence;
using FSH.Framework.Shared.Constants;
using FSH.Framework.Web.HttpResilience;
using FSH.Framework.Web.Modules;
using FSH.Modules.Proxies.Authentication;
using FSH.Modules.Proxies.Contracts.Authorization;
using FSH.Modules.Proxies.Data;
using FSH.Modules.Proxies.Features.v1.ApiClients.CreateApiClient;
using FSH.Modules.Proxies.Features.v1.ApiClients.DeleteApiClient;
using FSH.Modules.Proxies.Features.v1.ApiClients.ListApiClients;
using FSH.Modules.Proxies.Features.v1.HealthCheckTargets.AssignHealthCheckTargetToTag;
using FSH.Modules.Proxies.Features.v1.HealthCheckTargets.CreateHealthCheckTarget;
using FSH.Modules.Proxies.Features.v1.HealthCheckTargets.DeleteHealthCheckTarget;
using FSH.Modules.Proxies.Features.v1.HealthCheckTargets.ListHealthCheckTargets;
using FSH.Modules.Proxies.Features.v1.HealthCheckTargets.UnassignHealthCheckTargetFromTag;
using FSH.Modules.Proxies.Features.v1.HealthCheckTargets.UpdateHealthCheckTarget;
using FSH.Modules.Proxies.Features.v1.ManualProxies.CreateManualProxy;
using FSH.Modules.Proxies.Features.v1.ManualProxies.DeleteManualProxy;
using FSH.Modules.Proxies.Features.v1.ManualProxies.UpdateManualProxy;
using FSH.Modules.Proxies.Features.v1.Policies.AssignPolicyToTag;
using FSH.Modules.Proxies.Features.v1.Policies.CreatePolicyProfile;
using FSH.Modules.Proxies.Features.v1.Policies.DeletePolicyProfile;
using FSH.Modules.Proxies.Features.v1.Policies.ListPolicyProfiles;
using FSH.Modules.Proxies.Features.v1.Policies.UnassignPolicyFromTag;
using FSH.Modules.Proxies.Features.v1.Policies.UpdatePolicyProfile;
using FSH.Modules.Proxies.Features.v1.Proxies.DisableProxies;
using FSH.Modules.Proxies.Features.v1.Proxies.EnableProxies;
using FSH.Modules.Proxies.Features.v1.Proxies.ListProxies;
using FSH.Modules.Proxies.Features.v1.ProviderAccounts.CreateProviderAccount;
using FSH.Modules.Proxies.Features.v1.ProviderAccounts.DeleteProviderAccount;
using FSH.Modules.Proxies.Features.v1.ProviderAccounts.GetProviderAccountById;
using FSH.Modules.Proxies.Features.v1.ProviderAccounts.ListProviderAccounts;
using FSH.Modules.Proxies.Features.v1.ProviderAccounts.SyncProviderAccountNow;
using FSH.Modules.Proxies.Features.v1.ProviderAccounts.UpdateProviderAccount;
using FSH.Modules.Proxies.Features.v1.Tags.CreateTag;
using FSH.Modules.Proxies.Features.v1.Tags.DeleteTag;
using FSH.Modules.Proxies.Features.v1.Tags.ListTags;
using FSH.Modules.Proxies.Options;
using FSH.Modules.Proxies.Providers;
using FSH.Modules.Proxies.Providers.BrightData;
using FSH.Modules.Proxies.Providers.Oxylabs;
using FSH.Modules.Proxies.Providers.WebShare;
using FSH.Modules.Proxies.Services;
using Hangfire;
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

        builder.Services.AddSingleton<ProviderAccountCredentialProtector>();
        builder.Services.AddSingleton<ProxyPasswordProtector>();
        builder.Services.AddSingleton<IApiKeyHasher, ApiKeyHasher>();

        // ProviderAccount CRUD handlers (Task 7) depend on the IProxySecretProtector interface
        // for testability rather than the concrete ProviderAccountCredentialProtector type.
        // Delegate to the singleton above instead of a second AddSingleton<...>() registration so
        // there is exactly one protector instance in play. This is unkeyed and maps specifically
        // to ProviderAccountCredentialProtector; a later task introducing ManualProxies handlers
        // that need ProxyPasswordProtector behind the same interface must use keyed DI
        // (AddKeyedSingleton) instead of adding a second unkeyed registration here, which would
        // make plain IProxySecretProtector resolution ambiguous between the two protectors.
        builder.Services.AddSingleton<IProxySecretProtector>(sp => sp.GetRequiredService<ProviderAccountCredentialProtector>());

        // ManualProxies CRUD handlers (Task 8) need to resolve IProxySecretProtector
        // unambiguously to ProxyPasswordProtector specifically, distinct from the unkeyed
        // registration above (which maps to ProviderAccountCredentialProtector). Both keyed
        // registrations below delegate to the same two Task 5 singletons — they add resolution
        // paths, they don't replace anything.
        builder.Services.AddKeyedSingleton<IProxySecretProtector>("provider-account", (sp, _) => sp.GetRequiredService<ProviderAccountCredentialProtector>());
        builder.Services.AddKeyedSingleton<IProxySecretProtector>("proxy-password", (sp, _) => sp.GetRequiredService<ProxyPasswordProtector>());

        builder.Services.AddScoped<IProxyProviderAdapter, ManualAdapter>();

        builder.Services.AddHttpClient("ProxyProvider:WebShare")
            .AddHeroResilience(builder.Configuration);
        builder.Services.AddScoped<IProxyProviderAdapter, WebShareAdapter>();

        builder.Services.AddHttpClient("ProxyProvider:Oxylabs")
            .AddHeroResilience(builder.Configuration);
        builder.Services.AddScoped<IProxyProviderAdapter, OxylabsAdapter>();

        builder.Services.AddHttpClient("ProxyProvider:BrightData")
            .AddHeroResilience(builder.Configuration);
        builder.Services.AddScoped<IProxyProviderAdapter, BrightDataAdapter>();

        builder.Services.AddScoped<IProxyProviderAdapterFactory, ProxyProviderAdapterFactory>();

        builder.Services.AddScoped<IProviderAccountSyncService, ProviderAccountSyncService>();
        builder.Services.AddScoped<IProxyRenewalService, ProxyRenewalService>();
        builder.Services.AddScoped<IPolicyEvaluationService, PolicyEvaluationService>();

        builder.Services.AddOptions<ProxiesOptions>()
            .BindConfiguration(nameof(ProxiesOptions))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        builder.Services.AddScoped<IHealthCheckTargetResolver, HealthCheckTargetResolver>();
        builder.Services.AddScoped<IProxyPasswordResolver, ProxyPasswordResolver>();

        builder.Services.AddHealthChecks()
            .AddDbContextCheck<ProxiesDbContext>(name: "db:proxies", failureStatus: HealthStatus.Unhealthy);

        // Dual authentication (Task 22): a new "ApiKey" scheme for the two consumer-facing
        // endpoints landing in Tasks 23-24, added alongside — never replacing — the JWT scheme
        // the Identity module already registers. AddAuthentication() (parameterless) adds the
        // scheme into Identity's existing AuthenticationBuilder without touching
        // DefaultAuthenticateScheme/DefaultChallengeScheme, so JWT stays the default for every
        // other endpoint. The "ProxiesConsumerAccess" policy accepts either scheme; admin
        // endpoints in this module keep using the app-wide default RequirePermission policy
        // (JWT-only), unchanged.
        builder.Services.AddScoped<IApiKeyAuthenticator, ApiKeyAuthenticator>();

        builder.Services.AddAuthentication()
            .AddScheme<ApiKeyAuthenticationOptions, ApiKeyAuthenticationHandler>(
                ApiKeyAuthenticationDefaults.SchemeName, _ => { });

        builder.Services.AddAuthorizationBuilder()
            .AddPolicy(ApiKeyAuthenticationDefaults.ConsumerPolicyName, policy =>
                policy
                    .AddAuthenticationSchemes(ApiKeyAuthenticationDefaults.SchemeName, Microsoft.AspNetCore.Authentication.JwtBearer.JwtBearerDefaults.AuthenticationScheme)
                    .RequireAuthenticatedUser());
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

        group.MapCreateProviderAccountEndpoint();
        group.MapUpdateProviderAccountEndpoint();
        group.MapDeleteProviderAccountEndpoint();
        group.MapGetProviderAccountByIdEndpoint();
        group.MapListProviderAccountsEndpoint();
        group.MapSyncProviderAccountNowEndpoint();

        group.MapCreateManualProxyEndpoint();
        group.MapUpdateManualProxyEndpoint();
        group.MapDeleteManualProxyEndpoint();

        group.MapListProxiesEndpoint();
        group.MapEnableProxiesEndpoint();
        group.MapDisableProxiesEndpoint();

        group.MapCreateTagEndpoint();
        group.MapDeleteTagEndpoint();
        group.MapListTagsEndpoint();

        group.MapCreatePolicyProfileEndpoint();
        group.MapUpdatePolicyProfileEndpoint();
        group.MapDeletePolicyProfileEndpoint();
        group.MapAssignPolicyToTagEndpoint();
        group.MapUnassignPolicyFromTagEndpoint();
        group.MapListPolicyProfilesEndpoint();

        group.MapCreateHealthCheckTargetEndpoint();
        group.MapUpdateHealthCheckTargetEndpoint();
        group.MapDeleteHealthCheckTargetEndpoint();
        group.MapAssignHealthCheckTargetToTagEndpoint();
        group.MapUnassignHealthCheckTargetFromTagEndpoint();
        group.MapListHealthCheckTargetsEndpoint();

        group.MapCreateApiClientEndpoint();
        group.MapDeleteApiClientEndpoint();
        group.MapListApiClientsEndpoint();

        // Hourly periodic sync of every enabled provider account — mirrors Files'
        // PurgeOrphanedFilesJob registration exactly.
        var jobManager = endpoints.ServiceProvider.GetService<IRecurringJobManager>();
        if (jobManager is not null)
        {
            jobManager.AddOrUpdate<Jobs.ProviderAccountSyncJob>(
                "proxies-provider-account-sync",
                j => j.RunAsync(CancellationToken.None),
                "0 * * * *", // hourly
                new RecurringJobOptions { TimeZone = TimeZoneInfo.Utc });

            // Active connectivity probe of every Active proxy, every 15 minutes.
            jobManager.AddOrUpdate<Jobs.ProxyActiveHealthCheckJob>(
                "proxies-active-health-check",
                j => j.RunAsync(CancellationToken.None),
                "*/15 * * * *",
                new RecurringJobOptions { TimeZone = TimeZoneInfo.Utc });
        }
    }
}

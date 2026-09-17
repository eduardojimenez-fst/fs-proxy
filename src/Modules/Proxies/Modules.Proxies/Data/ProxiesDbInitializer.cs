using System.Text.Json;
using FSH.Framework.Persistence;
using FSH.Modules.Proxies.Contracts;
using FSH.Modules.Proxies.Domain;
using FSH.Modules.Proxies.Providers.BrightData;
using FSH.Modules.Proxies.Providers.WebShare;
using FSH.Modules.Proxies.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace FSH.Modules.Proxies.Data;

public sealed partial class ProxiesDbInitializer(
    ProxiesDbContext dbContext,
    IConfiguration configuration,
    IHostEnvironment environment,
    IProxySecretProtector protector,
    ILogger<ProxiesDbInitializer> logger)
    : IDbInitializer
{
    private const string BrightDataDevAccountName = "Bright Data - JP - datacenter_new_proxy_manager (dev seed)";
    private const string WebShareDevAccountName = "WebShare JP - (dev seed)";

    /// <summary>Name of the seeded default profile. Public so tests and tooling can identify it.</summary>
    public const string DefaultPolicyProfileName = "Default (holgada) — 20 fallos / 60 min / 2 reporters";

    public async Task MigrateAsync(CancellationToken cancellationToken)
    {
        if ((await dbContext.Database.GetPendingMigrationsAsync(cancellationToken).ConfigureAwait(false)).Any())
        {
            await dbContext.Database.MigrateAsync(cancellationToken).ConfigureAwait(false);
            LogAppliedMigrations(logger);
        }
    }

    public async Task SeedAsync(CancellationToken cancellationToken)
    {
        await SeedManualProviderAccountAsync(cancellationToken).ConfigureAwait(false);

        if (environment.IsDevelopment())
        {
            await SeedDevProviderAccountsAsync(cancellationToken).ConfigureAwait(false);
        }

        await SeedTagCategoriesAsync(cancellationToken).ConfigureAwait(false);
        await SeedDefaultPolicyProfileAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task SeedManualProviderAccountAsync(CancellationToken cancellationToken)
    {
        bool exists = await dbContext.ProviderAccounts
            .AnyAsync(x => x.Id == ManualProviderAccount.Id, cancellationToken).ConfigureAwait(false);
        if (exists)
        {
            return;
        }

        var manualAccount = ProviderAccount.CreateWithId(ManualProviderAccount.Id, "Manual", ProxyProviderType.Manual, "n/a");
        dbContext.ProviderAccounts.Add(manualAccount);
        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        LogSeededManualAccount(logger);
    }

    /// <summary>
    /// Dev-only convenience: seeds real provider accounts from `dotnet user-secrets` (never source
    /// control) so credentials don't need to be re-entered through the UI on every local test.
    /// Each provider is independent and silently skipped when its own keys are absent.
    /// </summary>
    private async Task SeedDevProviderAccountsAsync(CancellationToken cancellationToken)
    {
        var brightDataSection = configuration.GetSection("Seed:ProxyProviders:BrightData");
        var apiToken = brightDataSection["ApiToken"];
        var zone = brightDataSection["Zone"];
        var customerId = brightDataSection["CustomerId"];
        var gatewayPort = brightDataSection.GetValue<int?>("GatewayPort");
        if (!string.IsNullOrWhiteSpace(apiToken) && !string.IsNullOrWhiteSpace(zone)
            && !string.IsNullOrWhiteSpace(customerId) && gatewayPort is not null)
        {
            bool exists = await dbContext.ProviderAccounts
                .AnyAsync(x => x.Name == BrightDataDevAccountName, cancellationToken).ConfigureAwait(false);
            if (!exists)
            {
                var credentials = JsonSerializer.Serialize(new BrightDataCredentials(apiToken, zone, customerId, gatewayPort.Value));
                var account = ProviderAccount.Create(BrightDataDevAccountName, ProxyProviderType.BrightData, protector.Protect(credentials));
                dbContext.ProviderAccounts.Add(account);
                LogSeededDevProviderAccount(logger, BrightDataDevAccountName);
            }
        }

        var webShareSection = configuration.GetSection("Seed:ProxyProviders:WebShare");
        var apiKey = webShareSection["ApiKey"];
        if (!string.IsNullOrWhiteSpace(apiKey))
        {
            bool exists = await dbContext.ProviderAccounts
                .AnyAsync(x => x.Name == WebShareDevAccountName, cancellationToken).ConfigureAwait(false);
            if (!exists)
            {
                var credentials = JsonSerializer.Serialize(new WebShareCredentials(apiKey));
                var account = ProviderAccount.Create(WebShareDevAccountName, ProxyProviderType.WebShare, protector.Protect(credentials));
                dbContext.ProviderAccounts.Add(account);
                LogSeededDevProviderAccount(logger, WebShareDevAccountName);
            }
        }

        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Reference tag catalog, seeded once in every environment (not dev-only).</summary>
    private async Task SeedTagCategoriesAsync(CancellationToken cancellationToken)
    {
        if (await dbContext.TagCategories.AnyAsync(cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        foreach (var (name, values) in TagCategorySeedData.Categories)
        {
            var category = TagCategory.Create(name);
            foreach (var value in values)
            {
                category.AddValue(value);
            }
            dbContext.TagCategories.Add(category);
        }

        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        LogSeededTagCategories(logger, TagCategorySeedData.Categories.Count);
    }

    /// <summary>
    /// Seeds one deliberately slack policy profile so the auto-disable machinery can be exercised
    /// end to end without becoming a hazard: 20 failures inside 60 minutes, corroborated by at
    /// least 2 distinct reporters, and <see cref="PolicyProfileType.AutoDisable"/> rather than
    /// AutoDisableAndRenew — renewal calls the provider's API and spends real inventory.
    ///
    /// Deliberately left UNASSIGNED to every tag. A profile only acts on proxies through a tag
    /// assignment, so seeding it alone changes nothing about a running system; assigning it is a
    /// one-click, reversible decision on the Policies page. Seeding an *assignment* would silently
    /// arm auto-disabling across an existing fleet on the next deploy, which is exactly the risk
    /// this seed is meant to avoid.
    ///
    /// Idempotent by name: an operator who retunes or deletes it will not have it reappear with
    /// the original numbers on the next migrate/seed run.
    /// </summary>
    private async Task SeedDefaultPolicyProfileAsync(CancellationToken cancellationToken)
    {
        if (await dbContext.PolicyProfiles.AnyAsync(cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        var profile = PolicyProfile.Create(
            DefaultPolicyProfileName,
            PolicyProfileType.AutoDisable,
            failureThreshold: 20,
            windowMinutes: 60,
            minDistinctReporters: 2);

        dbContext.PolicyProfiles.Add(profile);
        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        LogSeededDefaultPolicy(logger, DefaultPolicyProfileName);
    }

    // LoggerMessage source-gen: compile-time templates avoid CA1873 (eager arg eval).
    [LoggerMessage(EventId = 1, Level = LogLevel.Information, Message = "[Proxies] applied migrations")]
    private static partial void LogAppliedMigrations(ILogger logger);

    [LoggerMessage(EventId = 2, Level = LogLevel.Information, Message = "Seeded the well-known Manual provider account.")]
    private static partial void LogSeededManualAccount(ILogger logger);

    [LoggerMessage(EventId = 3, Level = LogLevel.Information, Message = "Seeded the {AccountName} provider account from user-secrets.")]
    private static partial void LogSeededDevProviderAccount(ILogger logger, string accountName);

    [LoggerMessage(EventId = 4, Level = LogLevel.Information, Message = "Seeded {Count} default tag categories.")]
    private static partial void LogSeededTagCategories(ILogger logger, int count);

    [LoggerMessage(
        EventId = 5,
        Level = LogLevel.Information,
        Message = "Seeded the default policy profile '{ProfileName}'. It is not assigned to any tag yet, so nothing is auto-disabled until an operator assigns it.")]
    private static partial void LogSeededDefaultPolicy(ILogger logger, string profileName);
}

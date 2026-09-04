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
    private const string BrightDataDevAccountName = "BrightData (dev seed)";
    private const string WebShareDevAccountName = "WebShare (dev seed)";

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
        if (!string.IsNullOrWhiteSpace(apiToken) && !string.IsNullOrWhiteSpace(zone))
        {
            bool exists = await dbContext.ProviderAccounts
                .AnyAsync(x => x.Name == BrightDataDevAccountName, cancellationToken).ConfigureAwait(false);
            if (!exists)
            {
                var credentials = JsonSerializer.Serialize(new BrightDataCredentials(apiToken, zone));
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

    // LoggerMessage source-gen: compile-time templates avoid CA1873 (eager arg eval).
    [LoggerMessage(EventId = 1, Level = LogLevel.Information, Message = "[Proxies] applied migrations")]
    private static partial void LogAppliedMigrations(ILogger logger);

    [LoggerMessage(EventId = 2, Level = LogLevel.Information, Message = "Seeded the well-known Manual provider account.")]
    private static partial void LogSeededManualAccount(ILogger logger);

    [LoggerMessage(EventId = 3, Level = LogLevel.Information, Message = "Seeded the {AccountName} provider account from user-secrets.")]
    private static partial void LogSeededDevProviderAccount(ILogger logger, string accountName);

    [LoggerMessage(EventId = 4, Level = LogLevel.Information, Message = "Seeded {Count} default tag categories.")]
    private static partial void LogSeededTagCategories(ILogger logger, int count);
}

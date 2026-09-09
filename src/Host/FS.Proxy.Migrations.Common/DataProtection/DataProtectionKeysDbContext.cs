using Microsoft.AspNetCore.DataProtection.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace FS.Proxy.Migrations.Common.DataProtection;

/// <summary>
/// Persists ASP.NET Core Data Protection keys to the application database — the one piece of
/// infrastructure both FS.Proxy.Api and the separately-run FS.Proxy.DbMigrator always share,
/// regardless of whether Redis happens to be configured for either. Without this, DbMigrator
/// (typically run standalone — see the DbMigrator README, "Migrations / seed, separate step" —
/// outside AppHost's automatic Redis connection-string injection) and the API can end up with two
/// entirely different key stores: encrypted <c>ProviderAccount.ProtectedCredentials</c> the
/// migrator's dev-seed writes becomes permanently undecryptable by the API
/// ("CryptographicException: key {guid} not found in the key ring"), no matter how consistently the
/// Data Protection application name is pinned.
///
/// Deliberately a plain <see cref="DbContext"/>, not the app's tenant-aware <c>BaseDbContext</c>:
/// Data Protection keys are global infrastructure the framework itself manages, not tenant data.
///
/// Provider-neutral by design — it lives in FS.Proxy.Migrations.Common, and each provider's
/// migrations project (PostgreSQL + MSSQL) carries its own <c>DataProtection\</c> folder for it.
/// Both hosts wire it through <c>ConfigureHeroDatabase</c>, so it follows
/// <c>DatabaseOptions:Provider</c> like every other context.
/// </summary>
public sealed class DataProtectionKeysDbContext(DbContextOptions<DataProtectionKeysDbContext> options)
    : DbContext(options), IDataProtectionKeyContext
{
    public DbSet<DataProtectionKey> DataProtectionKeys => Set<DataProtectionKey>();
}

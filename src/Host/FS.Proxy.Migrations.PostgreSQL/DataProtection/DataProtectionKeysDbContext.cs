using Microsoft.AspNetCore.DataProtection.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace FS.Proxy.Migrations.PostgreSQL.DataProtection;

/// <summary>
/// Persists ASP.NET Core Data Protection keys to Postgres — the one piece of infrastructure both
/// FS.Proxy.Api and the separately-run FS.Proxy.DbMigrator always share, regardless of whether
/// Redis happens to be configured for either. Without this, DbMigrator (typically run standalone —
/// see the DbMigrator README, "Migrations / seed, separate step" — outside AppHost's automatic
/// Redis connection-string injection) and the API can end up with two entirely different key
/// stores: encrypted <c>ProviderAccount.ProtectedCredentials</c> the migrator's dev-seed writes
/// becomes permanently undecryptable by the API ("CryptographicException: key {guid} not found in
/// the key ring"), no matter how consistently the Data Protection application name is pinned.
///
/// Deliberately a plain <see cref="DbContext"/>, not the app's tenant-aware <c>BaseDbContext</c>:
/// Data Protection keys are global infrastructure the framework itself manages, not tenant data.
/// </summary>
public sealed class DataProtectionKeysDbContext(DbContextOptions<DataProtectionKeysDbContext> options)
    : DbContext(options), IDataProtectionKeyContext
{
    public DbSet<DataProtectionKey> DataProtectionKeys => Set<DataProtectionKey>();
}

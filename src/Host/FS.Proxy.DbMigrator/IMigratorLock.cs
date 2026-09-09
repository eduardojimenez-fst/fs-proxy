using FSH.Framework.Shared.Persistence;
using Microsoft.Extensions.Logging;

namespace FS.Proxy.DbMigrator;

/// <summary>
/// Serialises concurrent migrator invocations and waits for the database to become reachable.
/// </summary>
/// <remarks>
/// Both providers use a lock that the database server itself owns and releases when the holding
/// connection drops, so a migrator that crashes mid-run never strands the lock.
/// </remarks>
internal interface IMigratorLock
{
    /// <summary>Human-readable provider name, used in operator-facing console output.</summary>
    string ProviderDisplayName { get; }

    /// <summary>
    /// Polls the configured database until it accepts a connection — handles Aspire/K8s cold-starts.
    /// Returns as soon as the server is reachable, including when the target database does not exist
    /// yet (EF creates it on the first migrate).
    /// </summary>
    Task WaitForDatabaseAsync(string connectionString, ILogger logger, CancellationToken cancellationToken);

    /// <summary>
    /// Acquires the migrator lock, blocking until it is available. Disposing the returned handle
    /// releases it.
    /// </summary>
    Task<IAsyncDisposable> AcquireAsync(string connectionString, ILogger logger, CancellationToken cancellationToken);

    /// <summary>Opens a connection and reports the effective login and database, for operator logs.</summary>
    Task<(string User, string Database)> GetConnectionIdentityAsync(string connectionString, CancellationToken cancellationToken);

    /// <summary>
    /// Discards every pooled connection for the provider, so the next probe asks the server instead
    /// of reusing what the pool remembers.
    /// </summary>
    /// <remarks>
    /// Needed after the Data Protection bootstrap creates the database on a cold start.
    /// Microsoft.Data.SqlClient's pool blocking period (<c>PoolBlockingPeriod=Auto</c> is
    /// <c>AlwaysBlock</c> outside Azure) replays the login failure that the just-created database
    /// produced moments earlier for several more seconds. EF's SQL Server existence check reads that
    /// replayed error 4060 as "the database is not there" and issues a second
    /// <c>CREATE DATABASE</c>, which fails with "Database ... already exists" and takes the whole
    /// migrator down on the very first run. Postgres never had the symptom — EF checks
    /// <c>pg_database</c> from the maintenance database rather than by opening the target — but
    /// clearing its pool there is harmless and keeps the two implementations honest.
    /// </remarks>
    void ResetPooledConnections();
}

/// <summary>
/// Selects the migrator lock implementation for the configured provider.
/// </summary>
internal static class MigratorLockFactory
{
    public static IMigratorLock Create(string provider) => provider?.ToUpperInvariant() switch
    {
        DbProviders.PostgreSQL => new PostgresMigratorLock(),
        DbProviders.MSSQL => new SqlServerMigratorLock(),
        _ => throw new InvalidOperationException(
            $"Database Provider {provider} is not supported. Use '{DbProviders.PostgreSQL}' or '{DbProviders.MSSQL}'.")
    };
}

/// <summary>A lock handle that owns nothing — used when the target database does not exist yet.</summary>
internal sealed class NoopMigratorLock : IAsyncDisposable
{
    public static readonly NoopMigratorLock Instance = new();

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

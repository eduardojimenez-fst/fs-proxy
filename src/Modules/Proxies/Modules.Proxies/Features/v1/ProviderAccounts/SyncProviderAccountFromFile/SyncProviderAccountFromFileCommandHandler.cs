using System.Net;
using System.Text.Json;
using FSH.Framework.Core.Exceptions;
using FSH.Modules.Proxies.Contracts.Dtos;
using FSH.Modules.Proxies.Contracts.v1.ProviderAccounts;
using FSH.Modules.Proxies.Data;
using FSH.Modules.Proxies.Domain;
using FSH.Modules.Proxies.Providers;
using FSH.Modules.Proxies.Providers.FileImport;
using FSH.Modules.Proxies.Services;
using Mediator;
using Microsoft.EntityFrameworkCore;

namespace FSH.Modules.Proxies.Features.v1.ProviderAccounts.SyncProviderAccountFromFile;

public sealed class SyncProviderAccountFromFileCommandHandler(
    ProxiesDbContext dbContext, IProxySecretProtector protector, IProviderAccountSyncService syncService)
    : ICommandHandler<SyncProviderAccountFromFileCommand, FileImportResult>
{
    public async ValueTask<FileImportResult> Handle(SyncProviderAccountFromFileCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        var account = await dbContext.ProviderAccounts.FirstOrDefaultAsync(x => x.Id == command.ProviderAccountId, cancellationToken).ConfigureAwait(false)
            ?? throw new NotFoundException($"Provider account {command.ProviderAccountId} not found.");

        if (account.Id == ManualProviderAccount.Id)
        {
            throw new CustomException(
                "File import is not supported for the well-known Manual provider account — use \"Add manual proxy\" instead.",
                (IEnumerable<string>?)null, HttpStatusCode.BadRequest);
        }

        if (command.DefaultUsername is not null || command.DefaultPassword is not null)
        {
            var (existingUsername, existingPassword, incompatibleShape) = TryReadExistingDefaults(account.ProtectedCredentials);
            if (incompatibleShape)
            {
                throw new CustomException(
                    $"Provider account \"{account.Name}\" already has credentials configured that are not "
                    + "file-import default-username/password style. Refusing to overwrite them — clear the "
                    + "account's credentials first if you intend to use it for file-based sync only.",
                    (IEnumerable<string>?)null, HttpStatusCode.BadRequest);
            }

            var merged = new FileImportDefaultCredentials(
                command.DefaultUsername ?? existingUsername,
                command.DefaultPassword ?? existingPassword);
            account.UpdateCredentials(protector.Protect(JsonSerializer.Serialize(merged)));
        }

        ProviderFileParseResult parsed;
        try
        {
            parsed = ProviderFileParser.Parse(command.FileContent);
        }
        catch (FormatException ex)
        {
            throw new CustomException(ex.Message, (IEnumerable<string>?)null, HttpStatusCode.BadRequest);
        }

        if (parsed.Records.Count == 0)
        {
            throw new CustomException(
                "The file contains no valid proxy rows — refusing to run, since this account's full proxy list "
                + "would otherwise be retired.",
                parsed.Errors.Select(e => $"line {e.LineNumber}: {e.Message}"),
                HttpStatusCode.BadRequest);
        }

        FileImportDefaultCredentials? storedDefaults = null;
        if (parsed.Records.Any(r => r.Username is null || r.Password is null))
        {
            storedDefaults = TryDeserializeDefaults(protector.Unprotect(account.ProtectedCredentials));
            if (storedDefaults?.Username is null || storedDefaults.Password is null)
            {
                throw new CustomException(
                    "One or more rows omit Username/Password and no default credentials are configured for this account. "
                    + "Pass defaultUsername/defaultPassword on this upload once to set them.",
                    (IEnumerable<string>?)null, HttpStatusCode.BadRequest);
            }
        }

        var resolved = parsed.Records.Select(r => r with
        {
            Username = r.Username ?? storedDefaults!.Username,
            Password = r.Password ?? storedDefaults!.Password,
            Geolocation = r.Geolocation ?? command.DefaultGeolocation,
            Kind = r.Kind ?? command.DefaultProxyKind,
        }).ToList();

        // ReconcileAsync tracks changes on `dbContext` via the same scoped instance this handler
        // holds — both resolve from the same DI scope, so the single SaveChangesAsync below
        // flushes the reconciled Proxy rows and the RecordSyncResult update together.
        var (created, updated, retired) = await syncService.ReconcileAsync(account, resolved, cancellationToken).ConfigureAwait(false);

        account.RecordSyncResult(success: true, statusMessage: $"Imported {resolved.Count} proxies from file.");
        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return new FileImportResult(created, updated, retired, parsed.Errors);
    }

    /// <summary>
    /// Best-effort extraction of a previously-stored <see cref="FileImportDefaultCredentials"/> pair
    /// from the account's current (possibly differently-shaped, e.g. a live adapter's real API
    /// credentials) <c>ProtectedCredentials</c>. Returns <c>IncompatibleShape: true</c> when the
    /// stored JSON contains any property other than Username/Password (case-insensitive) — a strong
    /// signal this account already holds real API credentials this operation must not clobber.
    /// </summary>
    private (string? Username, string? Password, bool IncompatibleShape) TryReadExistingDefaults(string protectedCredentials)
    {
        string decrypted;
        try
        {
            decrypted = protector.Unprotect(protectedCredentials);
        }
        catch
        {
            return (null, null, false);
        }

        try
        {
            using var doc = JsonDocument.Parse(decrypted);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                return (null, null, true);
            }

            string? username = null;
            string? password = null;
            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                if (string.Equals(prop.Name, "Username", StringComparison.OrdinalIgnoreCase))
                {
                    username = prop.Value.ValueKind == JsonValueKind.String ? prop.Value.GetString() : null;
                }
                else if (string.Equals(prop.Name, "Password", StringComparison.OrdinalIgnoreCase))
                {
                    password = prop.Value.ValueKind == JsonValueKind.String ? prop.Value.GetString() : null;
                }
                else
                {
                    return (null, null, true);
                }
            }

            return (username, password, false);
        }
        catch (JsonException)
        {
            return (null, null, false);
        }
    }

    private static FileImportDefaultCredentials? TryDeserializeDefaults(string decrypted)
    {
        try
        {
            return JsonSerializer.Deserialize<FileImportDefaultCredentials>(decrypted);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

using System.Net;
using System.Text.Json;
using FSH.Framework.Core.Exceptions;
using FSH.Modules.Proxies.Contracts.Dtos;
using FSH.Modules.Proxies.Contracts.v1.ProviderAccounts;
using FSH.Modules.Proxies.Data;
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

        if (command.DefaultUsername is not null || command.DefaultPassword is not null)
        {
            var defaults = new FileImportDefaultCredentials(command.DefaultUsername, command.DefaultPassword);
            account.UpdateCredentials(protector.Protect(JsonSerializer.Serialize(defaults)));
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

        FileImportDefaultCredentials? storedDefaults = null;
        if (parsed.Records.Any(r => r.Username is null || r.Password is null))
        {
            storedDefaults = JsonSerializer.Deserialize<FileImportDefaultCredentials>(protector.Unprotect(account.ProtectedCredentials));
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
}

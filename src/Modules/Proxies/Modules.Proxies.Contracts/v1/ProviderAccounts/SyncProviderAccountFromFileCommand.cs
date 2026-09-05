using FSH.Modules.Proxies.Contracts.Dtos;
using Mediator;

namespace FSH.Modules.Proxies.Contracts.v1.ProviderAccounts;

public sealed record SyncProviderAccountFromFileCommand(
    Guid ProviderAccountId, string FileContent,
    string? DefaultUsername, string? DefaultPassword,
    string? DefaultGeolocation, ProxyKind? DefaultProxyKind) : ICommand<FileImportResult>;

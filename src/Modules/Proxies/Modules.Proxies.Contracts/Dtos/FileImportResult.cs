namespace FSH.Modules.Proxies.Contracts.Dtos;

public sealed record FileImportRowError(int LineNumber, string Message);

public sealed record FileImportResult(int Created, int Updated, int Retired, IReadOnlyList<FileImportRowError> Errors);

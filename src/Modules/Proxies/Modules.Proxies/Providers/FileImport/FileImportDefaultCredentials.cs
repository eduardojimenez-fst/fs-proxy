namespace FSH.Modules.Proxies.Providers.FileImport;

/// <summary>
/// Fallback Username/Password for canonical-format rows that leave those columns blank (e.g. an
/// Oxylabs export, where every proxy shares one account-wide credential entered once). Stored,
/// protected, in the same <c>ProviderAccount.ProtectedCredentials</c> field the live adapters use
/// for their own (differently-shaped) API credentials — see the design spec's "Credential fallback
/// mechanism" section for why sharing the field is safe.
/// </summary>
public sealed record FileImportDefaultCredentials(string? Username, string? Password);

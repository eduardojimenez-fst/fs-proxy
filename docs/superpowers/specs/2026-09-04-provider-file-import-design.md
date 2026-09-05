# Provider File Import — Design Spec

Status: Draft
Date: 2026-09-04
Author: Claude Sonnet 5 (with Eduardo Jimenez)
Related: `docs/superpowers/specs/2026-09-02-proxy-management-service-design.md` (parent module),
`docs/superpowers/specs/2026-09-03-provider-sync-brightdata-webshare-design.md` (introduced
`Proxy.Geolocation`/`Proxy.ProviderGrouping`, extended here)

## Context

Oxylabs' datacenter-proxy plan has no working sync API for this account — `OxylabsAdapter` calls a
documented-but-nonfunctional endpoint (`GET https://api.oxylabs.io/v1/proxies`) and the account
owner confirmed there is no real alternative. Every proxy provider, including the ones that DO have
a live API (BrightData, WebShare), also offers a plain-text/CSV export of the current proxy list
through their web portal. Manually re-typing hundreds of rows on every refresh isn't viable, and
these lists aren't "manual proxies" in the existing sense either — they're still provider-sourced,
still need periodic reconciliation as the provider's inventory changes, just via a file the admin
downloads and uploads instead of a live API call.

This spec adds file upload as a second **sync mechanism**, alongside the existing live-adapter one,
sharing the same reconciliation engine and the same `Proxy` table.

## Research: the three sample provider exports

| | BrightData (`.txt`) | Oxylabs (`.csv`) | WebShare (`.txt`) |
|---|---|---|---|
| Format | `host:port:user:pass`, one per line | Header row + comma columns | `host:port:user:pass`, one per line |
| Credentials per row | Yes, embedded in the line | **No** — same user/pass for every row, entered once in the provider's portal, not present in the file | Yes, embedded in the line |
| Geolocation | No (implicit in the zone name, e.g. `datacenter_new_proxy_manager`) | Yes — explicit `Country` column | No |
| Proxy kind | Implicit in zone/file naming ("datacenter") | Implicit in file naming ("Datacenter - Proxy lists.csv") | Not present |
| Notable trap | — | The `Associated IP` column is informational only — confirmed by the account owner it is **not** the real egress IP and must be ignored, not imported as `Proxy.Host` | — |

No two of these three share a delimiter or column set, and neither of the two providers that already
have a *working* live-API adapter (BrightData, WebShare) put geolocation or proxy-kind in their raw
export at all — so even a from-scratch parser per vendor format couldn't populate those two fields
from the file alone in 2 of 3 cases. This confirms the brief's own framing: build one **canonical**
format the platform defines, not per-vendor auto-detection. The user reshapes whatever their provider
gives them into this format before uploading (a five-minute spreadsheet operation, done rarely — a
provider's inventory doesn't change every day).

## Canonical file format

CSV, UTF-8, header row required, one data row per proxy:

```
Host,Port,Protocol,Username,Password,Geolocation,ProxyKind
```

| Column | Required | Notes |
|---|---|---|
| `Host` | Yes | IP or hostname (`brd.superproxy.io`, `dc.oxylabs.io`, `89.249.195.245`, ...). |
| `Port` | Yes | Integer. |
| `Protocol` | No | `Http` \| `Https` \| `Socks5`, case-insensitive. Defaults to `Http` when blank. |
| `Username` | No | Blank → falls back to the account's default credentials (see below). |
| `Password` | No | Same fallback as `Username`. |
| `Geolocation` | No | Free-form, ISO2 recommended (`CL`, `US`, ...) — same field the live adapters populate (`Proxy.Geolocation`), same flag-emoji rendering already shipped in the admin UI. |
| `ProxyKind` | No | `DataCenter` \| `Residential` \| `Mobile` \| `Dedicated`, case-insensitive. Blank → `null` (unclassified). |

Worked examples, converting each of the three sample files:

- **BrightData**: split each `host:port:user:pass` line into columns; `Geolocation` blank (not in
  this export); `ProxyKind=DataCenter` (the admin knows this from the zone name — typed once,
  not per-row, since every row in that file shares it; see "Per-upload defaults" below for a way to
  avoid retyping it 20 times).
- **Oxylabs**: `Entry point`→`Host`, `Port`→`Port`, `Country`→`Geolocation`; `Associated IP` column
  dropped entirely (per the account owner's note above); `Username`/`Password` left blank on every
  row, relying on the account-level default; `ProxyKind=DataCenter` for every row (the file is
  already segmented by kind, per its own filename).
- **WebShare**: split `host:port:user:pass` directly into columns; `Geolocation`/`ProxyKind` blank
  (not in this export, and the account owner hasn't confirmed this plan's proxy kind).

### Per-upload defaults (reduces repetition, solves Oxylabs' missing credentials)

The upload action itself (not the file) accepts four optional parameters applied to any row that
leaves its own column blank: `DefaultUsername`, `DefaultPassword`, `DefaultGeolocation`,
`DefaultProxyKind`. `DefaultUsername`/`DefaultPassword`, when supplied at upload time, are not
merely a one-time convenience — they get written back to `ProviderAccount.ProtectedCredentials` (see
below), so the next upload for that same account doesn't require re-entering them unless they
change. `DefaultGeolocation`/`DefaultProxyKind` are upload-time-only (not persisted): a future upload
to the same account may be a completely different batch (e.g. today all-Chile-datacenter, next
month a mixed batch), so baking those into the account itself would be wrong.

### Credential fallback mechanism

`ProviderAccount.ProtectedCredentials` already stores provider-specific JSON, decrypted once per
sync via the existing `IProxySecretProtector` — the identical mechanism `ProviderAccountSyncService`
uses today for BrightData/WebShare/Oxylabs API credentials. File-import reuses the same field and
protector with a new shape:

```csharp
namespace FSH.Modules.Proxies.Providers.FileImport;

public sealed record FileImportDefaultCredentials(string? Username, string? Password);
```

On upload: if `DefaultUsername`/`DefaultPassword` were supplied, protect+store them via
`account.UpdateCredentials(...)` before parsing; then, for every row missing its own
`Username`/`Password`, decrypt `account.ProtectedCredentials` as `FileImportDefaultCredentials` and
substitute. This is intentionally independent of whatever credentials shape a *live* adapter for the
same `ProviderType` expects (e.g. Oxylabs' Basic-auth API credentials) — file-import accounts and
live-sync accounts happen to share the `ProviderAccount` table and `ProviderType` enum, but not
necessarily the same credentials semantics, exactly as the field already varies today across
BrightData/WebShare/Oxylabs.

## Domain changes

New enum, `Modules.Proxies.Contracts/ProxyKind.cs`:

```csharp
public enum ProxyKind { DataCenter, Residential, Mobile, Dedicated }
```

`Proxy` (`Domain/Proxy.cs`) gains `public ProxyKind? Kind { get; private set; }`, following the exact
precedent `Geolocation`/`ProviderGrouping` set: a new trailing optional parameter on both
`Proxy.Create(...)` and `Proxy.UpdateConnection(...)`, mapped in `ProxyConfiguration.cs`, requiring
an EF migration (`AddColumn`, nullable, no data backfill). `ProviderProxyRecord`
(`Providers/ProviderProxyRecord.cs`) gains a matching trailing optional `ProxyKind? Kind = null`.
Propagates to `ProxyDto`, `ListProxiesQueryHandler` (new optional filter, same pattern as the
existing `Geolocation`/`Status`/`ProviderAccountId` filters), and the admin `Proxies` list
page/table (new column, filterable).

Live adapters (BrightData/WebShare/Oxylabs) keep passing `null` for `Kind` — this spec does not ask
any of them to infer it; only file-import rows populate it, since only the file format gives an
admin a place to state it explicitly.

## Reconciliation: shared with the existing sync path

`ProviderAccountSyncService.SyncAsync` (lines 70–104 today) already contains the exact upsert/retire
algorithm this feature needs — it just currently gets its `IReadOnlyList<ProviderProxyRecord>` from
`adapter.SyncProxiesAsync(...)`. Extract that block into a shared private method:

```csharp
private async Task<int> ReconcileAsync(
    ProviderAccount account, IReadOnlyList<ProviderProxyRecord> records, CancellationToken ct)
```

`SyncAsync` (live-adapter path) calls it after a successful `adapter.SyncProxiesAsync(...)`, exactly
as today, just via the extracted method. The new file-import path (below) calls the same method with
its parsed records. This is a pure refactor of the existing method — no behavior change for the
live-adapter path, verified by the existing `ProviderAccountSyncServiceTests` continuing to pass
unmodified.

### ExternalId for file rows

Live adapters supply a provider-assigned `ExternalId` (BrightData: `"{zone}:{ip}"`; WebShare: the
API's own `id`). Uploaded rows have no such identity. Confirmed with the account owner: **each
upload represents the complete current proxy list for that account** (full reconciliation — rows
missing from a new upload get retired, matching the live-adapter semantics exactly, no separate
merge-only mode). Given that, `ExternalId` for a file row is `"file:{host}:{port}"` — stable across
repeated uploads of the same (possibly reshaped) provider export, and namespaced with a `file:`
prefix so it can never collide with a live adapter's `ExternalId` scheme if the same `ProviderAccount`
ever also has a working live adapter.

## New feature: upload endpoint

```
POST /api/v1/proxies/provider-accounts/{id:guid}/sync-from-file
Content-Type: multipart/form-data
  file: the CSV
  defaultUsername, defaultPassword, defaultGeolocation, defaultProxyKind: optional form fields
```

Synchronous — parses and reconciles inline, no queued job, no persistence of the raw file (this is a
one-shot sync trigger, not a document the admin needs to re-download later; the existing
presigned-upload pipeline in `Modules.Files` is built for the latter and would be pure overhead
here — antivirus scanning a proxy list, `FileAsset` bookkeeping, an integration event nobody
consumes). Response:

```csharp
public sealed record FileImportResult(
    int Created, int Updated, int Retired, IReadOnlyList<FileImportRowError> Errors);

public sealed record FileImportRowError(int LineNumber, string Message);
```

A row-level error (missing `Host`, non-integer `Port`, unrecognized `Protocol`/`ProxyKind` value)
does not fail the whole upload — it's skipped and reported in `Errors`, and every valid row still
reconciles. An account-level error (file has no header row, zero data rows, or
`DefaultUsername`/`DefaultPassword` missing while some row also omits its own) fails the whole
request with `400`.

New files, mirroring the existing `SyncProviderAccountNow` feature folder:

- `Modules.Proxies.Contracts/v1/ProviderAccounts/SyncProviderAccountFromFileCommand.cs`
- `Features/v1/ProviderAccounts/SyncProviderAccountFromFile/{Command,CommandHandler,CommandValidator,Endpoint}.cs`
- `Providers/FileImport/{FileImportDefaultCredentials.cs,ProviderFileParser.cs}.cs` — the CSV
  parser, using the same lightweight hand-rolled parsing style already used elsewhere in this module
  (no new CSV package — the format is simple enough, and every other parsing point in this module is
  hand-rolled JSON/string work, not a library).

`ProviderFileParser` is a pure function of the file's bytes: it has no DB or crypto dependency, and
does **not** apply default-credential/geolocation/kind substitution itself (a blank `Username` stays
`null` in its output). It returns `IReadOnlyList<ProviderProxyRecord>` (blanks-as-`null`) +
`IReadOnlyList<FileImportRowError>`, keeping it trivially unit-testable against raw CSV text.

The handler owns substitution and orchestration: resolve the account, optionally persist new default
credentials (`UpdateCredentials`), call the parser, then for each parsed record with a `null`
`Username`/`Password`/`Geolocation`/`Kind` substitute the corresponding default (decrypted account
credentials for `Username`/`Password`; the request's `defaultGeolocation`/`defaultProxyKind` for the
other two), call the shared `ReconcileAsync` with the fully-substituted records, call
`account.RecordSyncResult(...)` exactly as the live path does, return `FileImportResult`.

Permission: reuses `ProxiesPermissions.ProviderAccounts.Update` (same gate as `SyncProviderAccountNow`
today) — this is an admin operator action, not a new permission surface.

## Admin UI

`provider-accounts.tsx`: a new "Upload file" action next to the existing "Sync now" button on every
row (not gated by `ProviderType` — any account can use it, matching the reasoning that file-import is
an alternative sync trigger, not a new provider type). Opens a small dialog:
file picker + four optional text inputs (default username/password/geolocation/proxy kind — proxy
kind as a `Select` populated from the fixed `ProxyKind` enum, geolocation as a plain text input
reusing the flag-emoji convention already established). On submit, shows the `FileImportResult`
summary via toast (`"12 created, 3 updated, 1 retired"`) and, if `Errors` is non-empty, an expandable
list of `line N: message` — matching the destructive/warning visual language already used elsewhere
(`ConfirmDialog`, `ErrorBand`).

`Proxies` list page: `ProxyKind` becomes a new filterable column, following the exact pattern
`Geolocation`/`Status`/`ProviderAccountId` already use on that page (a `Select` filter, a table
column, flag/badge-style rendering — no icon convention needed, just the enum label).

## Testing

- `ProviderFileParser` unit tests against literal fixtures built from the three worked-example
  reshapes above (Section "Worked examples"), plus the per-row error paths: missing `Host`,
  non-integer `Port`, unrecognized `Protocol`/`ProxyKind` (all reported per-row via
  `FileImportRowError`, not fatal — the parser has no notion of "fatal" beyond a structurally
  unreadable file, e.g. no header row).
- `ProviderAccountSyncService` gets a test proving the extracted `ReconcileAsync` behaves
  identically whether invoked from the live-adapter path or the file-import path (same
  create/update/retire outcomes for equivalent input), plus the existing adapter-path tests
  continuing to pass unmodified (proves the refactor introduced no behavior change).
- Handler tests: default-credentials persistence, per-row fallback substitution (including the
  account-level 400 when a row omits credentials and no default is configured — this is the
  handler's concern, not the parser's, since only the handler sees decrypted account state), full
  reconciliation across two sequential uploads (second upload retires rows missing from it, matching
  the confirmed semantics).
- Playwright: upload dialog happy path (result toast shows counts) and the error-list rendering when
  the mocked response includes row errors.

## Non-Goals (explicitly out of scope for this iteration)

- **Auto-detecting vendor-native formats** (BrightData/WebShare colon-lists, Oxylabs' exact CSV
  headers) — the canonical format is the only one this feature parses; per-vendor parsers are a
  separate, larger, ongoing-maintenance feature this spec deliberately avoids (see Research above).
- **Scheduled/automatic file sync** — upload is always a manual, admin-triggered action; no polling,
  no watched folder, no email-attachment ingestion.
- **Routing through `Modules.Files`' presigned-upload pipeline** — the raw file is never persisted;
  see "New feature: upload endpoint" above for why.
- **Per-provider-type gating of the upload action** — available on every `ProviderAccount`
  regardless of `ProviderType`, not a new `ProxyProviderType` enum member.
- **Inferring `ProxyKind` for existing live-adapter-synced proxies** — BrightData/WebShare/Oxylabs
  adapters keep passing `null`; retrofitting inference (e.g. from a BrightData zone name) is future
  work, not required here.

# IIS deployment — FS.Proxy

Two environments, one IIS site pair each.

| Env | App | Hostname | IIS app / pool | Server | Publish profile |
|---|---|---|---|---|---|
| Production | `FS.Proxy.Api` | `https://proxy-api.falcontenders.com` | `FS.Proxy.Api` | `fsapp1server` | `src/Host/FS.Proxy.Api/…/IIS-Prod.pubxml` |
| Production | `clients/admin` | `https://proxy.falcontenders.com` | `FS.Proxy` | `fsapp1server` | `deploy/iis/FS.Proxy.Admin.Web/…/IIS-Prod.pubxml` |
| QA | `FS.Proxy.Api` | `https://proxy-api-qa.falcontenders.com` | `FS.Proxy.Api` | `fsqa1server` | `src/Host/FS.Proxy.Api/…/IIS-QA.pubxml` |
| QA | `clients/admin` | `https://proxy-qa.falcontenders.com` | `FS.Proxy` | `fsqa1server` | `deploy/iis/FS.Proxy.Admin.Web/…/IIS-QA.pubxml` |

Full server names are `<host>.southcentralus.cloudapp.azure.com`.
`clients/dashboard` is **not** deployed in either environment. See [Known gaps](#known-gaps).

> **Every publish profile requires Visual Studio on Windows.** MSDeploy needs `msdeploy.exe`, and FTP
> is a Visual Studio web-publish method that the .NET SDK does not implement at all — there is no FTP
> target in `Microsoft.NET.Sdk.Publish`. `dotnet publish -p:PublishProfile=IIS-QA` will not deploy
> either app. From macOS/Linux, use `Folder-Test` (below) to verify the payload, then publish from VS.

## What differs between QA and Production

QA is meant to behave like Production, so everything that differs does so deliberately:

| | Production | QA |
|---|---|---|
| Environment name | `Production` | `QA` |
| Config file | `appsettings.Production.json` | `appsettings.QA.json` |
| SQL Server | `fsdb1server…azure.local` | `fsqa1server…azure.local` |
| Redis | `10.0.1.4:6379` (db 0) | `10.0.1.4:6379`, `defaultDatabase=1` |
| OTLP `service.name` | `FS.Proxy.Api` | `FS.Proxy-QA.Api` (via `web.QA.config`) |
| JWT key / issuer / audience | distinct | distinct |
| SPA runtime config | `clients/admin/config.production.json` | `clients/admin/config.qa.json` |

Rate limiting, storage provider, `disabledModules`, Scalar and the Serilog sinks are identical on
purpose — a limit or a hidden module that would bite a real user should bite in QA first.

Two isolation details worth knowing:

- **Redis is one instance shared by both environments, separated only by database index.** Without
  `defaultDatabase=1` they would share cache keys (tenant ids collide — both have `root`) and the
  same SignalR backplane, so a QA cache invalidation or realtime message could reach production
  clients. Data Protection is already isolated because `DataProtection:Store=Database` and the two
  databases are separate.
- **Each publish ships only its own `appsettings`.** Every `appsettings.*.json` lands in the build
  output, so each profile's `ExcludeFilesFromDeployment` drops the other environment's file (plus
  `appsettings.Development.json`). Otherwise the QA box would hold the production database, Redis and
  Hangfire credentials on disk, and vice versa.

## Server prerequisites

Verify these **before** the first publish on each server — most fail in ways that are hard to read
after the fact.

1. **SQL Server 2025 (17.x) or Azure SQL.** The migrations use the native `json` column type; an
   earlier engine cannot apply them. Check with `SELECT @@VERSION` against `fsdb1server` /
   `fsqa1server`.
2. **Network reachability** from the app server:
   - the environment's SQL host on `1433` (internal VNet DNS — does not resolve from a dev machine)
   - `10.0.1.4:6379` (Redis) — **required in every deployed environment**: `Program.cs` fails fast
     without it, because nothing else validates it and an empty value silently downgrades the app to
     an in-memory cache with no SignalR backplane
   - `10.0.1.4:4317` (OTLP collector, gRPC) — shared by both environments
3. **IIS features and modules**
   - .NET 10 **Hosting Bundle** (installs `AspNetCoreModuleV2`) — the API is hosted in-process
   - **URL Rewrite Module 2.1** — the admin site's `web.config` uses a `<rewrite>` section. If the
     module is missing, IIS cannot read that section and answers the **entire** site with a 500.19 —
     not just deep links — with nothing in the browser console. Check before deploying:
     ```powershell
     Get-WebGlobalModule | Where-Object Name -like "*Rewrite*"   # must return RewriteModule
     ```
     Install from <https://www.iis.net/downloads/microsoft/url-rewrite>, then `iisreset`. No
     redeploy needed afterwards — the `web.config` is already on the server.
   - **WebSocket Protocol** feature — SignalR at `/api/v1/realtime/hub` falls back to long polling
     without it
   - App pools `FS.Proxy.Api` and `FS.Proxy`: .NET CLR version **"No Managed Code"**
4. **Filesystem permissions** — the API app pool identity needs *write* access under the site's
   physical path for:
   - `Logs\` — **create this directory by hand.** Serilog creates its own log files, but ANCM does
     *not* create the directory for `stdoutLogFile`, and silently writes nothing if it is absent.
   - `wwwroot\uploads\` — the local storage provider writes uploads here.
5. **DNS + TLS certificates** bound for all four hostnames.

## Publish the API

From Visual Studio: right-click `FS.Proxy.Api` → Publish → `IIS-Prod` or `IIS-QA`.

Each profile sets `<EnvironmentName>`, which the SDK writes into the generated `web.config` as
`ASPNETCORE_ENVIRONMENT` — that is what selects `appsettings.Production.json` vs
`appsettings.QA.json`. The committed `web.config` also names `Production`, but the publish transform
overwrites it, so the profile always wins. Both profiles set `SkipExtraFilesOnServer=true` so `Logs\`
and `wwwroot\uploads\` survive a deploy.

Post-publish checks (substitute the QA hostnames as appropriate):

- `https://proxy-api.falcontenders.com/health/ready` — SQL Server and Redis both healthy
- `https://proxy-api.falcontenders.com/scalar` — API docs (the profile opens this automatically).
  QA's title reads "FS Proxy API (QA)", which is a quick way to confirm the right config loaded.
- `Logs\log-<date>.txt` exists on the server
- Traces, metrics and logs arrive at the collector under `service.name=FS.Proxy.Api` (or
  `FS.Proxy-QA.Api`), **once** each

If the site returns a **500.30**, read `Logs\stdout_*.log`. That is almost always a configuration
failure, and the stdout log is the only place the message appears. A missing connection string, Redis
or JWT signing key gives you `Missing required configuration '<key>' in <environment>.`

## Diagnosing a 500 on either site

IIS only serves the detailed error to *local* requests, which is why a browser on your machine shows
a bare 500 with an empty console. From the server itself:

```powershell
curl.exe -i -H "Host: proxy-qa.falcontenders.com" http://localhost/
```

That returns the full error page, including `Config Error` and a **Config Source** block naming the
failing section and line number.

The `500` alone is not enough — the substatus is what identifies the cause. It is in the IIS log:

```powershell
Get-Website | Select-Object Name, Id
Get-Content C:\inetpub\logs\LogFiles\W3SVC<id>\u_ex*.log -Tail 50 | Select-String " 500 "
```

`500 19` is a configuration error (a `web.config` IIS cannot parse — a missing module, a duplicate
collection entry); `500 0` is an application error, which on the static SPA site cannot happen. Note
that the SPA site has **no** stdout log: `stdoutLogEnabled` is an ASP.NET Core Module setting and
applies to the API only.

Two checks that split the problem quickly:

- Request `/config.json` directly. If even that 500s, `web.config` is not parsing and the whole site
  is down. If it serves but `/` does not, the problem is narrower (rewrite rule or default document).
- Rename `web.config` to `web.config.off` over FTP. If the site then comes up (with deep links
  broken), the `web.config` is confirmed as the cause.

Configuration parse failures are also logged under Event Viewer →
`Applications and Services Logs → Microsoft → Windows → IIS-Configuration → Operational`.

### 405 Method Not Allowed on PUT / DELETE

A `405` whose response carries `Allow: GET, HEAD, OPTIONS, TRACE` did not come from the app — that
verb list is IIS's, and ASP.NET Core would have answered with the verbs the route actually supports.
The cause is the **WebDAV** module, which claims PUT and DELETE before the ASP.NET Core Module sees
the request. The signature is distinctive: GET and POST work fine (so login, listing and the
assign/unassign endpoints are unaffected), every update and delete endpoint returns 405, and the API
logs show nothing at all because the request never reached it.

`src/Host/FS.Proxy.Api/web.config` removes both the module and the handler, so a redeploy fixes it.
Check whether a server has WebDAV at all with:

```powershell
Get-WebGlobalModule | Where-Object Name -like "*WebDAV*"
```

If WebDAV is not installed the `<remove>` entries are inert, so the same `web.config` is correct on
every server. Uninstalling the "WebDAV Publishing" Windows feature is an equivalent server-side fix,
but the `web.config` route is preferred: it travels with the deployment instead of relying on each
box being configured the same way.

## Publish the admin SPA

From Visual Studio: right-click `FS.Proxy.Admin.Web` → Publish → `IIS-Prod` or `IIS-QA`.

`FS.Proxy.Admin.Web` is a shim project — it contains no code. Its only job is to give Visual Studio
something FTP-publishable: on publish it runs `npm run build` in `clients/admin`, copies the profile's
`AdminRuntimeConfig` over `dist/config.json`, and hands the resulting static files to the FTP
transport. **`npm` must be on `PATH`** for whoever runs the publish.

`AdminRuntimeConfig` is the one property that must differ between the profiles, and getting it wrong
is silent — the SPA would come up on the QA host talking to production. There is deliberately **no
default**: a profile that does not set it fails the build rather than guessing an environment.

`DeleteExistingFiles` is `false` on purpose: Vite fingerprints every asset, so leaving the previous
build's chunks in place is what lets an in-flight session finish instead of 404ing on a chunk that
just disappeared.

Verify the payload first (works on any platform, no FTP involved):

```bash
# Production payload
dotnet publish deploy/iis/FS.Proxy.Admin.Web -p:PublishProfile=Folder-Test

# QA payload
dotnet publish deploy/iis/FS.Proxy.Admin.Web -p:PublishProfile=Folder-Test \
  -p:AdminRuntimeConfig=config.qa.json

ls deploy/iis/FS.Proxy.Admin.Web/bin/Release/publish-test
```

That directory must hold `index.html`, `assets/`, `web.config` and a `config.json` whose `apiBase`
matches the environment you asked for — and no `.dll`, `.deps.json` or `.runtimeconfig.json`.

Post-publish checks in the browser:

- `/config.json` responds with the expected `apiBase` (and `Cache-Control: no-cache`) — **check this
  first**, it is the one mistake that makes a QA site quietly drive production
- Sign in — this is what exercises CORS end to end, including the `tenant` request header
- Open a deep link such as `/tenants/root` and reload — validates the SPA rewrite rule
- The realtime indicator connects — validates the SignalR negotiate headers in the CORS allow-list

### If FTP publish fails

Visual Studio's FTP client is `FtpWebRequest`, which does not do TLS session reuse on the data
channel. IIS FTP sites that require it will accept the login and then fail every transfer. Either
turn that requirement off on the FTP site, or upload
`deploy/iis/FS.Proxy.Admin.Web/bin/Release/publish-test/` with an FTPS-capable client
(FileZilla, WinSCP) into the `FS.Proxy` folder.

## Migrate the database

The database is **not** migrated when the API starts — it is a separate step, and it has to run from
inside the VNet, because both SQL hosts are internal DNS that does not resolve from a dev machine.

```bash
# On a machine that can build (framework-dependent; needs the .NET 10 runtime on the target)
dotnet publish src/Host/FS.Proxy.DbMigrator -c Release -r win-x64 --self-contained false
```

Copy the output to the target app server, then:

```powershell
$env:DOTNET_ENVIRONMENT = "QA"          # or "Production"
.\FS.Proxy.DbMigrator.exe list-pending  # inspect first
.\FS.Proxy.DbMigrator.exe apply --seed  # apply + seed the root tenant admin
```

`FS.Proxy.DbMigrator.csproj` links the API's `appsettings.Production.json` **and**
`appsettings.QA.json`, so the migrator reads the same connection string and
`Seed:DefaultAdminPassword` the API is configured with. Without those links it would silently fall
back to the base `appsettings.json` — the localhost dev connection string — and migrate the wrong
database.

The trade-off is that the migrator's publish output carries **both** environment files, so unlike the
API publish it has no per-profile exclusion. Drop the one you are not deploying before copying it to
a server, so a QA box never holds production credentials:

```powershell
# from the publish output, before copying to the QA server
Remove-Item .\appsettings.Production.json, .\appsettings.Development.json
```

### What `--seed` does and does not create

In a deployed environment `--seed` creates the root tenant and its admin user, the **Manual** proxy
provider account, and the reference tag categories. Seeing the tag categories in the UI is the quick
confirmation that the seed ran to completion.

It does **not** create the BrightData or WebShare provider accounts. That is deliberate, not a
deployment problem: `ProxiesDbInitializer.SeedAsync` gates them behind `environment.IsDevelopment()`,
and their credentials come from `dotnet user-secrets`, which the migrator only loads in Development —
the point being that third-party API keys never enter source control. The seeded accounts are even
named "(dev seed)".

So in QA and Production, add them through the UI once per environment:
**Proxies → Provider Accounts** (`/proxies/provider-accounts`). See
[Provider account credentials](#provider-account-credentials) for the exact JSON each one expects.

## Provider account credentials

The **Credentials (JSON)** field in the Provider Account dialog takes one JSON object whose shape
depends on the provider. The API encrypts it with `IProxySecretProtector` before storing it in
`ProviderAccount.ProtectedCredentials`, and the value is never returned by any read endpoint — the
edit dialog leaves the field blank and only overwrites when you type something.

**Nothing validates the shape on save.** `CreateProviderAccountCommandValidator` only checks that the
string is non-empty, so a wrong key name is accepted happily and only surfaces later as a failed sync.
Property matching is case-insensitive, so `apiKey` and `ApiKey` both bind; the examples below use the
camelCase the dialog's placeholder shows.

### WebShare

```json
{ "apiKey": "<api key>" }
```

Authenticates as `Authorization: Token <apiKey>`. Sync is supported; per-proxy renew is not.

### BrightData

```json
{
  "apiToken": "<api token>",
  "zone": "<zone name>",
  "customerId": "<customer id>",
  "gatewayPort": 44445,
  "gatewayHost": "brd.superproxy.io"
}
```

`gatewayHost` is optional and defaults to `brd.superproxy.io`; the other four are required.
`gatewayPort` is a **number**, not a string. Sync is supported; per-proxy renew is not.

### Oxylabs

Oxylabs has no API key — it authenticates with HTTP Basic using the account username and password:

```json
{ "username": "<account username>", "password": "<account password>" }
```

In practice this account is usually fed by **file import** rather than the API sync, and that path
uses the same two fields for a different purpose: they are the *fallback* credentials applied to rows
in the uploaded file that leave the username/password columns blank (an Oxylabs export shares one
account-wide credential across every proxy). You do not have to type the JSON for that — pass
`defaultUsername` / `defaultPassword` once on an upload and the handler stores them in the same
field, in the same shape.

Because both uses share `ProtectedCredentials`, the file-import handler refuses to overwrite
credentials that are not in the username/password shape: if an account was set up for API sync with
a BrightData- or WebShare-shaped object, an upload carrying defaults fails with "already has
credentials configured that are not file-import default-username/password style". Clear the
account's credentials first if you mean to switch it to file-based sync.

## Configuration notes

Secrets live in the per-environment `appsettings.<Env>.json`, committed. A few things are
load-bearing and easy to break:

- **Configuration arrays merge by index, they do not replace.** `AllowedOrigins: ["https://a"]` in an
  environment file does not remove a second origin declared in the base `appsettings.json` — it only
  overwrites index 0. That is why the base file keeps `CorsOptions.AllowedOrigins` empty, with the dev
  origins in `appsettings.Development.json`.
- **`CorsOptions.AllowedHeaders` must cover what the clients actually send**: `tenant` (every
  tenant-scoped request), `idempotency-key`, and `x-requested-with` + `x-signalr-user-agent` (the
  SignalR JS client's negotiate call). Drop one and the corresponding feature fails in preflight, not
  at the endpoint.
- **Origins are compared as exact scheme+host+port strings.** A trailing slash never matches.
- **`HangfireOptions.UserName`/`Password` are `[Required]` with `ValidateOnStart`** (password ≥ 12
  chars). They are *not* in the explicit fail-fast list in `Program.cs`, so leaving them blank shows
  up as an opaque startup crash rather than a helpful message.
- **Options `ValidateOnStart` runs later than you would expect** — at host start, i.e. *after* the
  middleware pipeline is built. Hangfire opens its SQL connection while that pipeline is being
  configured, so a bad connection string surfaces as a connection-pool timeout from inside Hangfire
  rather than as a validation error. That is why `Program.cs` has an explicit fail-fast block for the
  three settings with no safe default, and why it covers every non-Development environment.
- **The OTLP log sink is added in code**, not declared in `Serilog:WriteTo`, whenever
  `OpenTelemetryOptions:Exporter:Otlp:Enabled` is true with an endpoint. Declaring it in the Serilog
  section as well ships every log record twice.
- **OTLP `service.name` comes from `OTEL_SERVICE_NAME`, falling back to the assembly name** — which
  is `FS.Proxy.Api` in every environment. `src/Host/FS.Proxy.Api/web.QA.config` is an XDT transform
  (the SDK applies `web.<EnvironmentName>.config` automatically) that sets `OTEL_SERVICE_NAME` so QA
  and Production do not collapse into one resource in the shared collector. It is excluded from the
  publish payload by the csproj.
- **Hangfire stores jobs in SQL Server**, on the same connection string as EF Core — so the queue is
  isolated per environment. Redis is the distributed cache and the SignalR backplane only.
- **Data Protection keys go to the database** (`DataProtection:Store=Database`), so the API and the
  DbMigrator do not need to share a Redis instance, and QA/Production keys never mix.

## Known gaps

Carried over deliberately — none of these block a deploy, but they are all live limitations, and
they apply to both environments unless noted.

- **No SMTP credentials.** `MailOptions.SMTP` is blank, so forgot-password, tenant invitations and
  email notifications do not send. When configuring it, note that the reset link is built as
  `{OriginOptions.OriginUrl}/reset-password`, and `OriginUrl` points at the API host (it also
  absolutizes profile-image URLs served from `wwwroot`). Pointing it at the SPA fixes the email link
  and breaks the images; the alternative is an IIS rewrite on the API for `/reset-password`.
- **The dashboard is not deployed.** `clients/admin`'s `dashboardUrl` is a placeholder in both
  environments, so the operator → tenant impersonation handoff has nowhere to land.
- **The Hangfire dashboard at `/jobs` is internet-reachable**, mounted ahead of the authentication
  pipeline and protected only by its basic-auth credentials. Restrict it with IIS "IP and Domain
  Restrictions" if that is not acceptable.
- **Scalar is public** at `/scalar` — `OpenApiOptions.Enabled` is `true`, so the full endpoint
  catalogue is browsable without authentication.
- **Invoice PDF downloads lose their filename cross-origin.** The framework does not expose
  `Content-Disposition` via `WithExposedHeaders`, and `CorsOptions` has no setting for it; fixing it
  means changing `BuildingBlocks`, which is protected.
- **Uploads over 30 MB fail** even though the `Archive` file category allows 50 MB. The IIS limit is
  raised in `web.config`, but ASP.NET Core's own 30 MB request body limit is not.
- **`UseHttpsRedirection` and HSTS are active with no `ForwardedHeaders` configuration.** Correct
  while IIS terminates TLS itself (in-process hosting preserves the request scheme). Putting ARR or a
  load balancer in front without wiring forwarded headers will cause redirect loops.
- **Uploads live under the site directory** (`wwwroot\uploads`). They survive publishes thanks to
  `SkipExtraFilesOnServer=true`, but moving `Storage:Provider` to `s3`/MinIO is the durable answer.
- **QA and Production share one Redis instance and one OTLP collector.** Separated by database index
  and `service.name` respectively, which is a convention, not an enforced boundary.

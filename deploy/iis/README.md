# IIS deployment — FS.Proxy

Two IIS sites on `fsapp1server.southcentralus.cloudapp.azure.com`:

| App | Hostname | IIS app / pool | Publish method | Profile |
|---|---|---|---|---|
| `FS.Proxy.Api` | `https://proxy-api.falcontenders.com` | `FS.Proxy.Api` | MSDeploy (WMSVC) | `src/Host/FS.Proxy.Api/Properties/PublishProfiles/IIS-Prod.pubxml` |
| `clients/admin` | `https://proxy.falcontenders.com` | `FS.Proxy` | FTP over explicit TLS | `deploy/iis/FS.Proxy.Admin.Web/Properties/PublishProfiles/IIS-Prod.pubxml` |

`clients/dashboard` is **not** deployed. See [Known gaps](#known-gaps).

> **Both publish profiles require Visual Studio on Windows.** MSDeploy needs `msdeploy.exe`, and FTP
> is a Visual Studio web-publish method that the .NET SDK does not implement at all — there is no FTP
> target in `Microsoft.NET.Sdk.Publish`. `dotnet publish -p:PublishProfile=IIS-Prod` will not deploy
> either app. From macOS/Linux, use `Folder-Test` (below) to verify the payload, then publish from VS.

## Server prerequisites

Verify these **before** the first publish — most of them fail in ways that are hard to read after the fact.

1. **SQL Server 2025 (17.x) or Azure SQL.** The migrations use the native `json` column type; an
   earlier engine cannot apply them. Check with `SELECT @@VERSION` against
   `fsdb1server.southcentralus.cloudapp.azure.local`.
2. **Network reachability** from the app server:
   - `fsdb1server.southcentralus.cloudapp.azure.local:1433` (SQL — internal VNet DNS)
   - `10.0.1.4:6379` (Redis) — **required in Production**: `Program.cs` fails fast without it
   - `10.0.1.4:4317` (OTLP collector, gRPC)
3. **IIS features and modules**
   - .NET 10 **Hosting Bundle** (installs `AspNetCoreModuleV2`) — the API is hosted in-process
   - **URL Rewrite Module 2.1** — the admin site's `web.config` uses a `<rewrite>` section, and IIS
     answers the whole site with a 500.19 if the module is missing
   - **WebSocket Protocol** feature — SignalR at `/api/v1/realtime/hub` falls back to long polling
     without it
   - App pool `FS.Proxy.Api`: .NET CLR version **"No Managed Code"**
   - App pool `FS.Proxy`: static site, also "No Managed Code"
4. **Filesystem permissions** — the API app pool identity needs *write* access under the site's
   physical path for:
   - `Logs\` — **create this directory by hand.** Serilog creates its own log files, but ANCM does
     *not* create the directory for `stdoutLogFile`, and silently writes nothing if it is absent.
   - `wwwroot\uploads\` — the local storage provider writes uploads here.
5. **DNS + TLS certificates** bound for `proxy-api.falcontenders.com` and `proxy.falcontenders.com`.

## Publish the API

From Visual Studio: right-click `FS.Proxy.Api` → Publish → `IIS-Prod`.

The profile sets `EnvironmentName=Production` (so ANCM gets `ASPNETCORE_ENVIRONMENT=Production`),
`SkipExtraFilesOnServer=true` (so `Logs\` and `wwwroot\uploads\` survive a deploy) and excludes
`appsettings.Development.json` from the payload.

Post-publish checks:

- `https://proxy-api.falcontenders.com/health/ready` — SQL Server and Redis both healthy
- `https://proxy-api.falcontenders.com/scalar` — API docs (the profile opens this automatically)
- `Logs\log-<date>.txt` exists on the server
- Traces, metrics and logs arrive at the collector under `service.name=FS.Proxy.Api`, **once** each

If the site returns a **500.30**, read `Logs\stdout_*.log`: that is almost always an options
validation failure (a missing or too-short `JwtOptions:SigningKey`, an empty Hangfire credential, an
unreachable database) and the stdout log is the only place the message appears.

## Publish the admin SPA

From Visual Studio: right-click `FS.Proxy.Admin.Web` → Publish → `IIS-Prod`.

`FS.Proxy.Admin.Web` is a shim project — it contains no code. On publish it runs `npm run build` in
`clients/admin`, copies `clients/admin/config.production.json` over `dist/config.json`, and hands the
resulting static files to the FTP transport. **`npm` must be on `PATH`** for whoever runs the publish.

`DeleteExistingFiles` is `false` on purpose: Vite fingerprints every asset, so leaving the previous
build's chunks in place is what lets an in-flight session finish instead of 404ing on a chunk that
just disappeared.

Verify the payload first (works on any platform, no FTP involved):

```bash
dotnet publish deploy/iis/FS.Proxy.Admin.Web -p:PublishProfile=Folder-Test
ls deploy/iis/FS.Proxy.Admin.Web/bin/Release/publish-test
```

That directory must hold `index.html`, `assets/`, `web.config` and a `config.json` whose `apiBase` is
`https://proxy-api.falcontenders.com` — and no `.dll`, `.deps.json` or `.runtimeconfig.json`.

Post-publish checks in the browser:

- `/config.json` responds with the production `apiBase` (and `Cache-Control: no-cache`)
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
inside the VNet, because `fsdb1server...azure.local` is internal DNS that does not resolve from a dev
machine.

```bash
# On a machine that can build (publishes framework-dependent; needs the .NET 10 runtime on the target)
dotnet publish src/Host/FS.Proxy.DbMigrator -c Release -r win-x64 --self-contained false
```

Copy the output to the app server, then:

```powershell
$env:DOTNET_ENVIRONMENT = "Production"
.\FS.Proxy.DbMigrator.exe list-pending     # inspect first
.\FS.Proxy.DbMigrator.exe apply --seed     # apply + seed the root tenant admin
```

`FS.Proxy.DbMigrator.csproj` links the API's `appsettings.Production.json`, so the migrator reads the
same connection string and the same `Seed:DefaultAdminPassword` the API is configured with. Without
that link it would silently fall back to the base `appsettings.json` — the localhost dev connection
string — and migrate the wrong database.

## Configuration notes

Everything lives in `src/Host/FS.Proxy.Api/appsettings.Production.json`, secrets included. A few
things there are load-bearing and easy to break:

- **Configuration arrays merge by index, they do not replace.** `AllowedOrigins: ["https://a"]` in
  Production does not remove a second origin declared in the base `appsettings.json` — it only
  overwrites index 0. That is why the base file keeps `CorsOptions.AllowedOrigins` empty, with the dev
  origins in `appsettings.Development.json`.
- **`CorsOptions.AllowedHeaders` must cover what the clients actually send**: `tenant` (every
  tenant-scoped request), `idempotency-key`, and `x-requested-with` + `x-signalr-user-agent` (the
  SignalR JS client's negotiate call). Drop one and the corresponding feature fails in preflight, not
  at the endpoint.
- **Origins are compared as exact scheme+host+port strings.** A trailing slash never matches.
- **`HangfireOptions.UserName`/`Password` are `[Required]` with `ValidateOnStart`** (password ≥ 12
  chars). They are not part of the explicit fail-fast list in `Program.cs`, so leaving them blank
  shows up as an opaque startup crash rather than a helpful message.
- **The OTLP log sink is added in code**, not declared in `Serilog:WriteTo`, whenever
  `OpenTelemetryOptions:Exporter:Otlp:Enabled` is true with an endpoint. Declaring it in the Serilog
  section as well ships every log record twice.
- **Hangfire stores jobs in SQL Server**, on the same connection string as EF Core. Redis is the
  distributed cache and the SignalR backplane only.
- **Data Protection keys go to the database** (`DataProtection:Store=Database`), so the API and the
  DbMigrator do not need to share a Redis instance.

## Known gaps

Carried over deliberately — none of these block the deploy, but they are all live limitations.

- **No SMTP credentials.** `MailOptions.SMTP` is blank, so forgot-password, tenant invitations and
  email notifications do not send. When configuring it, note that the reset link is built as
  `{OriginOptions.OriginUrl}/reset-password`, and `OriginUrl` points at the API host (it also
  absolutizes profile-image URLs served from `wwwroot`). Pointing it at the SPA fixes the email link
  and breaks the images; the alternative is an IIS rewrite on the API for `/reset-password`.
- **The dashboard is not deployed.** `clients/admin`'s `dashboardUrl` is a placeholder, so the
  operator → tenant impersonation handoff has nowhere to land.
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

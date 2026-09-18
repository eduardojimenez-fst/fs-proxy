# Provider Rotating Gateways Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Let an operator register a provider's rotating gateway endpoint (e.g. `p.webshare.io:80` with a `-US-rotate` username) as a first-class proxy that stays attached to its real provider account.

**Architecture:** A rotating gateway is a `Proxy` row discriminated by a new `ProxyEndpointType`, attached to the real `ProviderAccount` rather than to the well-known Manual account, with `ExternalId = null` so provider sync leaves it alone. Leasing is unchanged and type-blind — routing is the operator's job, done with tags. The auto-disable policy engine skips gateways entirely; a dedicated health-check rule disables one only on repeated endpoint failures, never on consumer-reported IP bans.

**Tech Stack:** .NET 10, EF Core 10, Mediator 3.x (source-gen), FluentValidation 12.x, xUnit + Shouldly + NSubstitute + EF InMemory, React 19 + TanStack Query v5 + react-hook-form + zod, Playwright.

**Spec:** `docs/superpowers/specs/2026-09-18-rotating-gateways-design.md`

## Global Constraints

- Mediator handlers are `public sealed`, return `ValueTask<T>`, and `.ConfigureAwait(false)` every await.
- Every command handler and paginated query handler needs a `{Name}Validator`. `Architecture.Tests` enforces this.
- Propagate `CancellationToken` into every EF/IO call.
- Structured logging only — message templates, never string interpolation.
- File-scoped namespaces, 4-space indent, explicit types (`var` only when the RHS is obvious), `is null` / `is not null`, records for DTOs and commands.
- Build runs with `TreatWarningsAsErrors` — warnings fail the build.
- Do NOT modify `src/BuildingBlocks`. Nothing in this plan needs to.
- A module's entities are all `IGlobalEntity` in Proxies; no tenant filter applies.
- Frontend: pass per-call data through `mutate(arg)`, never through state the mutation callbacks close over.
- `EndpointType` is set at creation and never modified. It must NOT be added to `Proxy.UpdateConnection`.
- Migrations are added to **both** `FS.Proxy.Migrations.MSSQL` and `FS.Proxy.Migrations.PostgreSQL`.

---

### Task 1: Domain model, persistence and migrations

**Files:**
- Create: `src/Modules/Proxies/Modules.Proxies.Contracts/ProxyEndpointType.cs`
- Modify: `src/Modules/Proxies/Modules.Proxies/Domain/Proxy.cs`
- Modify: `src/Modules/Proxies/Modules.Proxies/Data/Configurations/ProxyConfiguration.cs`
- Create: migration in `src/Host/FS.Proxy.Migrations.PostgreSQL/Proxies/`
- Create: migration in `src/Host/FS.Proxy.Migrations.MSSQL/Proxies/`
- Test: `src/Tests/Proxies.Tests/Domain/ProxyTests.cs`

**Interfaces:**
- Consumes: nothing.
- Produces: `FSH.Modules.Proxies.Contracts.ProxyEndpointType` (`Individual`, `RotatingGateway`); `Proxy.EndpointType { get; private set; }`; `static Proxy Proxy.CreateRotatingGateway(Guid providerAccountId, string host, int port, ProxyProtocol protocol, string? username, string? protectedPassword, string? geolocation, string? providerGrouping, ProxyKind? kind)`.

- [ ] **Step 1: Write the failing tests**

Append to `src/Tests/Proxies.Tests/Domain/ProxyTests.cs` (inside the existing `ProxyTests` class):

```csharp
[Fact]
public void CreateRotatingGateway_Should_SetEndpointType_And_LeaveExternalIdNull()
{
    var accountId = Guid.CreateVersion7();

    var gateway = Proxy.CreateRotatingGateway(
        accountId, "p.webshare.io", 80, ProxyProtocol.Http,
        "jgwcycpg-US-rotate", "protected", "US", "webshare-rotate", ProxyKind.Residential);

    gateway.EndpointType.ShouldBe(ProxyEndpointType.RotatingGateway);
    gateway.ProviderAccountId.ShouldBe(accountId);
    gateway.ExternalId.ShouldBeNull();
    gateway.Status.ShouldBe(ProxyStatus.Testing);
    gateway.Geolocation.ShouldBe("US");
    gateway.ProviderGrouping.ShouldBe("webshare-rotate");
    gateway.Kind.ShouldBe(ProxyKind.Residential);
}

[Fact]
public void Create_Should_ProduceIndividualEndpointType()
{
    var proxy = Proxy.Create(
        Guid.CreateVersion7(), "10.0.0.5", 3128, ProxyProtocol.Http, null, null, null);

    proxy.EndpointType.ShouldBe(ProxyEndpointType.Individual);
}

[Fact]
public void UpdateConnection_Should_NotChangeEndpointType()
{
    var gateway = Proxy.CreateRotatingGateway(
        Guid.CreateVersion7(), "p.webshare.io", 80, ProxyProtocol.Http,
        "jgwcycpg-US-rotate", "protected", "US", null, null);

    gateway.UpdateConnection("p.webshare.io", 80, ProxyProtocol.Http, "jgwcycpg-DE-rotate", "protected", "DE", null, null);

    gateway.EndpointType.ShouldBe(ProxyEndpointType.RotatingGateway);
    gateway.Username.ShouldBe("jgwcycpg-DE-rotate");
}
```

Make sure the file's `using` block contains `using FSH.Modules.Proxies.Contracts;` and `using FSH.Modules.Proxies.Domain;` (it already does — verify rather than duplicate).

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test src/Tests/Proxies.Tests --filter "FullyQualifiedName~ProxyTests"`
Expected: FAIL — `ProxyEndpointType` and `CreateRotatingGateway` do not exist (compile error).

- [ ] **Step 3: Add the enum**

Create `src/Modules/Proxies/Modules.Proxies.Contracts/ProxyEndpointType.cs`:

```csharp
namespace FSH.Modules.Proxies.Contracts;

/// <summary>
/// What kind of endpoint a <c>Proxy</c> row represents. <see cref="Individual"/> is one proxy with
/// one (possibly rotating-on-renewal) egress IP. <see cref="RotatingGateway"/> is a provider-side
/// virtual endpoint that rotates across the proxies already contracted with that provider on every
/// request — one durable host/port whose exit IP changes underneath it, with no per-IP listing to
/// sync.
/// </summary>
public enum ProxyEndpointType { Individual, RotatingGateway }
```

- [ ] **Step 4: Add the property and factory to `Proxy`**

In `src/Modules/Proxies/Modules.Proxies/Domain/Proxy.cs`, add the property after `Kind`:

```csharp
    public ProxyEndpointType EndpointType { get; private set; }
```

Then add this factory immediately after the existing `Create` method:

```csharp
    /// <summary>
    /// Registers a provider's rotating gateway: one durable endpoint the provider rotates across
    /// the proxies already contracted on <paramref name="providerAccountId"/>. It carries no
    /// <c>ExternalId</c> because the provider exposes no per-gateway listing — which is also what
    /// keeps <c>ProviderAccountSyncService.ReconcileAsync</c> (whose query filters on
    /// <c>ExternalId != null</c>) from updating or retiring it.
    /// </summary>
    /// <remarks>
    /// A separate factory rather than an eleventh parameter on <see cref="Create"/>: every one of
    /// that method's four call sites would otherwise have to name a value for an axis it does not
    /// care about. <c>EndpointType</c> is deliberately absent from <see cref="UpdateConnection"/>
    /// — a proxy never changes its nature, and leaving it out means no edit or renewal path can
    /// demote a gateway by omission (the failure mode that comment documents).
    /// </remarks>
    public static Proxy CreateRotatingGateway(
        Guid providerAccountId, string host, int port, ProxyProtocol protocol,
        string? username, string? protectedPassword,
        string? geolocation, string? providerGrouping, ProxyKind? kind)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(host);
        return new Proxy
        {
            Id = Guid.CreateVersion7(),
            ProviderAccountId = providerAccountId,
            Host = host.Trim(),
            Port = port,
            Protocol = protocol,
            Username = username,
            ProtectedPassword = protectedPassword,
            ExternalId = null,
            Geolocation = geolocation,
            ProviderGrouping = providerGrouping,
            Kind = kind,
            EndpointType = ProxyEndpointType.RotatingGateway,
            Status = ProxyStatus.Testing,
            CreatedAtUtc = DateTime.UtcNow
        };
    }
```

`Proxy.Create` needs no change: `EndpointType` defaults to `Individual` (enum value 0).

- [ ] **Step 5: Run the domain tests to verify they pass**

Run: `dotnet test src/Tests/Proxies.Tests --filter "FullyQualifiedName~ProxyTests"`
Expected: PASS

- [ ] **Step 6: Map the column**

In `src/Modules/Proxies/Modules.Proxies/Data/Configurations/ProxyConfiguration.cs`, add after the `ProviderGrouping` property line:

```csharp
        builder.Property(x => x.EndpointType).IsRequired().HasDefaultValue(ProxyEndpointType.Individual);
```

Add `using FSH.Modules.Proxies.Contracts;` to the file's usings.

- [ ] **Step 7: Build before generating migrations**

Run: `dotnet build src/FS.Proxy.slnx`
Expected: build succeeds. `dotnet ef migrations add` reads a snapshot regenerated from the build — generating against a stale one silently drops the column.

- [ ] **Step 8: Generate the PostgreSQL migration**

```bash
dotnet tool restore
dotnet ef migrations add AddProxyEndpointType \
  --project src/Host/FS.Proxy.Migrations.PostgreSQL \
  --startup-project src/Host/FS.Proxy.Api \
  --context ProxiesDbContext \
  --output-dir Proxies
```

- [ ] **Step 9: Generate the SQL Server migration**

Needs a reachable SQL Server 2025. Substitute the real SA password from `src/Host/FS.Proxy.Api/appsettings.Development.json`:

```bash
DatabaseOptions__Provider=MSSQL \
DatabaseOptions__MigrationsAssembly=FS.Proxy.Migrations.MSSQL \
DatabaseOptions__ConnectionString='Server=localhost,1433;Database=fsh;User Id=sa;Password=<sa-password>;TrustServerCertificate=True' \
dotnet ef migrations add AddProxyEndpointType \
  --project src/Host/FS.Proxy.Migrations.MSSQL \
  --startup-project src/Host/FS.Proxy.Api \
  --context ProxiesDbContext \
  --output-dir Proxies
```

- [ ] **Step 10: Verify neither provider has drifted**

Run: `dotnet test src/Tests/Integration.Tests --filter "FullyQualifiedName~MigrationDriftTests"`
Expected: PASS. Inspect both generated `AddProxyEndpointType.cs` files and confirm each adds an `EndpointType` integer column, NOT NULL, default `0`.

- [ ] **Step 11: Commit**

```bash
git add src/Modules/Proxies src/Host/FS.Proxy.Migrations.PostgreSQL src/Host/FS.Proxy.Migrations.MSSQL src/Tests/Proxies.Tests
git commit -m "feat(proxies): add ProxyEndpointType discriminator to Proxy"
```

---

### Task 2: Permissions and the create endpoint

**Files:**
- Modify: `src/Modules/Proxies/Modules.Proxies.Contracts/Authorization/ProxiesPermissions.cs`
- Create: `src/Modules/Proxies/Modules.Proxies.Contracts/v1/RotatingGateways/CreateRotatingGatewayCommand.cs`
- Create: `src/Modules/Proxies/Modules.Proxies/Features/v1/RotatingGateways/CreateRotatingGateway/CreateRotatingGatewayCommandValidator.cs`
- Create: `src/Modules/Proxies/Modules.Proxies/Features/v1/RotatingGateways/CreateRotatingGateway/CreateRotatingGatewayCommandHandler.cs`
- Create: `src/Modules/Proxies/Modules.Proxies/Features/v1/RotatingGateways/CreateRotatingGateway/CreateRotatingGatewayEndpoint.cs`
- Modify: `src/Modules/Proxies/Modules.Proxies/ProxiesModule.cs`
- Test: `src/Tests/Proxies.Tests/Handlers/RotatingGatewayHandlerTests.cs` (create)
- Test: `src/Tests/Proxies.Tests/Validators/RotatingGatewayValidatorTests.cs` (create)

**Interfaces:**
- Consumes: `ProxyEndpointType`, `Proxy.CreateRotatingGateway` (Task 1); `CreateManualProxyCommandHandler.ResolveTagIdsAsync(ProxiesDbContext, IReadOnlyList<string>, CancellationToken)` — an existing `internal static` helper reused as-is.
- Produces: `CreateRotatingGatewayCommand(Guid ProviderAccountId, string Host, int Port, ProxyProtocol Protocol, string? Username, string? PlaintextPassword, string? Geolocation, string? ProviderGrouping, ProxyKind? Kind, IReadOnlyList<string> TagNames) : ICommand<Guid>`; `ProxiesPermissions.RotatingGateways.{View,Create,Update,Delete}`; route `POST /api/v1/proxies/rotating-gateways`.

- [ ] **Step 1: Write the failing handler tests**

Create `src/Tests/Proxies.Tests/Handlers/RotatingGatewayHandlerTests.cs`:

```csharp
using FSH.Framework.Core.Exceptions;
using FSH.Modules.Proxies.Contracts;
using FSH.Modules.Proxies.Contracts.v1.RotatingGateways;
using FSH.Modules.Proxies.Data;
using FSH.Modules.Proxies.Domain;
using FSH.Modules.Proxies.Features.v1.RotatingGateways.CreateRotatingGateway;
using FSH.Modules.Proxies.Services;
using Microsoft.EntityFrameworkCore;
using Shouldly;
using Xunit;

namespace Proxies.Tests.Handlers;

public sealed class RotatingGatewayHandlerTests
{
    private static ProxiesDbContext CreateDb() =>
        TestProxiesDbContext.Create(new DbContextOptionsBuilder<ProxiesDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    private static async Task<ProviderAccount> SeedWebShareAccountAsync(ProxiesDbContext db)
    {
        var account = ProviderAccount.Create("WebShare", ProxyProviderType.WebShare, "protected-creds");
        db.ProviderAccounts.Add(account);
        await db.SaveChangesAsync();
        return account;
    }

    private static CreateRotatingGatewayCommand CommandFor(Guid accountId, string username = "jgwcycpg-US-rotate") =>
        new(accountId, "p.webshare.io", 80, ProxyProtocol.Http, username, "gateway-pass",
            "US", "webshare-rotate", ProxyKind.Residential, ["pais:us", "rotativo"]);

    [Fact]
    public async Task Create_Should_AttachToTheProviderAccount_AsARotatingGateway()
    {
        await using var db = CreateDb();
        var account = await SeedWebShareAccountAsync(db);
        var sut = new CreateRotatingGatewayCommandHandler(db, new FakeProviderProtector());

        var id = await sut.Handle(CommandFor(account.Id), CancellationToken.None);

        var stored = await db.Proxies.Include(x => x.TagAssignments).SingleAsync(x => x.Id == id);
        stored.ProviderAccountId.ShouldBe(account.Id);
        stored.EndpointType.ShouldBe(ProxyEndpointType.RotatingGateway);
        stored.ExternalId.ShouldBeNull();
        stored.TagAssignments.Count.ShouldBe(2);
    }

    // ProxyPasswordResolver picks its protector by ProviderAccountId: anything that is not the
    // Manual account is decrypted with "provider-account". A gateway hangs off a real provider
    // account, so it must be ENCRYPTED with that same protector — using the "proxy-password" one
    // that CreateManualProxyCommandHandler uses would produce a password that fails to decrypt at
    // lease time rather than an error at creation time.
    [Fact]
    public async Task Create_Should_EncryptWithTheProviderAccountProtector()
    {
        await using var db = CreateDb();
        var account = await SeedWebShareAccountAsync(db);
        var protector = new FakeProviderProtector();
        var sut = new CreateRotatingGatewayCommandHandler(db, protector);

        var id = await sut.Handle(CommandFor(account.Id), CancellationToken.None);

        var stored = await db.Proxies.SingleAsync(x => x.Id == id);
        stored.ProtectedPassword.ShouldBe(protector.Protect("gateway-pass"));
    }

    [Fact]
    public async Task Create_Should_Throw_When_ProviderAccountDoesNotExist()
    {
        await using var db = CreateDb();
        var sut = new CreateRotatingGatewayCommandHandler(db, new FakeProviderProtector());

        await Should.ThrowAsync<NotFoundException>(
            () => sut.Handle(CommandFor(Guid.CreateVersion7()), CancellationToken.None).AsTask());
    }

    // Two gateways of the same provider legitimately share host:port (WebShare's US and DE
    // gateways are both p.webshare.io:80) and differ only in the username, so the duplicate
    // check has to include the username.
    [Fact]
    public async Task Create_Should_Throw_When_SameAccountHostPortUsernameAlreadyExists()
    {
        await using var db = CreateDb();
        var account = await SeedWebShareAccountAsync(db);
        var sut = new CreateRotatingGatewayCommandHandler(db, new FakeProviderProtector());
        await sut.Handle(CommandFor(account.Id), CancellationToken.None);

        await Should.ThrowAsync<ConflictException>(
            () => sut.Handle(CommandFor(account.Id), CancellationToken.None).AsTask());
    }

    [Fact]
    public async Task Create_Should_Allow_SameHostAndPort_When_UsernameDiffers()
    {
        await using var db = CreateDb();
        var account = await SeedWebShareAccountAsync(db);
        var sut = new CreateRotatingGatewayCommandHandler(db, new FakeProviderProtector());

        await sut.Handle(CommandFor(account.Id, "jgwcycpg-US-rotate"), CancellationToken.None);
        await sut.Handle(CommandFor(account.Id, "jgwcycpg-DE-rotate"), CancellationToken.None);

        (await db.Proxies.CountAsync()).ShouldBe(2);
    }

    internal sealed class FakeProviderProtector : IProxySecretProtector
    {
        public string Protect(string plaintext) => $"enc:{plaintext}";
        public string Unprotect(string protectedValue) => protectedValue["enc:".Length..];
    }
}
```

Before writing the file, open `src/Modules/Proxies/Modules.Proxies/Services/IProxySecretProtector.cs` and confirm the two member names are exactly `Protect` and `Unprotect`; if they differ, match the real interface here and everywhere below.

- [ ] **Step 2: Write the failing validator tests**

Create `src/Tests/Proxies.Tests/Validators/RotatingGatewayValidatorTests.cs`:

```csharp
using FSH.Modules.Proxies.Contracts;
using FSH.Modules.Proxies.Contracts.v1.RotatingGateways;
using FSH.Modules.Proxies.Domain;
using FSH.Modules.Proxies.Features.v1.RotatingGateways.CreateRotatingGateway;
using Shouldly;
using Xunit;

namespace Proxies.Tests.Validators;

public sealed class RotatingGatewayValidatorTests
{
    private static CreateRotatingGatewayCommand Valid(Guid accountId) =>
        new(accountId, "p.webshare.io", 80, ProxyProtocol.Http, "jgwcycpg-US-rotate", "pass",
            "US", "webshare-rotate", ProxyKind.Residential, ["pais:us"]);

    [Fact]
    public void Create_Should_Accept_AWellFormedGateway()
    {
        var result = new CreateRotatingGatewayCommandValidator().Validate(Valid(Guid.CreateVersion7()));
        result.IsValid.ShouldBeTrue();
    }

    // A rotating gateway with no provider behind it is not a thing that exists, and allowing it
    // would reintroduce the lost provider link this feature was written to fix.
    [Fact]
    public void Create_Should_Reject_TheManualProviderAccount()
    {
        var result = new CreateRotatingGatewayCommandValidator().Validate(Valid(ManualProviderAccount.Id));

        result.IsValid.ShouldBeFalse();
        result.Errors.ShouldContain(e => e.PropertyName == nameof(CreateRotatingGatewayCommand.ProviderAccountId));
    }

    [Fact]
    public void Create_Should_Reject_AnEmptyProviderAccountId()
    {
        var result = new CreateRotatingGatewayCommandValidator().Validate(Valid(Guid.Empty));
        result.IsValid.ShouldBeFalse();
    }

    [Fact]
    public void Create_Should_Reject_AnOutOfRangePort()
    {
        var command = Valid(Guid.CreateVersion7()) with { Port = 70000 };
        new CreateRotatingGatewayCommandValidator().Validate(command).IsValid.ShouldBeFalse();
    }

    [Fact]
    public void Create_Should_Reject_AGeolocationLongerThanTenCharacters()
    {
        var command = Valid(Guid.CreateVersion7()) with { Geolocation = "UNITED-STATES-OF-AMERICA" };
        new CreateRotatingGatewayCommandValidator().Validate(command).IsValid.ShouldBeFalse();
    }
}
```

- [ ] **Step 3: Run both test files to verify they fail**

Run: `dotnet test src/Tests/Proxies.Tests --filter "FullyQualifiedName~RotatingGateway"`
Expected: FAIL — the command, validator and handler types do not exist (compile error).

- [ ] **Step 4: Add the permissions**

In `src/Modules/Proxies/Modules.Proxies.Contracts/Authorization/ProxiesPermissions.cs`, add this nested class immediately after the `ManualProxies` class:

```csharp
    /// <summary>
    /// Registering a rotating gateway operates against a real provider account, which is a
    /// meaningfully different grant from registering a standalone self-hosted proxy — hence its
    /// own resource rather than reusing <see cref="ManualProxies"/>.
    /// </summary>
    public static class RotatingGateways
    {
        public const string Resource = "Proxies.RotatingGateways";
        public const string View = $"Permissions.{Resource}.View";
        public const string Create = $"Permissions.{Resource}.Create";
        public const string Update = $"Permissions.{Resource}.Update";
        public const string Delete = $"Permissions.{Resource}.Delete";
    }
```

And in the `All` collection, immediately after the four `ManualProxies` entries:

```csharp
        new("View Rotating Gateways", ActionConstants.View, RotatingGateways.Resource, IsBasic: true),
        new("Create Rotating Gateways", ActionConstants.Create, RotatingGateways.Resource),
        new("Update Rotating Gateways", ActionConstants.Update, RotatingGateways.Resource),
        new("Delete Rotating Gateways", ActionConstants.Delete, RotatingGateways.Resource),
```

- [ ] **Step 5: Add the command**

Create `src/Modules/Proxies/Modules.Proxies.Contracts/v1/RotatingGateways/CreateRotatingGatewayCommand.cs`:

```csharp
using Mediator;

namespace FSH.Modules.Proxies.Contracts.v1.RotatingGateways;

/// <summary>
/// Registers a provider's rotating gateway by hand. The system deliberately models no provider's
/// username grammar: rotation mode, country, city and sticky-session parameters are all encoded in
/// the username by every provider that supports them, so the operator types the username they want
/// and the system stores it verbatim.
/// </summary>
public sealed record CreateRotatingGatewayCommand(
    Guid ProviderAccountId, string Host, int Port, ProxyProtocol Protocol,
    string? Username, string? PlaintextPassword,
    string? Geolocation, string? ProviderGrouping, ProxyKind? Kind,
    IReadOnlyList<string> TagNames) : ICommand<Guid>;
```

- [ ] **Step 6: Add the validator**

Create `src/Modules/Proxies/Modules.Proxies/Features/v1/RotatingGateways/CreateRotatingGateway/CreateRotatingGatewayCommandValidator.cs`:

```csharp
using FluentValidation;
using FSH.Modules.Proxies.Contracts.v1.RotatingGateways;
using FSH.Modules.Proxies.Domain;

namespace FSH.Modules.Proxies.Features.v1.RotatingGateways.CreateRotatingGateway;

public sealed class CreateRotatingGatewayCommandValidator : AbstractValidator<CreateRotatingGatewayCommand>
{
    public CreateRotatingGatewayCommandValidator()
    {
        // Existence is checked in the handler (it needs the DbContext); this rule only rules out
        // the two ids that are wrong regardless of what is in the database.
        RuleFor(x => x.ProviderAccountId)
            .NotEmpty()
            .NotEqual(ManualProviderAccount.Id)
            .WithMessage("A rotating gateway must belong to a real provider account, not the Manual account.");
        RuleFor(x => x.Host).NotEmpty().MaximumLength(255);
        RuleFor(x => x.Port).InclusiveBetween(1, 65535);
        RuleFor(x => x.Protocol).IsInEnum();
        RuleFor(x => x.Username).MaximumLength(255);
        RuleFor(x => x.Geolocation).MaximumLength(10);
        RuleFor(x => x.ProviderGrouping).MaximumLength(255);
        RuleForEach(x => x.TagNames).NotEmpty().MaximumLength(128);
    }
}
```

- [ ] **Step 7: Add the handler**

Create `src/Modules/Proxies/Modules.Proxies/Features/v1/RotatingGateways/CreateRotatingGateway/CreateRotatingGatewayCommandHandler.cs`:

```csharp
using FSH.Framework.Core.Exceptions;
using FSH.Modules.Proxies.Contracts;
using FSH.Modules.Proxies.Contracts.v1.RotatingGateways;
using FSH.Modules.Proxies.Data;
using FSH.Modules.Proxies.Domain;
using FSH.Modules.Proxies.Features.v1.ManualProxies.CreateManualProxy;
using FSH.Modules.Proxies.Services;
using Mediator;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace FSH.Modules.Proxies.Features.v1.RotatingGateways.CreateRotatingGateway;

/// <summary>
/// Note the protector key: <c>"provider-account"</c>, NOT the <c>"proxy-password"</c> one that
/// <see cref="CreateManualProxyCommandHandler"/> uses. <c>ProxyPasswordResolver</c> chooses its
/// protector by <c>ProviderAccountId</c> — the manual one only for proxies hanging off
/// <see cref="ManualProviderAccount"/> — so a gateway attached to a real provider account will be
/// decrypted with the provider-account protector and must be encrypted with it too. Getting this
/// wrong fails at lease time, not at creation time.
/// </summary>
public sealed class CreateRotatingGatewayCommandHandler(
    ProxiesDbContext dbContext, [FromKeyedServices("provider-account")] IProxySecretProtector protector)
    : ICommandHandler<CreateRotatingGatewayCommand, Guid>
{
    public async ValueTask<Guid> Handle(CreateRotatingGatewayCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        bool accountExists = await dbContext.ProviderAccounts
            .AnyAsync(a => a.Id == command.ProviderAccountId, cancellationToken).ConfigureAwait(false);
        if (!accountExists)
        {
            throw new NotFoundException($"Provider account {command.ProviderAccountId} not found.");
        }

        // Check-then-insert, with a race this knowingly accepts: gateway registration is a manual
        // operator action measured in units per month. The alternative — a filtered/partial unique
        // index over a nullable Username column — diverges between SQL Server and PostgreSQL for
        // no practical gain, and a plain unique index on (Host, Port) would make a provider's
        // second gateway unregisterable (US and DE are both p.webshare.io:80).
        bool duplicate = await dbContext.Proxies.AnyAsync(
            p => p.ProviderAccountId == command.ProviderAccountId
                && p.Host == command.Host
                && p.Port == command.Port
                && p.Username == command.Username,
            cancellationToken).ConfigureAwait(false);
        if (duplicate)
        {
            throw new ConflictException($"A rotating gateway for {command.Host}:{command.Port} with that username already exists on this provider account.");
        }

        string? protectedPassword = string.IsNullOrWhiteSpace(command.PlaintextPassword)
            ? null : protector.Protect(command.PlaintextPassword);

        var gateway = Proxy.CreateRotatingGateway(
            command.ProviderAccountId, command.Host, command.Port, command.Protocol,
            command.Username, protectedPassword,
            command.Geolocation, command.ProviderGrouping, command.Kind);

        foreach (var tagId in await CreateManualProxyCommandHandler
            .ResolveTagIdsAsync(dbContext, command.TagNames, cancellationToken).ConfigureAwait(false))
        {
            gateway.AssignTag(tagId);
        }

        dbContext.Proxies.Add(gateway);
        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return gateway.Id;
    }
}
```

If `FSH.Framework.Core.Exceptions` has no `ConflictException`, run `grep -rn "class .*Exception" src/BuildingBlocks --include=*.cs | grep -i "conflict\|badrequest"` and use whichever 409/400 exception the framework provides; update the test in Step 1 to match. Do not add a new exception type to `BuildingBlocks`.

- [ ] **Step 8: Add the endpoint**

Create `src/Modules/Proxies/Modules.Proxies/Features/v1/RotatingGateways/CreateRotatingGateway/CreateRotatingGatewayEndpoint.cs`:

```csharp
using FSH.Framework.Shared.Identity.Authorization;
using FSH.Modules.Proxies.Contracts.Authorization;
using FSH.Modules.Proxies.Contracts.v1.RotatingGateways;
using Mediator;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace FSH.Modules.Proxies.Features.v1.RotatingGateways.CreateRotatingGateway;

public static class CreateRotatingGatewayEndpoint
{
    internal static RouteHandlerBuilder MapCreateRotatingGatewayEndpoint(this IEndpointRouteBuilder endpoints)
    {
        return endpoints.MapPost("/rotating-gateways",
                async (CreateRotatingGatewayCommand command, IMediator mediator, CancellationToken ct) =>
                    Results.Ok(await mediator.Send(command, ct)))
            .WithName("CreateRotatingGateway")
            .WithSummary("Register a provider's rotating gateway endpoint")
            .RequirePermission(ProxiesPermissions.RotatingGateways.Create);
    }
}
```

- [ ] **Step 9: Wire it into the module**

In `src/Modules/Proxies/Modules.Proxies/ProxiesModule.cs`, add the using:

```csharp
using FSH.Modules.Proxies.Features.v1.RotatingGateways.CreateRotatingGateway;
```

and in `MapEndpoints`, immediately after `group.MapDeleteManualProxyEndpoint();`:

```csharp
        group.MapCreateRotatingGatewayEndpoint();
```

- [ ] **Step 10: Run the tests to verify they pass**

Run: `dotnet test src/Tests/Proxies.Tests --filter "FullyQualifiedName~RotatingGateway"`
Expected: PASS

- [ ] **Step 11: Run the architecture tests**

Run: `dotnet test src/Tests/Architecture.Tests`
Expected: PASS — this is what confirms the new command handler has a matching validator and respects module boundaries.

- [ ] **Step 12: Commit**

```bash
git add src/Modules/Proxies src/Tests/Proxies.Tests
git commit -m "feat(proxies): register rotating gateways under a real provider account"
```

---

### Task 3: Update and delete endpoints

**Files:**
- Create: `src/Modules/Proxies/Modules.Proxies.Contracts/v1/RotatingGateways/UpdateRotatingGatewayCommand.cs`
- Create: `src/Modules/Proxies/Modules.Proxies.Contracts/v1/RotatingGateways/DeleteRotatingGatewayCommand.cs`
- Create: `src/Modules/Proxies/Modules.Proxies/Features/v1/RotatingGateways/UpdateRotatingGateway/` (validator, handler, endpoint)
- Create: `src/Modules/Proxies/Modules.Proxies/Features/v1/RotatingGateways/DeleteRotatingGateway/` (validator, handler, endpoint)
- Modify: `src/Modules/Proxies/Modules.Proxies/ProxiesModule.cs`
- Test: `src/Tests/Proxies.Tests/Handlers/RotatingGatewayHandlerTests.cs`

**Interfaces:**
- Consumes: everything from Task 2.
- Produces: `UpdateRotatingGatewayCommand(Guid Id, string Host, int Port, ProxyProtocol Protocol, string? Username, string? PlaintextPassword, string? Geolocation, string? ProviderGrouping, ProxyKind? Kind, IReadOnlyList<string> TagNames) : ICommand`; `DeleteRotatingGatewayCommand(Guid Id) : ICommand`; routes `PUT`/`DELETE /api/v1/proxies/rotating-gateways/{id:guid}`.

- [ ] **Step 1: Write the failing tests**

Append to the `RotatingGatewayHandlerTests` class created in Task 2:

```csharp
    [Fact]
    public async Task Update_Should_KeepExistingPassword_When_PlaintextPasswordNotProvided()
    {
        await using var db = CreateDb();
        var account = await SeedWebShareAccountAsync(db);
        var protector = new FakeProviderProtector();
        var id = await new CreateRotatingGatewayCommandHandler(db, protector)
            .Handle(CommandFor(account.Id), CancellationToken.None);

        await new UpdateRotatingGatewayCommandHandler(db, protector).Handle(
            new UpdateRotatingGatewayCommand(id, "p.webshare.io", 80, ProxyProtocol.Http,
                "jgwcycpg-DE-rotate", null, "DE", "webshare-rotate", ProxyKind.Residential, ["pais:de"]),
            CancellationToken.None);

        var stored = await db.Proxies.SingleAsync(x => x.Id == id);
        stored.ProtectedPassword.ShouldBe(protector.Protect("gateway-pass"));
        stored.Username.ShouldBe("jgwcycpg-DE-rotate");
        stored.Geolocation.ShouldBe("DE");
        stored.EndpointType.ShouldBe(ProxyEndpointType.RotatingGateway);
    }

    // Without the EndpointType scope, PUT /rotating-gateways with the id of a provider-synced
    // individual proxy would rewrite its connection details — which the next sync would then
    // silently revert, making the bug intermittent and very hard to trace.
    [Fact]
    public async Task Update_Should_Throw_When_TheIdBelongsToAnIndividualProxy()
    {
        await using var db = CreateDb();
        var account = await SeedWebShareAccountAsync(db);
        var individual = Proxy.Create(account.Id, "1.2.3.4", 8080, ProxyProtocol.Http, "u", null, "ext-1");
        db.Proxies.Add(individual);
        await db.SaveChangesAsync();

        await Should.ThrowAsync<NotFoundException>(() =>
            new UpdateRotatingGatewayCommandHandler(db, new FakeProviderProtector()).Handle(
                new UpdateRotatingGatewayCommand(individual.Id, "evil.example", 80, ProxyProtocol.Http,
                    null, null, null, null, null, []),
                CancellationToken.None).AsTask());

        var untouched = await db.Proxies.SingleAsync(x => x.Id == individual.Id);
        untouched.Host.ShouldBe("1.2.3.4");
    }

    [Fact]
    public async Task Delete_Should_RemoveTheGateway()
    {
        await using var db = CreateDb();
        var account = await SeedWebShareAccountAsync(db);
        var id = await new CreateRotatingGatewayCommandHandler(db, new FakeProviderProtector())
            .Handle(CommandFor(account.Id), CancellationToken.None);

        await new DeleteRotatingGatewayCommandHandler(db).Handle(
            new DeleteRotatingGatewayCommand(id), CancellationToken.None);

        (await db.Proxies.AnyAsync(x => x.Id == id)).ShouldBeFalse();
    }

    [Fact]
    public async Task Delete_Should_Throw_When_TheIdBelongsToAnIndividualProxy()
    {
        await using var db = CreateDb();
        var account = await SeedWebShareAccountAsync(db);
        var individual = Proxy.Create(account.Id, "1.2.3.4", 8080, ProxyProtocol.Http, "u", null, "ext-1");
        db.Proxies.Add(individual);
        await db.SaveChangesAsync();

        await Should.ThrowAsync<NotFoundException>(() =>
            new DeleteRotatingGatewayCommandHandler(db)
                .Handle(new DeleteRotatingGatewayCommand(individual.Id), CancellationToken.None).AsTask());

        (await db.Proxies.AnyAsync(x => x.Id == individual.Id)).ShouldBeTrue();
    }
```

Add these usings to the test file:

```csharp
using FSH.Modules.Proxies.Features.v1.RotatingGateways.UpdateRotatingGateway;
using FSH.Modules.Proxies.Features.v1.RotatingGateways.DeleteRotatingGateway;
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test src/Tests/Proxies.Tests --filter "FullyQualifiedName~RotatingGatewayHandlerTests"`
Expected: FAIL — the update/delete types do not exist (compile error).

- [ ] **Step 3: Add the two commands**

Create `src/Modules/Proxies/Modules.Proxies.Contracts/v1/RotatingGateways/UpdateRotatingGatewayCommand.cs`:

```csharp
using Mediator;

namespace FSH.Modules.Proxies.Contracts.v1.RotatingGateways;

/// <summary>
/// Every connection field is required, not optional. <c>Proxy.UpdateConnection</c> is a full
/// replacement, and optional parameters there are exactly how manual edits used to silently blank
/// out provider metadata a caller forgot existed. A caller that means to keep a value passes it.
/// A blank <see cref="PlaintextPassword"/> is the one exception and means "keep the stored one".
/// </summary>
public sealed record UpdateRotatingGatewayCommand(
    Guid Id, string Host, int Port, ProxyProtocol Protocol,
    string? Username, string? PlaintextPassword,
    string? Geolocation, string? ProviderGrouping, ProxyKind? Kind,
    IReadOnlyList<string> TagNames) : ICommand;
```

Create `src/Modules/Proxies/Modules.Proxies.Contracts/v1/RotatingGateways/DeleteRotatingGatewayCommand.cs`:

```csharp
using Mediator;

namespace FSH.Modules.Proxies.Contracts.v1.RotatingGateways;

public sealed record DeleteRotatingGatewayCommand(Guid Id) : ICommand;
```

- [ ] **Step 4: Add the update validator and handler**

Create `.../Features/v1/RotatingGateways/UpdateRotatingGateway/UpdateRotatingGatewayCommandValidator.cs`:

```csharp
using FluentValidation;
using FSH.Modules.Proxies.Contracts.v1.RotatingGateways;

namespace FSH.Modules.Proxies.Features.v1.RotatingGateways.UpdateRotatingGateway;

public sealed class UpdateRotatingGatewayCommandValidator : AbstractValidator<UpdateRotatingGatewayCommand>
{
    public UpdateRotatingGatewayCommandValidator()
    {
        RuleFor(x => x.Id).NotEmpty();
        RuleFor(x => x.Host).NotEmpty().MaximumLength(255);
        RuleFor(x => x.Port).InclusiveBetween(1, 65535);
        RuleFor(x => x.Protocol).IsInEnum();
        RuleFor(x => x.Username).MaximumLength(255);
        RuleFor(x => x.Geolocation).MaximumLength(10);
        RuleFor(x => x.ProviderGrouping).MaximumLength(255);
        RuleForEach(x => x.TagNames).NotEmpty().MaximumLength(128);
    }
}
```

Create `.../Features/v1/RotatingGateways/UpdateRotatingGateway/UpdateRotatingGatewayCommandHandler.cs`:

```csharp
using FSH.Framework.Core.Exceptions;
using FSH.Modules.Proxies.Contracts;
using FSH.Modules.Proxies.Contracts.v1.RotatingGateways;
using FSH.Modules.Proxies.Data;
using FSH.Modules.Proxies.Features.v1.ManualProxies.CreateManualProxy;
using FSH.Modules.Proxies.Services;
using Mediator;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace FSH.Modules.Proxies.Features.v1.RotatingGateways.UpdateRotatingGateway;

public sealed class UpdateRotatingGatewayCommandHandler(
    ProxiesDbContext dbContext, [FromKeyedServices("provider-account")] IProxySecretProtector protector)
    : ICommandHandler<UpdateRotatingGatewayCommand>
{
    public async ValueTask<Unit> Handle(UpdateRotatingGatewayCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        // Scoped by EndpointType, which is the ONLY thing identifying a gateway — the manual-proxy
        // handlers get their equivalent guard for free by scoping to ManualProviderAccount.Id, but
        // a gateway hangs off an ordinary provider account alongside its synced individual proxies.
        var gateway = await dbContext.Proxies.Include(x => x.TagAssignments)
            .FirstOrDefaultAsync(
                x => x.Id == command.Id && x.EndpointType == ProxyEndpointType.RotatingGateway,
                cancellationToken).ConfigureAwait(false)
            ?? throw new NotFoundException($"Rotating gateway {command.Id} not found.");

        string? protectedPassword = string.IsNullOrWhiteSpace(command.PlaintextPassword)
            ? gateway.ProtectedPassword
            : protector.Protect(command.PlaintextPassword);

        var newTagIds = await CreateManualProxyCommandHandler
            .ResolveTagIdsAsync(dbContext, command.TagNames, cancellationToken).ConfigureAwait(false);
        foreach (var tagId in gateway.TagAssignments.Select(a => a.TagId).Except(newTagIds).ToList())
        {
            gateway.UnassignTag(tagId);
        }
        foreach (var tagId in newTagIds)
        {
            gateway.AssignTag(tagId);
        }

        gateway.UpdateConnection(
            command.Host, command.Port, command.Protocol, command.Username, protectedPassword,
            command.Geolocation, command.ProviderGrouping, command.Kind);

        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return Unit.Value;
    }
}
```

- [ ] **Step 5: Add the delete validator and handler**

Create `.../Features/v1/RotatingGateways/DeleteRotatingGateway/DeleteRotatingGatewayCommandValidator.cs`:

```csharp
using FluentValidation;
using FSH.Modules.Proxies.Contracts.v1.RotatingGateways;

namespace FSH.Modules.Proxies.Features.v1.RotatingGateways.DeleteRotatingGateway;

public sealed class DeleteRotatingGatewayCommandValidator : AbstractValidator<DeleteRotatingGatewayCommand>
{
    public DeleteRotatingGatewayCommandValidator() => RuleFor(x => x.Id).NotEmpty();
}
```

Create `.../Features/v1/RotatingGateways/DeleteRotatingGateway/DeleteRotatingGatewayCommandHandler.cs`:

```csharp
using FSH.Framework.Core.Exceptions;
using FSH.Modules.Proxies.Contracts;
using FSH.Modules.Proxies.Contracts.v1.RotatingGateways;
using FSH.Modules.Proxies.Data;
using Mediator;
using Microsoft.EntityFrameworkCore;

namespace FSH.Modules.Proxies.Features.v1.RotatingGateways.DeleteRotatingGateway;

public sealed class DeleteRotatingGatewayCommandHandler(ProxiesDbContext dbContext)
    : ICommandHandler<DeleteRotatingGatewayCommand>
{
    public async ValueTask<Unit> Handle(DeleteRotatingGatewayCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        var gateway = await dbContext.Proxies
            .FirstOrDefaultAsync(
                x => x.Id == command.Id && x.EndpointType == ProxyEndpointType.RotatingGateway,
                cancellationToken).ConfigureAwait(false)
            ?? throw new NotFoundException($"Rotating gateway {command.Id} not found.");

        dbContext.Proxies.Remove(gateway);
        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return Unit.Value;
    }
}
```

- [ ] **Step 6: Add the two endpoints**

Create `.../UpdateRotatingGateway/UpdateRotatingGatewayEndpoint.cs`:

```csharp
using FSH.Framework.Shared.Identity.Authorization;
using FSH.Modules.Proxies.Contracts;
using FSH.Modules.Proxies.Contracts.Authorization;
using FSH.Modules.Proxies.Contracts.v1.RotatingGateways;
using Mediator;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace FSH.Modules.Proxies.Features.v1.RotatingGateways.UpdateRotatingGateway;

public static class UpdateRotatingGatewayEndpoint
{
    internal static RouteHandlerBuilder MapUpdateRotatingGatewayEndpoint(this IEndpointRouteBuilder endpoints)
    {
        return endpoints.MapPut("/rotating-gateways/{id:guid}",
                async (Guid id, UpdateRotatingGatewayBody body, IMediator mediator, CancellationToken ct) =>
                {
                    await mediator.Send(
                        new UpdateRotatingGatewayCommand(id, body.Host, body.Port, body.Protocol,
                            body.Username, body.PlaintextPassword, body.Geolocation,
                            body.ProviderGrouping, body.Kind, body.TagNames), ct);
                    return Results.NoContent();
                })
            .WithName("UpdateRotatingGateway")
            .WithSummary("Update a provider's rotating gateway endpoint")
            .RequirePermission(ProxiesPermissions.RotatingGateways.Update);
    }

    internal sealed record UpdateRotatingGatewayBody(
        string Host, int Port, ProxyProtocol Protocol,
        string? Username, string? PlaintextPassword,
        string? Geolocation, string? ProviderGrouping, ProxyKind? Kind,
        IReadOnlyList<string> TagNames);
}
```

Create `.../DeleteRotatingGateway/DeleteRotatingGatewayEndpoint.cs`:

```csharp
using FSH.Framework.Shared.Identity.Authorization;
using FSH.Modules.Proxies.Contracts.Authorization;
using FSH.Modules.Proxies.Contracts.v1.RotatingGateways;
using Mediator;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace FSH.Modules.Proxies.Features.v1.RotatingGateways.DeleteRotatingGateway;

public static class DeleteRotatingGatewayEndpoint
{
    internal static RouteHandlerBuilder MapDeleteRotatingGatewayEndpoint(this IEndpointRouteBuilder endpoints)
    {
        return endpoints.MapDelete("/rotating-gateways/{id:guid}",
                async (Guid id, IMediator mediator, CancellationToken ct) =>
                {
                    await mediator.Send(new DeleteRotatingGatewayCommand(id), ct);
                    return Results.NoContent();
                })
            .WithName("DeleteRotatingGateway")
            .WithSummary("Delete a provider's rotating gateway endpoint")
            .RequirePermission(ProxiesPermissions.RotatingGateways.Delete);
    }
}
```

- [ ] **Step 7: Wire both into the module**

In `ProxiesModule.cs`, add the usings for the two new namespaces and, after `group.MapCreateRotatingGatewayEndpoint();`:

```csharp
        group.MapUpdateRotatingGatewayEndpoint();
        group.MapDeleteRotatingGatewayEndpoint();
```

- [ ] **Step 8: Run the tests to verify they pass**

Run: `dotnet test src/Tests/Proxies.Tests --filter "FullyQualifiedName~RotatingGateway"`
Expected: PASS

- [ ] **Step 9: Commit**

```bash
git add src/Modules/Proxies src/Tests/Proxies.Tests
git commit -m "feat(proxies): update and delete endpoints for rotating gateways"
```

---

### Task 4: Surface the endpoint type in listing

**Files:**
- Modify: `src/Modules/Proxies/Modules.Proxies.Contracts/Dtos/ProxyDto.cs`
- Modify: `src/Modules/Proxies/Modules.Proxies.Contracts/v1/Proxies/ListProxiesQuery.cs`
- Modify: `src/Modules/Proxies/Modules.Proxies/Features/v1/Proxies/ListProxies/ListProxiesQueryHandler.cs:87`
- Modify: `src/Modules/Proxies/Modules.Proxies/Features/v1/Proxies/ListProxies/ListProxiesEndpoint.cs`
- Test: `src/Tests/Proxies.Tests/Handlers/ListProxiesHandlerTests.cs`

**Interfaces:**
- Consumes: `ProxyEndpointType` (Task 1).
- Produces: `ProxyDto.EndpointType` (non-optional, positioned immediately after `Kind`); `ListProxiesQuery.EndpointType` (optional, appended last).

- [ ] **Step 1: Write the failing test**

Append to `src/Tests/Proxies.Tests/Handlers/ListProxiesHandlerTests.cs` (match the file's existing seeding helpers — read it first and reuse them rather than writing new ones):

```csharp
    [Fact]
    public async Task Handle_Should_FilterByEndpointType()
    {
        await using var db = CreateDb();
        var account = ProviderAccount.Create("WebShare", ProxyProviderType.WebShare, "creds");
        db.ProviderAccounts.Add(account);
        db.Proxies.Add(Proxy.Create(account.Id, "1.2.3.4", 8080, ProxyProtocol.Http, null, null, "ext-1"));
        db.Proxies.Add(Proxy.CreateRotatingGateway(account.Id, "p.webshare.io", 80, ProxyProtocol.Http,
            "jgwcycpg-US-rotate", null, "US", null, null));
        await db.SaveChangesAsync();
        var sut = new ListProxiesQueryHandler(db);

        var gateways = await sut.Handle(
            new ListProxiesQuery(null, null, null, EndpointType: ProxyEndpointType.RotatingGateway),
            CancellationToken.None);

        gateways.Items.Count.ShouldBe(1);
        gateways.Items[0].Host.ShouldBe("p.webshare.io");
        gateways.Items[0].EndpointType.ShouldBe(ProxyEndpointType.RotatingGateway);
    }

    [Fact]
    public async Task Handle_Should_ReturnBothTypes_When_NoEndpointTypeFilterGiven()
    {
        await using var db = CreateDb();
        var account = ProviderAccount.Create("WebShare", ProxyProviderType.WebShare, "creds");
        db.ProviderAccounts.Add(account);
        db.Proxies.Add(Proxy.Create(account.Id, "1.2.3.4", 8080, ProxyProtocol.Http, null, null, "ext-1"));
        db.Proxies.Add(Proxy.CreateRotatingGateway(account.Id, "p.webshare.io", 80, ProxyProtocol.Http,
            "jgwcycpg-US-rotate", null, "US", null, null));
        await db.SaveChangesAsync();

        var all = await new ListProxiesQueryHandler(db).Handle(
            new ListProxiesQuery(null, null, null), CancellationToken.None);

        all.Items.Count.ShouldBe(2);
    }
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test src/Tests/Proxies.Tests --filter "FullyQualifiedName~ListProxiesHandlerTests"`
Expected: FAIL — `ListProxiesQuery` has no `EndpointType` parameter and `ProxyDto` has no `EndpointType` property.

- [ ] **Step 3: Add the DTO property**

In `ProxyDto.cs`, insert `ProxyEndpointType EndpointType,` immediately after the `ProxyKind? Kind,` parameter — inside the metadata block and **ahead** of the defaulted trailing parameters (`Username`, `SuccessCount24h`, `FailureCount24h`, `LastEventAtUtc`), so those defaults stay intact. Add a doc comment:

```csharp
    // Individual proxy vs. a provider's rotating gateway. A gateway is one durable endpoint whose
    // exit IP the provider rotates per request, so the operator reads this to understand why the
    // same host:port appears on several rows (one per country/mode username).
    ProxyEndpointType EndpointType,
```

- [ ] **Step 4: Add the query filter parameter**

In `ListProxiesQuery.cs`, append `ProxyEndpointType? EndpointType = null` as the last parameter, after `int PageSize = 20`:

```csharp
public sealed record ListProxiesQuery(
    IReadOnlyList<string>? Tags, ProxyStatus? Status, Guid? ProviderAccountId,
    string? Geolocation = null, ProxyKind? Kind = null, string? Host = null, string? Username = null,
    int PageNumber = 1, int PageSize = 20,
    ProxyEndpointType? EndpointType = null) : IQuery<PagedResponse<ProxyDto>>;
```

- [ ] **Step 5: Apply the filter and populate the DTO**

In `ListProxiesQueryHandler.cs`, add after the `query.Kind` filter:

```csharp
        if (query.EndpointType is { } endpointType) q = q.Where(p => p.EndpointType == endpointType);
```

And in the `new ProxyDto(...)` call at line ~87, insert `p.EndpointType,` immediately after `p.Kind,`:

```csharp
                p.CreatedAtUtc, p.LastRenewedAtUtc, p.Geolocation, p.ProviderGrouping, p.Kind, p.EndpointType, p.Username,
```

- [ ] **Step 6: Bind the query-string parameter**

In `src/Modules/Proxies/Modules.Proxies/Features/v1/Proxies/ListProxies/ListProxiesEndpoint.cs`, replace the handler lambda with this one. `endpointType` is added as a nullable enum parameter (bound from the query string exactly like `status` and `kind`) and passed as a named argument, since `PageNumber`/`PageSize` sit between it and the positional run:

```csharp
        return endpoints.MapGet("/",
                (string[]? tags, ProxyStatus? status, Guid? providerAccountId, string? geolocation, ProxyKind? kind,
                    string? host, string? username, ProxyEndpointType? endpointType,
                    int pageNumber, int pageSize, IMediator mediator, CancellationToken ct) =>
                    mediator.Send(new ListProxiesQuery(tags, status, providerAccountId, geolocation, kind, host, username,
                        pageNumber == 0 ? 1 : pageNumber, pageSize == 0 ? 20 : pageSize, endpointType), ct))
            .WithName("ListProxies")
            .WithSummary("List proxies (paged, filterable by tags/status/provider account/geolocation/kind/host/username/endpoint type)")
            .RequirePermission(ProxiesPermissions.ProviderAccounts.View);
```

- [ ] **Step 7: Run the tests to verify they pass**

Run: `dotnet test src/Tests/Proxies.Tests`
Expected: PASS — the whole Proxies test project, since the `ProxyDto` shape change touches other tests that construct it.

- [ ] **Step 8: Commit**

```bash
git add src/Modules/Proxies src/Tests/Proxies.Tests
git commit -m "feat(proxies): expose and filter by proxy endpoint type in listing"
```

---

### Task 5: Exempt gateways from the auto-disable policy engine

**Files:**
- Modify: `src/Modules/Proxies/Modules.Proxies/Services/PolicyEvaluationService.cs`
- Test: `src/Tests/Proxies.Tests/Services/PolicyEvaluationServiceTests.cs`

**Interfaces:**
- Consumes: `Proxy.EndpointType` (Task 1).
- Produces: no new types. `IPolicyEvaluationService.EvaluateAsync` becomes a no-op for gateways.

- [ ] **Step 1: Write the failing tests**

Append to `PolicyEvaluationServiceTests`:

```csharp
    // A gateway fronts hundreds of exit IPs. Disabling it because individual IPs got banned is a
    // false positive that costs the whole resource, so no PolicyProfile may act on one. The usage
    // events are still recorded — the operator reads them as the 24h ratio on the proxy list.
    [Fact]
    public async Task EvaluateAsync_Should_NeverDisable_ARotatingGateway()
    {
        await using var db = CreateDb();
        var tag = Tag.Create("pais:us");
        var policy = PolicyProfile.Create("aggressive", PolicyProfileType.AutoDisable,
            failureThreshold: 1, windowMinutes: 60, minDistinctReporters: 1);
        var account = ProviderAccount.Create("WebShare", ProxyProviderType.WebShare, "creds");
        var gateway = Proxy.CreateRotatingGateway(account.Id, "p.webshare.io", 80, ProxyProtocol.Http,
            "jgwcycpg-US-rotate", null, "US", null, null);
        gateway.SetStatus(ProxyStatus.Active);
        gateway.AssignTag(tag.Id);
        var reporter = ApiClient.Create("scraper-a", "hash-a");
        db.ProviderAccounts.Add(account);
        db.Tags.Add(tag);
        db.PolicyProfiles.Add(policy);
        db.Proxies.Add(gateway);
        db.ApiClients.Add(reporter);
        db.Set<TagPolicyAssignment>().Add(TagPolicyAssignment.Create(tag.Id, policy.Id));
        db.ProxyUsageEvents.AddRange(
            ProxyUsageEvent.Create(gateway.Id, UsageEventSource.ConsumerFeedback, UsageEventOutcome.Banned, null, reporter.Id, null),
            ProxyUsageEvent.Create(gateway.Id, UsageEventSource.ConsumerFeedback, UsageEventOutcome.Banned, null, reporter.Id, null),
            ProxyUsageEvent.Create(gateway.Id, UsageEventSource.ConsumerFeedback, UsageEventOutcome.Failure, null, reporter.Id, null));
        await db.SaveChangesAsync();
        var renewalService = Substitute.For<IProxyRenewalService>();
        var sut = new PolicyEvaluationService(db, renewalService, new ProxyPolicyResolver(db));

        await sut.EvaluateAsync(gateway.Id, CancellationToken.None);

        (await db.Proxies.SingleAsync(p => p.Id == gateway.Id)).Status.ShouldBe(ProxyStatus.Active);
        await renewalService.DidNotReceive().TriggerAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }
```

The existing `EvaluateAsync_Should_Disable_When_ThresholdAndReportersReached` test already covers the other half — that an individual proxy under the same conditions still gets disabled, i.e. the guard did not widen. Leave it in place.

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test src/Tests/Proxies.Tests --filter "FullyQualifiedName~PolicyEvaluationServiceTests"`
Expected: FAIL — the gateway is disabled, because nothing exempts it yet.

- [ ] **Step 3: Add the guard**

In `PolicyEvaluationService.EvaluateAsync`, immediately after the existing `if (proxy.Status != ProxyStatus.Active) return;` guard:

```csharp
        // A rotating gateway is one endpoint fronting hundreds of provider-rotated exit IPs. A
        // Banned/Failure report against it says the exit IP used for that request is burnt, not
        // that the gateway is — disabling it would cost every scraper the whole resource over a
        // signal that is almost always a false positive. Its liveness is judged separately, by
        // IRotatingGatewayHealthPolicy, which reads only SystemHealthCheck events.
        //
        // The guard lives here rather than in the callers so all three entry points are covered at
        // once: the single-event feedback endpoint, the batch feedback endpoint, and the
        // health-check job.
        if (proxy.EndpointType == ProxyEndpointType.RotatingGateway) return;
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test src/Tests/Proxies.Tests --filter "FullyQualifiedName~PolicyEvaluationServiceTests"`
Expected: PASS — both the new gateway test and the existing individual-proxy disable tests.

- [ ] **Step 5: Commit**

```bash
git add src/Modules/Proxies src/Tests/Proxies.Tests
git commit -m "feat(proxies): exempt rotating gateways from auto-disable policies"
```

---

### Task 6: Gateway liveness rule in the health check

**Files:**
- Modify: `src/Modules/Proxies/Modules.Proxies/Options/ProxiesOptions.cs`
- Create: `src/Modules/Proxies/Modules.Proxies/Services/IRotatingGatewayHealthPolicy.cs`
- Create: `src/Modules/Proxies/Modules.Proxies/Services/RotatingGatewayHealthPolicy.cs`
- Modify: `src/Modules/Proxies/Modules.Proxies/Jobs/ProxyActiveHealthCheckJob.cs`
- Modify: `src/Modules/Proxies/Modules.Proxies/ProxiesModule.cs`
- Modify: `src/Host/FS.Proxy.Api/appsettings.json` (`ProxiesOptions` section)
- Test: `src/Tests/Proxies.Tests/Services/RotatingGatewayHealthPolicyTests.cs` (create)

**Interfaces:**
- Consumes: `ProxyEndpointType` (Task 1).
- Produces: `IRotatingGatewayHealthPolicy` with `Task EvaluateAsync(Guid proxyId, CancellationToken cancellationToken)`; `ProxiesOptions.RotatingGatewayFailureThreshold` (int, default 5).

- [ ] **Step 1: Write the failing tests**

Create `src/Tests/Proxies.Tests/Services/RotatingGatewayHealthPolicyTests.cs`:

```csharp
using FSH.Modules.Proxies.Contracts;
using FSH.Modules.Proxies.Data;
using FSH.Modules.Proxies.Domain;
using FSH.Modules.Proxies.Options;
using FSH.Modules.Proxies.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Shouldly;
using Xunit;

namespace Proxies.Tests.Services;

public sealed class RotatingGatewayHealthPolicyTests
{
    private const int Threshold = 3;

    private static ProxiesDbContext CreateDb() =>
        TestProxiesDbContext.Create(new DbContextOptionsBuilder<ProxiesDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    private static RotatingGatewayHealthPolicy CreateSut(ProxiesDbContext db) =>
        new(db, Options.Create(new ProxiesOptions { RotatingGatewayFailureThreshold = Threshold }));

    private static async Task<Proxy> SeedActiveGatewayAsync(ProxiesDbContext db)
    {
        var account = ProviderAccount.Create("WebShare", ProxyProviderType.WebShare, "creds");
        var gateway = Proxy.CreateRotatingGateway(account.Id, "p.webshare.io", 80, ProxyProtocol.Http,
            "jgwcycpg-US-rotate", null, "US", null, null);
        gateway.SetStatus(ProxyStatus.Active);
        db.ProviderAccounts.Add(account);
        db.Proxies.Add(gateway);
        await db.SaveChangesAsync();
        return gateway;
    }

    private static void AddEvent(ProxiesDbContext db, Guid proxyId, UsageEventSource source, UsageEventOutcome outcome) =>
        db.ProxyUsageEvents.Add(ProxyUsageEvent.Create(proxyId, source, outcome, null, null, null));

    [Fact]
    public async Task EvaluateAsync_Should_Disable_When_LastNHealthChecksAllFailed()
    {
        await using var db = CreateDb();
        var gateway = await SeedActiveGatewayAsync(db);
        for (int i = 0; i < Threshold; i++)
        {
            AddEvent(db, gateway.Id, UsageEventSource.SystemHealthCheck, UsageEventOutcome.Timeout);
        }
        await db.SaveChangesAsync();

        await CreateSut(db).EvaluateAsync(gateway.Id, CancellationToken.None);

        (await db.Proxies.SingleAsync(p => p.Id == gateway.Id)).Status.ShouldBe(ProxyStatus.Disabled);
    }

    [Fact]
    public async Task EvaluateAsync_Should_DoNothing_When_FewerThanNHealthChecksExist()
    {
        await using var db = CreateDb();
        var gateway = await SeedActiveGatewayAsync(db);
        for (int i = 0; i < Threshold - 1; i++)
        {
            AddEvent(db, gateway.Id, UsageEventSource.SystemHealthCheck, UsageEventOutcome.Failure);
        }
        await db.SaveChangesAsync();

        await CreateSut(db).EvaluateAsync(gateway.Id, CancellationToken.None);

        (await db.Proxies.SingleAsync(p => p.Id == gateway.Id)).Status.ShouldBe(ProxyStatus.Active);
    }

    // The whole point of the rule: banned exit IPs are not a dead gateway. Consumer feedback must
    // not count toward the threshold no matter how much of it there is.
    [Fact]
    public async Task EvaluateAsync_Should_IgnoreConsumerFeedback_NoMatterHowMuch()
    {
        await using var db = CreateDb();
        var gateway = await SeedActiveGatewayAsync(db);
        for (int i = 0; i < Threshold * 5; i++)
        {
            AddEvent(db, gateway.Id, UsageEventSource.ConsumerFeedback, UsageEventOutcome.Banned);
        }
        await db.SaveChangesAsync();

        await CreateSut(db).EvaluateAsync(gateway.Id, CancellationToken.None);

        (await db.Proxies.SingleAsync(p => p.Id == gateway.Id)).Status.ShouldBe(ProxyStatus.Active);
    }

    [Fact]
    public async Task EvaluateAsync_Should_DoNothing_When_ASuccessIsInsideTheWindow()
    {
        await using var db = CreateDb();
        var gateway = await SeedActiveGatewayAsync(db);
        AddEvent(db, gateway.Id, UsageEventSource.SystemHealthCheck, UsageEventOutcome.Timeout);
        AddEvent(db, gateway.Id, UsageEventSource.SystemHealthCheck, UsageEventOutcome.Success);
        AddEvent(db, gateway.Id, UsageEventSource.SystemHealthCheck, UsageEventOutcome.Timeout);
        await db.SaveChangesAsync();

        await CreateSut(db).EvaluateAsync(gateway.Id, CancellationToken.None);

        (await db.Proxies.SingleAsync(p => p.Id == gateway.Id)).Status.ShouldBe(ProxyStatus.Active);
    }

    [Fact]
    public async Task EvaluateAsync_Should_DoNothing_For_AnIndividualProxy()
    {
        await using var db = CreateDb();
        var account = ProviderAccount.Create("WebShare", ProxyProviderType.WebShare, "creds");
        var proxy = Proxy.Create(account.Id, "1.2.3.4", 8080, ProxyProtocol.Http, null, null, "ext-1");
        proxy.SetStatus(ProxyStatus.Active);
        db.ProviderAccounts.Add(account);
        db.Proxies.Add(proxy);
        for (int i = 0; i < Threshold; i++)
        {
            AddEvent(db, proxy.Id, UsageEventSource.SystemHealthCheck, UsageEventOutcome.Timeout);
        }
        await db.SaveChangesAsync();

        await CreateSut(db).EvaluateAsync(proxy.Id, CancellationToken.None);

        // Individual proxies are the PolicyProfile engine's business, not this rule's.
        (await db.Proxies.SingleAsync(p => p.Id == proxy.Id)).Status.ShouldBe(ProxyStatus.Active);
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test src/Tests/Proxies.Tests --filter "FullyQualifiedName~RotatingGatewayHealthPolicyTests"`
Expected: FAIL — `RotatingGatewayHealthPolicy` and `RotatingGatewayFailureThreshold` do not exist (compile error).

- [ ] **Step 3: Add the option**

In `src/Modules/Proxies/Modules.Proxies/Options/ProxiesOptions.cs`, append:

```csharp
    /// <summary>
    /// How many consecutive non-successful <c>SystemHealthCheck</c> events disable a rotating
    /// gateway. Counts EVENTS, not cycles: the health-check job writes one event per resolved
    /// health-check target, so a gateway with 3 targets and a threshold of 5 is disabled after
    /// roughly two cycles. Consumer feedback never counts toward this.
    /// </summary>
    [Range(1, 100)]
    public int RotatingGatewayFailureThreshold { get; set; } = 5;
```

Add the same key to the `ProxiesOptions` section of `src/Host/FS.Proxy.Api/appsettings.json`:

```json
    "RotatingGatewayFailureThreshold": 5
```

- [ ] **Step 4: Add the interface and implementation**

Create `src/Modules/Proxies/Modules.Proxies/Services/IRotatingGatewayHealthPolicy.cs`:

```csharp
namespace FSH.Modules.Proxies.Services;

/// <summary>
/// Judges whether a rotating gateway's own endpoint is dead, as distinct from its exit IPs being
/// burnt. The counterpart to <see cref="IPolicyEvaluationService"/>, which is exempt for gateways.
/// </summary>
public interface IRotatingGatewayHealthPolicy
{
    Task EvaluateAsync(Guid proxyId, CancellationToken cancellationToken = default);
}
```

Create `src/Modules/Proxies/Modules.Proxies/Services/RotatingGatewayHealthPolicy.cs`:

```csharp
using FSH.Modules.Proxies.Contracts;
using FSH.Modules.Proxies.Data;
using FSH.Modules.Proxies.Options;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace FSH.Modules.Proxies.Services;

/// <summary>
/// The two signals this rule needs to separate are already separated by
/// <see cref="UsageEventSource"/>, so no new outcome taxonomy (and no HTTP-status inspection for
/// 407 and friends) is required:
/// <list type="bullet">
/// <item><c>ProxyHealthCheckOutcomeClassifier</c> emits only Success/Failure/Timeout — it CANNOT
/// emit Banned. Every SystemHealthCheck event therefore answers "does the endpoint respond?".</item>
/// <item>Banned can only originate from a scraper calling the feedback endpoints. Every
/// ConsumerFeedback event answers "did this request work?" — the exit IP's health.</item>
/// </list>
/// So this rule reads SystemHealthCheck events exclusively.
/// </summary>
public sealed class RotatingGatewayHealthPolicy(ProxiesDbContext dbContext, IOptions<ProxiesOptions> options)
    : IRotatingGatewayHealthPolicy
{
    public async Task EvaluateAsync(Guid proxyId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);

        var proxy = await dbContext.Proxies
            .FirstOrDefaultAsync(p => p.Id == proxyId, cancellationToken).ConfigureAwait(false);
        if (proxy is null || proxy.EndpointType != ProxyEndpointType.RotatingGateway) return;

        // Same idempotency guard as PolicyEvaluationService: only an Active gateway is a candidate.
        // A Testing one is not being served yet (RequestProxies returns Active only) and is waiting
        // for the health check to promote it; a Disabled one has already been acted on, and a burst
        // of probe results must not re-disable it repeatedly.
        if (proxy.Status != ProxyStatus.Active) return;

        int threshold = options.Value.RotatingGatewayFailureThreshold;

        // Ordered by Id as a tiebreaker: several targets are probed in the same cycle and can land
        // on the same OccurredAtUtc tick. Id is a Guid v7, so it is monotonic with creation order.
        var recent = await dbContext.ProxyUsageEvents
            .Where(e => e.ProxyId == proxyId && e.Source == UsageEventSource.SystemHealthCheck)
            .OrderByDescending(e => e.OccurredAtUtc).ThenByDescending(e => e.Id)
            .Take(threshold)
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        if (recent.Count < threshold) return;
        if (recent.Exists(e => e.Outcome == UsageEventOutcome.Success)) return;

        proxy.SetStatus(ProxyStatus.Disabled);
        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }
}
```

- [ ] **Step 5: Run the tests to verify they pass**

Run: `dotnet test src/Tests/Proxies.Tests --filter "FullyQualifiedName~RotatingGatewayHealthPolicyTests"`
Expected: PASS

- [ ] **Step 6: Register the service**

In `ProxiesModule.ConfigureServices`, next to the existing `IPolicyEvaluationService` registration:

```csharp
        builder.Services.AddScoped<IRotatingGatewayHealthPolicy, RotatingGatewayHealthPolicy>();
```

- [ ] **Step 7: Call it from the job**

In `src/Modules/Proxies/Modules.Proxies/Jobs/ProxyActiveHealthCheckJob.cs`, add `IRotatingGatewayHealthPolicy rotatingGatewayHealthPolicy` to the primary constructor parameter list (after `policyEvaluationService`), and in `CheckOneProxyAsync`, immediately after the existing `policyEvaluationService.EvaluateAsync` call:

```csharp
            // PolicyEvaluationService is a no-op for gateways by design; this is the rule that
            // judges a gateway's own liveness instead.
            await rotatingGatewayHealthPolicy.EvaluateAsync(proxyId, cancellationToken).ConfigureAwait(false);
```

- [ ] **Step 8: Cover gateway promotion in the job's selection tests**

Adding the rule must not cost a gateway its route out of `Testing`. In
`src/Tests/Proxies.Tests/Jobs/ProxyActiveHealthCheckJobSelectionTests.cs`, add:

```csharp
    [Fact]
    public async Task RunAsync_Should_ProbeRotatingGateways_LikeAnyOtherProxy()
    {
        await using var db = CreateDb();
        var account = ProviderAccount.Create("WebShare", ProxyProviderType.WebShare, "creds");
        var gateway = Proxy.CreateRotatingGateway(account.Id, "p.webshare.io", 80, ProxyProtocol.Http,
            "jgwcycpg-US-rotate", null, "US", null, null);
        // Proxy.CreateRotatingGateway lands in Testing, and this job is the only thing that
        // promotes it to Active. A gateway left out of the predicate would sit unprobed and
        // unusable forever, since RequestProxies only returns Active.
        db.ProviderAccounts.Add(account);
        db.Proxies.Add(gateway);
        await db.SaveChangesAsync();

        var resolvedIds = new List<Guid>();
        var targetResolver = Substitute.For<IHealthCheckTargetResolver>();
        targetResolver.ResolveTargetsAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                resolvedIds.Add(call.Arg<Guid>());
                return Task.FromResult<IReadOnlyList<ResolvedHealthCheckTarget>>([]);
            });

        var sut = new ProxyActiveHealthCheckJob(
            db, targetResolver, new ProxyPasswordResolver(new FakeProtector(), new FakeProtector()),
            Substitute.For<IPolicyEvaluationService>(), Substitute.For<IRotatingGatewayHealthPolicy>(),
            NullLogger<ProxyActiveHealthCheckJob>.Instance);

        await sut.RunAsync(CancellationToken.None);

        resolvedIds.ShouldContain(gateway.Id);
    }
```

Match the existing test's construction exactly — read `RunAsync_Should_ProbeActiveAndTestingProxies_ButNotDisabledOrRetiredOnes` and copy how it builds `ProxyActiveHealthCheckJob` and its `ResolveTargetsAsync` substitute, adding only the new `IRotatingGatewayHealthPolicy` argument. If that test names its protector fake something other than `FakeProtector`, use the real name.

- [ ] **Step 9: Run the full Proxies suite**

Run: `dotnet test src/Tests/Proxies.Tests`
Expected: PASS. Every existing test that constructs `ProxyActiveHealthCheckJob` directly needs the new constructor argument — pass `Substitute.For<IRotatingGatewayHealthPolicy>()` there.

- [ ] **Step 10: Commit**

```bash
git add src/Modules/Proxies src/Host/FS.Proxy.Api/appsettings.json src/Tests/Proxies.Tests
git commit -m "feat(proxies): disable a rotating gateway only on repeated endpoint failures"
```

---

### Task 7: Pin down sync immunity with an integration test

**Files:**
- Test: `src/Tests/Integration.Tests/` — add to the existing provider-sync integration test file; find it with `grep -rln "ReconcileAsync\|SyncAsync" src/Tests/Integration.Tests`. If none exists, create `src/Tests/Integration.Tests/Proxies/RotatingGatewaySyncImmunityTests.cs` following the harness conventions in `.agents/rules/integration-testing.md`.

**Interfaces:**
- Consumes: `Proxy.CreateRotatingGateway` (Task 1), `ProviderAccountSyncService.ReconcileAsync(ProviderAccount, IReadOnlyList<ProviderProxyRecord>, CancellationToken)`.
- Produces: nothing.

- [ ] **Step 1: Read the integration-testing rules and the existing harness**

Run: `cat .agents/rules/integration-testing.md` and read whichever existing test file exercises `ProviderAccountSyncService`. Reuse its fixture and seeding helpers — do not build a new harness.

- [ ] **Step 2: Write the failing (well, should-already-pass) test**

This test documents behavior that already works by side effect. Write it so it would fail loudly if `ReconcileAsync`'s `ExternalId != null` filter were ever loosened:

```csharp
[Fact]
public async Task ReconcileAsync_Should_LeaveRotatingGatewaysAlone()
{
    // A gateway carries no ExternalId, because the provider exposes no per-gateway listing.
    // ReconcileAsync's query filters on ExternalId != null, which is what keeps it out of both
    // the upsert loop and the retire loop. That is currently a side effect of how the query is
    // written; this test makes it a contract.
    var account = ProviderAccount.Create("WebShare", ProxyProviderType.WebShare, "protected-creds");
    var gateway = Proxy.CreateRotatingGateway(account.Id, "p.webshare.io", 80, ProxyProtocol.Http,
        "jgwcycpg-US-rotate", "protected", "US", "webshare-rotate", ProxyKind.Residential);
    gateway.SetStatus(ProxyStatus.Active);
    var syncedProxy = Proxy.Create(account.Id, "1.2.3.4", 8080, ProxyProtocol.Http, "u", null, "ext-1");
    syncedProxy.SetStatus(ProxyStatus.Active);
    dbContext.ProviderAccounts.Add(account);
    dbContext.Proxies.AddRange(gateway, syncedProxy);
    await dbContext.SaveChangesAsync();

    // The provider now reports a completely different set: the previously-synced proxy is gone.
    var records = new List<ProviderProxyRecord>
    {
        new("ext-2", "5.6.7.8", 8080, ProxyProtocol.Http, "u2", "p2", IsActive: true)
    };

    var (created, updated, retired) = await sut.ReconcileAsync(account, records, CancellationToken.None);

    created.ShouldBe(1);
    retired.ShouldBe(1); // the ext-1 proxy, as expected

    var storedGateway = await dbContext.Proxies.SingleAsync(p => p.Id == gateway.Id);
    storedGateway.Status.ShouldBe(ProxyStatus.Active);
    storedGateway.Host.ShouldBe("p.webshare.io");
    storedGateway.Username.ShouldBe("jgwcycpg-US-rotate");
    storedGateway.Geolocation.ShouldBe("US");
    storedGateway.EndpointType.ShouldBe(ProxyEndpointType.RotatingGateway);
}
```

Adapt `dbContext` / `sut` to whatever the surrounding fixture names them.

- [ ] **Step 3: Run it**

Run: `dotnet test src/Tests/Integration.Tests --filter "FullyQualifiedName~RotatingGateway"`
Expected: PASS. **Integration tests require Docker to be running.** If it fails, the reconciliation logic diverges from what the spec assumed — stop and report rather than loosening the test.

- [ ] **Step 4: Commit**

```bash
git add src/Tests/Integration.Tests
git commit -m "test(proxies): pin down that provider sync leaves rotating gateways alone"
```

---

### Task 8: Admin UI — gateways page

**Files:**
- Modify: `clients/admin/src/lib/permissions.ts`
- Modify: `clients/admin/src/api/proxies.ts`
- Create: `clients/admin/src/api/rotating-gateways.ts`
- Create: `clients/admin/src/components/proxies/rotating-gateway-dialog.tsx`
- Create: `clients/admin/src/pages/proxies/rotating-gateways.tsx`
- Modify: `clients/admin/src/routes.tsx`
- Modify: `clients/admin/src/components/layout/nav-items.ts`
- Test: `clients/admin/tests/proxies/rotating-gateways.spec.ts` (create)

**Interfaces:**
- Consumes: `POST/PUT/DELETE /api/v1/proxies/rotating-gateways` (Tasks 2–3); `ProxyDto.endpointType` and `ListProxiesParams.endpointType` (Task 4).
- Produces: `ProxiesPermissions.RotatingGateways.{View,Create,Update,Delete}` (TS); `createRotatingGateway`, `updateRotatingGateway`, `deleteRotatingGateway`; route `/proxies/rotating`.

- [ ] **Step 1: Add the TS permission constants**

In `clients/admin/src/lib/permissions.ts`, after the `ManualProxies` block:

```ts
  RotatingGateways: {
    View: "Permissions.Proxies.RotatingGateways.View",
    Create: "Permissions.Proxies.RotatingGateways.Create",
    Update: "Permissions.Proxies.RotatingGateways.Update",
    Delete: "Permissions.Proxies.RotatingGateways.Delete",
  },
```

And in the permission-catalog list (around line 239, after the four `ManualProxies` entries):

```ts
      { name: ProxiesPermissions.RotatingGateways.View, description: "View rotating gateways", basic: true },
      { name: ProxiesPermissions.RotatingGateways.Create, description: "Create rotating gateways" },
      { name: ProxiesPermissions.RotatingGateways.Update, description: "Update rotating gateways" },
      { name: ProxiesPermissions.RotatingGateways.Delete, description: "Delete rotating gateways" },
```

- [ ] **Step 2: Extend the proxies API types**

In `clients/admin/src/api/proxies.ts`:

```ts
export type ProxyEndpointType = "Individual" | "RotatingGateway";
```

Add to `ProxyDto`, after `kind`:

```ts
  /** Individual proxy vs. a provider's rotating gateway (one endpoint, provider-rotated exit IPs). */
  endpointType: ProxyEndpointType;
```

Add to `ListProxiesParams`:

```ts
  endpointType?: ProxyEndpointType;
```

And inside `listProxies`, next to the other optional params:

```ts
  if (params.endpointType) query.set("endpointType", params.endpointType);
```

- [ ] **Step 3: Add the API module**

Create `clients/admin/src/api/rotating-gateways.ts`:

```ts
import { apiFetch } from "@/lib/api-client";
import type { ProxyKind, ProxyProtocol } from "./proxies";

const BASE = "/api/v1/proxies/rotating-gateways";

export type CreateRotatingGatewayInput = {
  providerAccountId: string;
  host: string;
  port: number;
  protocol: ProxyProtocol;
  username?: string;
  plaintextPassword?: string;
  geolocation?: string;
  providerGrouping?: string;
  kind?: ProxyKind;
  tagNames: string[];
};

export async function createRotatingGateway(input: CreateRotatingGatewayInput): Promise<string> {
  return apiFetch<string>(`${BASE}`, { method: "POST", body: JSON.stringify(input) });
}

/** The provider account cannot be changed after creation — delete and re-register instead. */
export type UpdateRotatingGatewayInput = Omit<CreateRotatingGatewayInput, "providerAccountId"> & { id: string };

export async function updateRotatingGateway(input: UpdateRotatingGatewayInput): Promise<void> {
  const { id, ...body } = input;
  await apiFetch<void>(`${BASE}/${id}`, { method: "PUT", body: JSON.stringify(body) });
}

export async function deleteRotatingGateway(id: string): Promise<void> {
  await apiFetch<void>(`${BASE}/${id}`, { method: "DELETE" });
}
```

- [ ] **Step 4: Add the dialog**

Create `clients/admin/src/components/proxies/rotating-gateway-dialog.tsx`, modeled closely on `manual-proxy-dialog.tsx` (read it first and match its structure, imports and idioms). Differences from that file:

- The zod schema in full (the manual dialog's, plus the gateway-only fields). `geolocation` is capped at 10 to match `ProxyConfiguration`'s column length and the validator:

```ts
const schema = z.object({
  providerAccountId: z.string().min(1, "Required."),
  host: z.string().trim().min(1, "Required.").max(255),
  port: z.coerce.number().int().min(1, "Required.").max(65535),
  username: z.string().trim().max(255).optional(),
  plaintextPassword: z.string().trim().optional(),
  geolocation: z.string().trim().max(10, "Max 10 characters.").optional(),
  providerGrouping: z.string().trim().max(255).optional(),
  tagsInput: z.string().trim(),
});
```
- A provider-account `<select>` fed by `listProviderAccounts(1, 100)`, filtering out `providerType === "Manual"`. It is disabled when `isEdit` is true (the update command carries no provider account).
- The host field's placeholder is `p.webshare.io` and the port default is `80`.
- The username field's hint explains it carries the provider's rotation parameters, e.g. `jgwcycpg-US-rotate`.
- Field ids are prefixed `rg-` instead of `mp-` (`rg-host`, `rg-port`, `rg-username`, `rg-password`, `rg-tags`, `rg-account`, `rg-geolocation`).
- Toast copy: "Rotating gateway created" / "Rotating gateway updated".
- `queryClient.invalidateQueries({ queryKey: ["proxies", "list"] })` on success, same as the manual dialog.
- Pass values through `mutate(values)`; do not close over component state in `mutationFn`.
- As in the manual dialog, pre-fill `username` on edit (the update is a full replacement) but never the password; a blank password means "keep the stored one".

- [ ] **Step 5: Add the page**

Create `clients/admin/src/pages/proxies/rotating-gateways.tsx`, modeled on `manual-proxies.tsx` (read it first). Differences:

- Export name `RotatingGatewaysListPage`.
- It does **not** need the manual-account lookup that page does. Query directly:

```ts
  const gatewaysQuery = useQuery({
    queryKey: ["proxies", "list", "rotating-gateways", pageNumber],
    queryFn: () => listProxies({ endpointType: "RotatingGateway", pageNumber, pageSize: PAGE_SIZE }),
    placeholderData: keepPreviousData,
  });
```

- Permissions come from `ProxiesPermissions.RotatingGateways`.
- Header: icon `Repeat` from `lucide-react`, title `"Rotating gateways"`, description `"Provider endpoints that rotate across your contracted proxies on every request."`.
- Empty state: kicker `"// no rotating gateways"`, title `"No rotating gateways yet."`, description `"Register a provider's rotating endpoint to get started."`.
- Each row shows the provider account name alongside `host:port`, plus the username (it is what distinguishes one gateway from another on the same host) and the tags.
- Delete confirm copy: `Delete rotating gateway "${gateway.username ?? gateway.host}"?`

- [ ] **Step 6: Register the route and nav entry**

In `clients/admin/src/routes.tsx`, add the lazy import next to the others:

```tsx
const RotatingGatewaysListPage = lazyNamed(
  () => import("@/pages/proxies/rotating-gateways"),
  "RotatingGatewaysListPage",
);
```

and the route, after the `proxies/manual` one:

```tsx
            {
              path: "proxies/rotating",
              element: (
                <RouteGuard perms={[ProxiesPermissions.RotatingGateways.View]}>
                  <RotatingGatewaysListPage />
                </RouteGuard>
              ),
            },
```

In `clients/admin/src/components/layout/nav-items.ts`, after the "Manual Proxies" item:

```ts
      {
        to: "/proxies/rotating",
        label: "Rotating Gateways",
        icon: Repeat,
        perms: [ProxiesPermissions.RotatingGateways.View],
      },
```

Add `Repeat` to the `lucide-react` import at the top of that file.

- [ ] **Step 7: Write the Playwright test**

Create `clients/admin/tests/proxies/rotating-gateways.spec.ts`, modeled on `manual-proxies.spec.ts` (read it first for the `seedAuthedSession` / `installAdminShellMocks` / `paged` helpers). Cover:

```ts
test("shows the empty state before any gateway exists", ...)
test("lists a gateway with its username and provider account", ...)
test("pre-fills the username when editing, so saving does not wipe it", ...)
```

Route-mock `**/api/v1/proxies/?*` with a `paged([...])` payload whose item includes `endpointType: "RotatingGateway"` alongside every other `ProxyDto` field (copy the field list from `manual-proxies.spec.ts` and add `endpointType`). Also mock `**/api/v1/proxies/provider-accounts*` with a WebShare account so the dialog's selector has something to show.

- [ ] **Step 8: Run lint, typecheck and the tests**

```bash
cd clients/admin
npm run lint
npm run build
npx playwright test tests/proxies/rotating-gateways.spec.ts
```

Expected: all pass. If `npm run lint` / `npm run build` are named differently, check `clients/admin/package.json` scripts and use the real names.

- [ ] **Step 9: Commit**

```bash
git add clients/admin
git commit -m "feat(admin): rotating gateways page"
```

---

### Task 9: Distinguish gateways on the main proxy list

**Files:**
- Modify: `clients/admin/src/pages/proxies/list.tsx`
- Test: `clients/admin/tests/proxies/proxies-list.spec.ts`

**Interfaces:**
- Consumes: `ProxyDto.endpointType`, `ListProxiesParams.endpointType` (Tasks 4, 8).
- Produces: nothing.

- [ ] **Step 1: Write the failing Playwright test**

Append to `clients/admin/tests/proxies/proxies-list.spec.ts`, matching the file's existing mocking style:

```ts
test("badges a rotating gateway so repeated host:port rows are explainable", async ({ page }) => {
  await page.route("**/api/v1/proxies/?*", async (route) => {
    await route.fulfill({
      status: 200,
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify(
        paged([
          {
            id: "44444444-4444-4444-4444-444444444444",
            host: "p.webshare.io",
            port: 80,
            protocol: "Http",
            status: "Active",
            providerAccountId: "webshare-acct",
            providerAccountName: "WebShare",
            providerType: "WebShare",
            tags: ["pais:us"],
            createdAtUtc: "2026-01-01T00:00:00Z",
            lastRenewedAtUtc: null,
            geolocation: "US",
            providerGrouping: null,
            kind: "Residential",
            endpointType: "RotatingGateway",
            username: "jgwcycpg-US-rotate",
            successCount24h: 0,
            failureCount24h: 0,
            lastEventAtUtc: null,
          },
        ]),
      ),
    });
  });

  await page.goto("/proxies");

  await expect(page.getByText("Rotating", { exact: true }).first()).toBeVisible({ timeout: 10_000 });
});
```

- [ ] **Step 2: Run it to verify it fails**

```bash
cd clients/admin && npx playwright test tests/proxies/proxies-list.spec.ts -g "badges a rotating gateway"
```
Expected: FAIL — no such badge is rendered.

- [ ] **Step 3: Render the badge**

In `clients/admin/src/pages/proxies/list.tsx`, in the row component that renders each proxy, add next to the status badge:

```tsx
{proxy.endpointType === "RotatingGateway" && (
  <Badge variant="info" className="font-mono uppercase tracking-[0.14em]" title="Provider-side rotating endpoint — its exit IP changes per request">
    Rotating
  </Badge>
)}
```

Match the surrounding markup: read how the existing badges are laid out in that file's grid and place this one so the columns still line up at phone width.

- [ ] **Step 4: Add the endpoint-type filter**

The page already has a filter bar (status / kind / geolocation / host / username). Add an endpoint-type `<select>` in the same style with options `All types` (undefined), `Individual`, `Rotating gateway`, threading the value into the `listProxies({ ... })` call and into the query key so the cache keys stay distinct. Follow exactly how the existing `kind` filter is wired.

- [ ] **Step 5: Run lint, build and the tests**

```bash
cd clients/admin
npm run lint
npm run build
npx playwright test tests/proxies/proxies-list.spec.ts
```
Expected: PASS

- [ ] **Step 6: Commit**

```bash
git add clients/admin
git commit -m "feat(admin): badge and filter rotating gateways on the proxy list"
```

---

### Task 10: Document the feature

**Files:**
- Modify: `.agents/rules/modules/proxies.md`

**Interfaces:**
- Consumes: everything above.
- Produces: nothing.

- [ ] **Step 1: Add the rotating-gateways section**

Append to `.agents/rules/modules/proxies.md`:

```markdown
## Rotating gateways

Some providers sell a **rotating gateway** alongside the individual proxies you contract: one durable
endpoint (`p.webshare.io:80` with a `<user>-US-rotate` username, or a BrightData zone endpoint) that
the provider rotates across those same proxies on every request. There is no per-IP listing to sync.

Modeled as a `Proxy` row with `EndpointType == ProxyEndpointType.RotatingGateway`, attached to the
**real** `ProviderAccount` — not `ManualProviderAccount.Id` — with `ExternalId == null`.

- **`EndpointType` is creation-only.** It is not a parameter of `Proxy.UpdateConnection`, so no edit or
  renewal path can demote a gateway by omission. Use `Proxy.CreateRotatingGateway`.
- **Password protector.** `ProxyPasswordResolver` picks its protector by `ProviderAccountId`, so a
  gateway is decrypted with `"provider-account"` and must be encrypted with it too — NOT with the
  `"proxy-password"` protector the manual-proxy handlers use. Getting this wrong fails at lease time.
- **Sync immunity.** `ProviderAccountSyncService.ReconcileAsync` filters on `ExternalId != null`, so
  gateways are neither updated nor retired by a sync. Pinned by an integration test.
- **Leasing is type-blind.** `RequestProxiesQueryHandler` does not read `EndpointType`. Routing
  gateways to the scrapers that want them is the operator's job, done with tags.
- **Exempt from auto-disable.** `PolicyEvaluationService.EvaluateAsync` returns early for gateways: a
  gateway fronts hundreds of exit IPs, and a `Banned` report means that request's exit IP is burnt, not
  that the gateway is. Usage events are still recorded, so the 24h ratio and activity timeline work.
- **Liveness instead.** `IRotatingGatewayHealthPolicy` disables a gateway when its last
  `ProxiesOptions.RotatingGatewayFailureThreshold` (default 5) `SystemHealthCheck` events are all
  non-`Success`. This works because `ProxyHealthCheckOutcomeClassifier` cannot emit `Banned` — health
  checks answer "does the endpoint respond?", consumer feedback answers "did this request work?", so
  `UsageEventSource` already separates the two signals. The threshold counts EVENTS, and the job writes
  one per health-check target, so a gateway with 3 targets trips after ~2 cycles.
- **Disabled gateways do not revive.** The job only probes `Active`/`Testing`. An operator re-enables
  via `SetProxiesStatusCommand`, same as any proxy.
- **Endpoint scoping.** The gateway update/delete handlers scope by
  `EndpointType == RotatingGateway`; without it, `PUT /rotating-gateways` could rewrite a synced
  individual proxy (which the next sync would revert, making the bug intermittent). The manual-proxy
  handlers get the mirror guard for free by scoping to `ManualProviderAccount.Id`.
- **No structured connection parameters.** Sticky sessions, country and city selection are all encoded
  in the username by every provider that supports them. The operator types the username; the system
  stores it verbatim and models no provider's grammar.

Permissions: `Proxies.RotatingGateways.{View,Create,Update,Delete}`.
Endpoints: `POST /rotating-gateways`, `PUT|DELETE /rotating-gateways/{id:guid}`.
Admin UI: `/proxies/rotating`.
```

- [ ] **Step 2: Run the full backend suite one last time**

Run: `dotnet test src/FS.Proxy.slnx`
Expected: PASS. Integration tests require Docker.

- [ ] **Step 3: Commit**

```bash
git add .agents/rules/modules/proxies.md
git commit -m "docs(proxies): document rotating gateways"
```

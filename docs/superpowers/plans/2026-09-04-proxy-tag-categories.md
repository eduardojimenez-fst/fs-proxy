# Proxy Tag Categories + Assignment UI Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Let admins tag any proxy (individually or in bulk) through a select-driven UI backed by a predefined category+value catalog (e.g. "pais" → "cl"/"ar"/"pe"), while keeping the existing free-form Tag system fully intact as a fallback and for everything already built on it (policy assignment, health-check-target assignment, list filtering).

**Architecture:** Two new EF entities (`TagCategory`, `TagCategoryValue`) form a small, purely-advisory catalog — no relationship to `Tag`/`ProxyTagAssignment` at the database level. The frontend composes `"{category}:{value}"` strings from the catalog and sends them through new proxy-tag-assignment commands that reuse the existing `Tag.Normalize`/`CreateManualProxyCommandHandler.ResolveTagIdsAsync` find-or-create machinery — identical to typing a tag by hand today.

**Tech Stack:** .NET 10, EF Core 10 / PostgreSQL, Mediator 3.x CQRS, FluentValidation, xUnit/Shouldly, React 19 + TanStack Query v5 + react-hook-form/zod, Playwright.

**Spec:** `docs/superpowers/specs/2026-09-04-proxy-tag-categories-design.md`

## Global Constraints

- Mediator handlers `public sealed`, return `ValueTask<T>`, `.ConfigureAwait(false)` every await.
- Every command handler + paginated query handler needs a validator (none of this plan's queries are paginated, so only commands need validators).
- Endpoint class names must start with an approved verb prefix (enforced by `Architecture.Tests/EndpointConventionTests.cs`): `Create`, `Update`, `Delete`, `List`, `Add`, `Remove`, `Assign`, `Unassign`, `Set` are all already allowed — **`Bulk` is not**, so the multi-proxy tag commands are named `AssignProxyTag`/`UnassignProxyTag`, not `BulkAssignProxyTag`/`BulkUnassignProxyTag`.
- `TagCategory`/`TagCategoryValue` never touch `Tag`/`ProxyTagAssignment` at the data level — they are a UI-composition catalog only. A `Tag` row created via the catalog is indistinguishable from one typed by hand.
- Frontend: pass per-call data through `mutate(arg)`, never via state a callback closes over.
- Build runs with `TreatWarningsAsErrors` — warnings fail the build.
- Reuse the existing `ProxiesPermissions.Tags` resource (`View`/`Create`/`Update`/`Delete`) for every new endpoint in this plan — no new permission group, backend or frontend (`clients/admin/src/lib/permissions.ts` already mirrors it).

---

### Task 1: `TagCategory`/`TagCategoryValue` domain entities + EF migration

**Files:**
- Create: `src/Modules/Proxies/Modules.Proxies/Domain/TagCategory.cs`
- Create: `src/Modules/Proxies/Modules.Proxies/Domain/TagCategoryValue.cs`
- Create: `src/Modules/Proxies/Modules.Proxies/Data/Configurations/TagCategoryConfiguration.cs`
- Create: `src/Modules/Proxies/Modules.Proxies/Data/Configurations/TagCategoryValueConfiguration.cs`
- Modify: `src/Modules/Proxies/Modules.Proxies/Data/ProxiesDbContext.cs`
- Test: `src/Tests/Proxies.Tests/Domain/TagCategoryTests.cs`
- Create (generated): `src/Host/FS.Proxy.Migrations.PostgreSQL/Proxies/{timestamp}_AddTagCategories.cs` + `.Designer.cs`, and regenerate `ProxiesDbContextModelSnapshot.cs`

**Interfaces:**
- Produces: `TagCategory.Create(string name) : TagCategory`, `TagCategory.Rename(string name)`, `TagCategory.AddValue(string value)` (throws `InvalidOperationException` if the normalized value already exists in this category), `TagCategory.RemoveValue(string value)`, `TagCategory.Id`, `TagCategory.Name`, `TagCategory.Values : IReadOnlyCollection<TagCategoryValue>`.
- Produces: `TagCategoryValue.TagCategoryId`, `TagCategoryValue.Value` (read-only properties; no public constructor — created only via `TagCategory.AddValue`).
- Produces: `ProxiesDbContext.TagCategories : DbSet<TagCategory>`.

- [ ] **Step 1: Write the failing tests**

Create `src/Tests/Proxies.Tests/Domain/TagCategoryTests.cs`:

```csharp
using FSH.Modules.Proxies.Domain;
using Shouldly;
using Xunit;

namespace Proxies.Tests.Domain;

public sealed class TagCategoryTests
{
    [Fact]
    public void Create_Should_NormalizeName()
    {
        var category = TagCategory.Create("  Pais  ");

        category.Name.ShouldBe("pais");
        category.Values.ShouldBeEmpty();
    }

    [Fact]
    public void Rename_Should_NormalizeNewName()
    {
        var category = TagCategory.Create("pais");

        category.Rename("  Country  ");

        category.Name.ShouldBe("country");
    }

    [Fact]
    public void AddValue_Should_NormalizeAndAppend()
    {
        var category = TagCategory.Create("pais");

        category.AddValue("  CL  ");

        category.Values.Single().Value.ShouldBe("cl");
        category.Values.Single().TagCategoryId.ShouldBe(category.Id);
    }

    [Fact]
    public void AddValue_Should_Throw_When_ValueAlreadyExists_CaseInsensitive()
    {
        var category = TagCategory.Create("pais");
        category.AddValue("cl");

        Should.Throw<InvalidOperationException>(() => category.AddValue("CL"));
    }

    [Fact]
    public void RemoveValue_Should_RemoveMatchingValue_CaseInsensitive()
    {
        var category = TagCategory.Create("pais");
        category.AddValue("cl");
        category.AddValue("ar");

        category.RemoveValue("CL");

        category.Values.Select(v => v.Value).ShouldBe(["ar"]);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test src/Tests/Proxies.Tests --filter "FullyQualifiedName~TagCategoryTests"`
Expected: FAIL — `TagCategory`/`TagCategoryValue` don't exist yet (compile error).

- [ ] **Step 3: Implement the domain entities**

Create `src/Modules/Proxies/Modules.Proxies/Domain/TagCategoryValue.cs`:

```csharp
using FSH.Framework.Core.Domain;

namespace FSH.Modules.Proxies.Domain;

/// <summary>
/// A plain child entity, not its own aggregate root — mirrors <see cref="ProxyTagAssignment"/>'s
/// shape. Values are never renamed, only added/removed, so the composite key
/// (TagCategoryId, Value) needs no separate surrogate id.
/// </summary>
public sealed class TagCategoryValue : IGlobalEntity
{
    public Guid TagCategoryId { get; private set; }
    public string Value { get; private set; } = default!;

    private TagCategoryValue() { }

    internal static TagCategoryValue Create(Guid tagCategoryId, string value) =>
        new() { TagCategoryId = tagCategoryId, Value = Normalize(value) };

    internal static string Normalize(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        return value.Trim().ToLowerInvariant();
    }
}
```

Create `src/Modules/Proxies/Modules.Proxies/Domain/TagCategory.cs`:

```csharp
using FSH.Framework.Core.Domain;

namespace FSH.Modules.Proxies.Domain;

/// <summary>
/// A purely advisory catalog of tag "dimensions" (e.g. "pais") and their allowed values (e.g.
/// "cl"), used only to compose "{category}:{value}" strings for the admin tag-assignment UI.
/// Deliberately has no foreign key to <see cref="Tag"/> or <see cref="ProxyTagAssignment"/> — a
/// tag composed from this catalog is indistinguishable from one typed by hand, and deleting a
/// category or value never touches already-assigned tags.
/// </summary>
public sealed class TagCategory : AggregateRoot<Guid>, IGlobalEntity
{
    public string Name { get; private set; } = default!;

    private readonly List<TagCategoryValue> _values = [];
    public IReadOnlyCollection<TagCategoryValue> Values => _values;

    private TagCategory() { }

    public static TagCategory Create(string name) =>
        new() { Id = Guid.CreateVersion7(), Name = Normalize(name) };

    public void Rename(string name) => Name = Normalize(name);

    public void AddValue(string value)
    {
        var normalized = TagCategoryValue.Normalize(value);
        if (_values.Any(v => v.Value == normalized))
        {
            throw new InvalidOperationException($"Value \"{normalized}\" already exists in category \"{Name}\".");
        }
        _values.Add(TagCategoryValue.Create(Id, normalized));
    }

    public void RemoveValue(string value)
    {
        var normalized = TagCategoryValue.Normalize(value);
        _values.RemoveAll(v => v.Value == normalized);
    }

    private static string Normalize(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return name.Trim().ToLowerInvariant();
    }
}
```

Create `src/Modules/Proxies/Modules.Proxies/Data/Configurations/TagCategoryValueConfiguration.cs`:

```csharp
using FSH.Modules.Proxies.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FSH.Modules.Proxies.Data.Configurations;

public sealed class TagCategoryValueConfiguration : IEntityTypeConfiguration<TagCategoryValue>
{
    public void Configure(EntityTypeBuilder<TagCategoryValue> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.ToTable("TagCategoryValues");
        builder.HasKey(x => new { x.TagCategoryId, x.Value });
        builder.Property(x => x.Value).IsRequired().HasMaxLength(128);
    }
}
```

Create `src/Modules/Proxies/Modules.Proxies/Data/Configurations/TagCategoryConfiguration.cs`:

```csharp
using FSH.Modules.Proxies.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FSH.Modules.Proxies.Data.Configurations;

public sealed class TagCategoryConfiguration : IEntityTypeConfiguration<TagCategory>
{
    public void Configure(EntityTypeBuilder<TagCategory> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.ToTable("TagCategories");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Name).IsRequired().HasMaxLength(128);
        builder.HasIndex(x => x.Name).IsUnique();
        builder.HasMany(x => x.Values).WithOne().HasForeignKey(x => x.TagCategoryId).OnDelete(DeleteBehavior.Cascade);
        builder.Metadata.FindNavigation(nameof(TagCategory.Values))!.SetPropertyAccessMode(PropertyAccessMode.Field);
        builder.Ignore(x => x.DomainEvents);
    }
}
```

In `src/Modules/Proxies/Modules.Proxies/Data/ProxiesDbContext.cs`, add next to the other `DbSet` properties:

```csharp
    public DbSet<TagCategory> TagCategories => Set<TagCategory>();
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test src/Tests/Proxies.Tests --filter "FullyQualifiedName~TagCategoryTests"`
Expected: PASS (all 5 facts)

- [ ] **Step 5: Build, then generate and review the migration**

```bash
dotnet build src/FS.Proxy.slnx
dotnet ef migrations add AddTagCategories \
  --project src/Host/FS.Proxy.Migrations.PostgreSQL \
  --startup-project src/Host/FS.Proxy.Api \
  --context ProxiesDbContext \
  --output-dir Proxies
dotnet ef migrations script --idempotent \
  --project src/Host/FS.Proxy.Migrations.PostgreSQL \
  --startup-project src/Host/FS.Proxy.Api \
  --context ProxiesDbContext
```

Confirm the generated script only **creates** two new tables (`TagCategories`, `TagCategoryValues` with its FK + cascade delete) — no drops, no changes to any existing table.

- [ ] **Step 6: Commit**

```bash
git add src/Modules/Proxies/Modules.Proxies/Domain/TagCategory.cs \
        src/Modules/Proxies/Modules.Proxies/Domain/TagCategoryValue.cs \
        src/Modules/Proxies/Modules.Proxies/Data/Configurations/TagCategoryConfiguration.cs \
        src/Modules/Proxies/Modules.Proxies/Data/Configurations/TagCategoryValueConfiguration.cs \
        src/Modules/Proxies/Modules.Proxies/Data/ProxiesDbContext.cs \
        src/Tests/Proxies.Tests/Domain/TagCategoryTests.cs \
        src/Host/FS.Proxy.Migrations.PostgreSQL/Proxies/
git commit -m "feat(proxies): add TagCategory/TagCategoryValue catalog entities

Co-Authored-By: Claude Sonnet 5 <noreply@anthropic.com>"
```

---

### Task 2: TagCategory CRUD backend

**Files:**
- Create: `src/Modules/Proxies/Modules.Proxies.Contracts/v1/TagCategories/CreateTagCategoryCommand.cs`
- Create: `src/Modules/Proxies/Modules.Proxies.Contracts/v1/TagCategories/UpdateTagCategoryCommand.cs`
- Create: `src/Modules/Proxies/Modules.Proxies.Contracts/v1/TagCategories/DeleteTagCategoryCommand.cs`
- Create: `src/Modules/Proxies/Modules.Proxies.Contracts/v1/TagCategories/AddTagCategoryValueCommand.cs`
- Create: `src/Modules/Proxies/Modules.Proxies.Contracts/v1/TagCategories/RemoveTagCategoryValueCommand.cs`
- Create: `src/Modules/Proxies/Modules.Proxies.Contracts/v1/TagCategories/ListTagCategoriesQuery.cs`
- Create: `src/Modules/Proxies/Modules.Proxies.Contracts/Dtos/TagCategoryDto.cs`
- Create: `src/Modules/Proxies/Modules.Proxies/Features/v1/TagCategories/CreateTagCategory/CreateTagCategoryCommandHandler.cs` + `CommandValidator.cs` + `Endpoint.cs`
- Create: `src/Modules/Proxies/Modules.Proxies/Features/v1/TagCategories/UpdateTagCategory/UpdateTagCategoryCommandHandler.cs` + `CommandValidator.cs` + `Endpoint.cs`
- Create: `src/Modules/Proxies/Modules.Proxies/Features/v1/TagCategories/DeleteTagCategory/DeleteTagCategoryCommandHandler.cs` + `CommandValidator.cs` + `Endpoint.cs`
- Create: `src/Modules/Proxies/Modules.Proxies/Features/v1/TagCategories/AddTagCategoryValue/AddTagCategoryValueCommandHandler.cs` + `CommandValidator.cs` + `Endpoint.cs`
- Create: `src/Modules/Proxies/Modules.Proxies/Features/v1/TagCategories/RemoveTagCategoryValue/RemoveTagCategoryValueCommandHandler.cs` + `CommandValidator.cs` + `Endpoint.cs`
- Create: `src/Modules/Proxies/Modules.Proxies/Features/v1/TagCategories/ListTagCategories/ListTagCategoriesQueryHandler.cs` + `Endpoint.cs`
- Modify: `src/Modules/Proxies/Modules.Proxies/ProxiesModule.cs`
- Test: `src/Tests/Proxies.Tests/Handlers/TagCategoryHandlerTests.cs`

**Interfaces:**
- Consumes: `TagCategory.Create/Rename/AddValue/RemoveValue`, `ProxiesDbContext.TagCategories` from Task 1.
- Produces: `TagCategoryDto(Guid Id, string Name, IReadOnlyList<string> Values)` — consumed by Task 4 (admin page) and Task 5 (individual tag editor).

- [ ] **Step 1: Write the failing tests**

Create `src/Tests/Proxies.Tests/Handlers/TagCategoryHandlerTests.cs`:

```csharp
using FSH.Framework.Core.Exceptions;
using FSH.Modules.Proxies.Contracts.v1.TagCategories;
using FSH.Modules.Proxies.Data;
using FSH.Modules.Proxies.Domain;
using FSH.Modules.Proxies.Features.v1.TagCategories.AddTagCategoryValue;
using FSH.Modules.Proxies.Features.v1.TagCategories.CreateTagCategory;
using FSH.Modules.Proxies.Features.v1.TagCategories.DeleteTagCategory;
using FSH.Modules.Proxies.Features.v1.TagCategories.ListTagCategories;
using FSH.Modules.Proxies.Features.v1.TagCategories.RemoveTagCategoryValue;
using FSH.Modules.Proxies.Features.v1.TagCategories.UpdateTagCategory;
using Microsoft.EntityFrameworkCore;
using Shouldly;
using Xunit;

namespace Proxies.Tests.Handlers;

public sealed class TagCategoryHandlerTests
{
    private static ProxiesDbContext CreateDb() =>
        Proxies.Tests.TestProxiesDbContext.Create(new DbContextOptionsBuilder<ProxiesDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    [Fact]
    public async Task Create_Should_Persist()
    {
        await using var db = CreateDb();
        var sut = new CreateTagCategoryCommandHandler(db);

        var id = await sut.Handle(new CreateTagCategoryCommand("pais"), CancellationToken.None);

        (await db.TagCategories.SingleAsync(x => x.Id == id)).Name.ShouldBe("pais");
    }

    [Fact]
    public async Task Update_Should_Rename()
    {
        await using var db = CreateDb();
        var category = TagCategory.Create("pais");
        db.TagCategories.Add(category);
        await db.SaveChangesAsync();
        var sut = new UpdateTagCategoryCommandHandler(db);

        await sut.Handle(new UpdateTagCategoryCommand(category.Id, "country"), CancellationToken.None);

        (await db.TagCategories.SingleAsync(x => x.Id == category.Id)).Name.ShouldBe("country");
    }

    [Fact]
    public async Task Delete_Should_Remove()
    {
        await using var db = CreateDb();
        var category = TagCategory.Create("pais");
        db.TagCategories.Add(category);
        await db.SaveChangesAsync();
        var sut = new DeleteTagCategoryCommandHandler(db);

        await sut.Handle(new DeleteTagCategoryCommand(category.Id), CancellationToken.None);

        (await db.TagCategories.AnyAsync(x => x.Id == category.Id)).ShouldBeFalse();
    }

    [Fact]
    public async Task Delete_Should_Throw_When_NotFound()
    {
        await using var db = CreateDb();
        var sut = new DeleteTagCategoryCommandHandler(db);

        await Should.ThrowAsync<NotFoundException>(() => sut.Handle(new DeleteTagCategoryCommand(Guid.NewGuid()), CancellationToken.None).AsTask());
    }

    [Fact]
    public async Task AddValue_Should_Append()
    {
        await using var db = CreateDb();
        var category = TagCategory.Create("pais");
        db.TagCategories.Add(category);
        await db.SaveChangesAsync();
        var sut = new AddTagCategoryValueCommandHandler(db);

        await sut.Handle(new AddTagCategoryValueCommand(category.Id, "cl"), CancellationToken.None);

        var reloaded = await db.TagCategories.Include(x => x.Values).SingleAsync(x => x.Id == category.Id);
        reloaded.Values.Select(v => v.Value).ShouldBe(["cl"]);
    }

    [Fact]
    public async Task RemoveValue_Should_Remove()
    {
        await using var db = CreateDb();
        var category = TagCategory.Create("pais");
        category.AddValue("cl");
        db.TagCategories.Add(category);
        await db.SaveChangesAsync();
        var sut = new RemoveTagCategoryValueCommandHandler(db);

        await sut.Handle(new RemoveTagCategoryValueCommand(category.Id, "cl"), CancellationToken.None);

        var reloaded = await db.TagCategories.Include(x => x.Values).SingleAsync(x => x.Id == category.Id);
        reloaded.Values.ShouldBeEmpty();
    }

    [Fact]
    public async Task List_Should_ReturnCategoriesWithValues_OrderedByName()
    {
        await using var db = CreateDb();
        var funcionalidad = TagCategory.Create("funcionalidad");
        funcionalidad.AddValue("licitaciones");
        var pais = TagCategory.Create("pais");
        pais.AddValue("cl");
        pais.AddValue("ar");
        db.TagCategories.AddRange(funcionalidad, pais);
        await db.SaveChangesAsync();
        var sut = new ListTagCategoriesQueryHandler(db);

        var result = await sut.Handle(new ListTagCategoriesQuery(), CancellationToken.None);

        result.Select(x => x.Name).ShouldBe(["funcionalidad", "pais"]);
        result.Single(x => x.Name == "pais").Values.ShouldBe(["ar", "cl"]);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test src/Tests/Proxies.Tests --filter "FullyQualifiedName~TagCategoryHandlerTests"`
Expected: FAIL — none of the commands/handlers exist yet (compile error).

- [ ] **Step 3: Implement the Contracts**

Create `src/Modules/Proxies/Modules.Proxies.Contracts/v1/TagCategories/CreateTagCategoryCommand.cs`:

```csharp
using Mediator;

namespace FSH.Modules.Proxies.Contracts.v1.TagCategories;

public sealed record CreateTagCategoryCommand(string Name) : ICommand<Guid>;
```

Create `src/Modules/Proxies/Modules.Proxies.Contracts/v1/TagCategories/UpdateTagCategoryCommand.cs`:

```csharp
using Mediator;

namespace FSH.Modules.Proxies.Contracts.v1.TagCategories;

public sealed record UpdateTagCategoryCommand(Guid Id, string Name) : ICommand;
```

Create `src/Modules/Proxies/Modules.Proxies.Contracts/v1/TagCategories/DeleteTagCategoryCommand.cs`:

```csharp
using Mediator;

namespace FSH.Modules.Proxies.Contracts.v1.TagCategories;

public sealed record DeleteTagCategoryCommand(Guid Id) : ICommand;
```

Create `src/Modules/Proxies/Modules.Proxies.Contracts/v1/TagCategories/AddTagCategoryValueCommand.cs`:

```csharp
using Mediator;

namespace FSH.Modules.Proxies.Contracts.v1.TagCategories;

public sealed record AddTagCategoryValueCommand(Guid TagCategoryId, string Value) : ICommand;
```

Create `src/Modules/Proxies/Modules.Proxies.Contracts/v1/TagCategories/RemoveTagCategoryValueCommand.cs`:

```csharp
using Mediator;

namespace FSH.Modules.Proxies.Contracts.v1.TagCategories;

public sealed record RemoveTagCategoryValueCommand(Guid TagCategoryId, string Value) : ICommand;
```

Create `src/Modules/Proxies/Modules.Proxies.Contracts/v1/TagCategories/ListTagCategoriesQuery.cs`:

```csharp
using FSH.Modules.Proxies.Contracts.Dtos;
using Mediator;

namespace FSH.Modules.Proxies.Contracts.v1.TagCategories;

public sealed record ListTagCategoriesQuery : IQuery<IReadOnlyList<TagCategoryDto>>;
```

Create `src/Modules/Proxies/Modules.Proxies.Contracts/Dtos/TagCategoryDto.cs`:

```csharp
namespace FSH.Modules.Proxies.Contracts.Dtos;

public sealed record TagCategoryDto(Guid Id, string Name, IReadOnlyList<string> Values);
```

- [ ] **Step 4: Implement the handlers, validators, and endpoints**

Create `src/Modules/Proxies/Modules.Proxies/Features/v1/TagCategories/CreateTagCategory/CreateTagCategoryCommandHandler.cs`:

```csharp
using FSH.Modules.Proxies.Contracts.v1.TagCategories;
using FSH.Modules.Proxies.Data;
using FSH.Modules.Proxies.Domain;
using Mediator;

namespace FSH.Modules.Proxies.Features.v1.TagCategories.CreateTagCategory;

public sealed class CreateTagCategoryCommandHandler(ProxiesDbContext dbContext) : ICommandHandler<CreateTagCategoryCommand, Guid>
{
    public async ValueTask<Guid> Handle(CreateTagCategoryCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        var category = TagCategory.Create(command.Name);
        dbContext.TagCategories.Add(category);
        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return category.Id;
    }
}
```

Create `src/Modules/Proxies/Modules.Proxies/Features/v1/TagCategories/CreateTagCategory/CreateTagCategoryCommandValidator.cs`:

```csharp
using FluentValidation;
using FSH.Modules.Proxies.Contracts.v1.TagCategories;

namespace FSH.Modules.Proxies.Features.v1.TagCategories.CreateTagCategory;

public sealed class CreateTagCategoryCommandValidator : AbstractValidator<CreateTagCategoryCommand>
{
    public CreateTagCategoryCommandValidator() => RuleFor(x => x.Name).NotEmpty().MaximumLength(128);
}
```

Create `src/Modules/Proxies/Modules.Proxies/Features/v1/TagCategories/CreateTagCategory/CreateTagCategoryEndpoint.cs`:

```csharp
using FSH.Framework.Shared.Identity.Authorization;
using FSH.Modules.Proxies.Contracts.Authorization;
using FSH.Modules.Proxies.Contracts.v1.TagCategories;
using Mediator;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace FSH.Modules.Proxies.Features.v1.TagCategories.CreateTagCategory;

public static class CreateTagCategoryEndpoint
{
    internal static RouteHandlerBuilder MapCreateTagCategoryEndpoint(this IEndpointRouteBuilder endpoints) =>
        endpoints.MapPost("/tag-categories", async (CreateTagCategoryCommand command, IMediator mediator, CancellationToken ct) => Results.Ok(await mediator.Send(command, ct)))
            .WithName("CreateTagCategory").WithSummary("Create a tag category")
            .RequirePermission(ProxiesPermissions.Tags.Create);
}
```

Create `src/Modules/Proxies/Modules.Proxies/Features/v1/TagCategories/UpdateTagCategory/UpdateTagCategoryCommandHandler.cs`:

```csharp
using FSH.Framework.Core.Exceptions;
using FSH.Modules.Proxies.Contracts.v1.TagCategories;
using FSH.Modules.Proxies.Data;
using Mediator;
using Microsoft.EntityFrameworkCore;

namespace FSH.Modules.Proxies.Features.v1.TagCategories.UpdateTagCategory;

public sealed class UpdateTagCategoryCommandHandler(ProxiesDbContext dbContext) : ICommandHandler<UpdateTagCategoryCommand>
{
    public async ValueTask<Unit> Handle(UpdateTagCategoryCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        var category = await dbContext.TagCategories.FirstOrDefaultAsync(x => x.Id == command.Id, cancellationToken).ConfigureAwait(false)
            ?? throw new NotFoundException($"Tag category {command.Id} not found.");
        category.Rename(command.Name);
        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return Unit.Value;
    }
}
```

Create `src/Modules/Proxies/Modules.Proxies/Features/v1/TagCategories/UpdateTagCategory/UpdateTagCategoryCommandValidator.cs`:

```csharp
using FluentValidation;
using FSH.Modules.Proxies.Contracts.v1.TagCategories;

namespace FSH.Modules.Proxies.Features.v1.TagCategories.UpdateTagCategory;

public sealed class UpdateTagCategoryCommandValidator : AbstractValidator<UpdateTagCategoryCommand>
{
    public UpdateTagCategoryCommandValidator()
    {
        RuleFor(x => x.Id).NotEmpty();
        RuleFor(x => x.Name).NotEmpty().MaximumLength(128);
    }
}
```

Create `src/Modules/Proxies/Modules.Proxies/Features/v1/TagCategories/UpdateTagCategory/UpdateTagCategoryEndpoint.cs`:

```csharp
using FSH.Framework.Shared.Identity.Authorization;
using FSH.Modules.Proxies.Contracts.Authorization;
using FSH.Modules.Proxies.Contracts.v1.TagCategories;
using Mediator;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace FSH.Modules.Proxies.Features.v1.TagCategories.UpdateTagCategory;

public static class UpdateTagCategoryEndpoint
{
    internal static RouteHandlerBuilder MapUpdateTagCategoryEndpoint(this IEndpointRouteBuilder endpoints) =>
        endpoints.MapPut("/tag-categories/{id:guid}", async (Guid id, UpdateTagCategoryBody body, IMediator mediator, CancellationToken ct) =>
            {
                await mediator.Send(new UpdateTagCategoryCommand(id, body.Name), ct);
                return Results.NoContent();
            })
            .WithName("UpdateTagCategory").WithSummary("Rename a tag category")
            .RequirePermission(ProxiesPermissions.Tags.Update);

    internal sealed record UpdateTagCategoryBody(string Name);
}
```

Create `src/Modules/Proxies/Modules.Proxies/Features/v1/TagCategories/DeleteTagCategory/DeleteTagCategoryCommandHandler.cs`:

```csharp
using FSH.Framework.Core.Exceptions;
using FSH.Modules.Proxies.Contracts.v1.TagCategories;
using FSH.Modules.Proxies.Data;
using Mediator;
using Microsoft.EntityFrameworkCore;

namespace FSH.Modules.Proxies.Features.v1.TagCategories.DeleteTagCategory;

public sealed class DeleteTagCategoryCommandHandler(ProxiesDbContext dbContext) : ICommandHandler<DeleteTagCategoryCommand>
{
    public async ValueTask<Unit> Handle(DeleteTagCategoryCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        var category = await dbContext.TagCategories.FirstOrDefaultAsync(x => x.Id == command.Id, cancellationToken).ConfigureAwait(false)
            ?? throw new NotFoundException($"Tag category {command.Id} not found.");
        dbContext.TagCategories.Remove(category);
        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return Unit.Value;
    }
}
```

Create `src/Modules/Proxies/Modules.Proxies/Features/v1/TagCategories/DeleteTagCategory/DeleteTagCategoryCommandValidator.cs`:

```csharp
using FluentValidation;
using FSH.Modules.Proxies.Contracts.v1.TagCategories;

namespace FSH.Modules.Proxies.Features.v1.TagCategories.DeleteTagCategory;

public sealed class DeleteTagCategoryCommandValidator : AbstractValidator<DeleteTagCategoryCommand>
{
    public DeleteTagCategoryCommandValidator() => RuleFor(x => x.Id).NotEmpty();
}
```

Create `src/Modules/Proxies/Modules.Proxies/Features/v1/TagCategories/DeleteTagCategory/DeleteTagCategoryEndpoint.cs`:

```csharp
using FSH.Framework.Shared.Identity.Authorization;
using FSH.Modules.Proxies.Contracts.Authorization;
using FSH.Modules.Proxies.Contracts.v1.TagCategories;
using Mediator;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace FSH.Modules.Proxies.Features.v1.TagCategories.DeleteTagCategory;

public static class DeleteTagCategoryEndpoint
{
    internal static RouteHandlerBuilder MapDeleteTagCategoryEndpoint(this IEndpointRouteBuilder endpoints) =>
        endpoints.MapDelete("/tag-categories/{id:guid}", async (Guid id, IMediator mediator, CancellationToken ct) => { await mediator.Send(new DeleteTagCategoryCommand(id), ct); return Results.NoContent(); })
            .WithName("DeleteTagCategory").WithSummary("Delete a tag category")
            .RequirePermission(ProxiesPermissions.Tags.Delete);
}
```

Create `src/Modules/Proxies/Modules.Proxies/Features/v1/TagCategories/AddTagCategoryValue/AddTagCategoryValueCommandHandler.cs`:

```csharp
using FSH.Framework.Core.Exceptions;
using FSH.Modules.Proxies.Contracts.v1.TagCategories;
using FSH.Modules.Proxies.Data;
using Mediator;
using Microsoft.EntityFrameworkCore;

namespace FSH.Modules.Proxies.Features.v1.TagCategories.AddTagCategoryValue;

public sealed class AddTagCategoryValueCommandHandler(ProxiesDbContext dbContext) : ICommandHandler<AddTagCategoryValueCommand>
{
    public async ValueTask<Unit> Handle(AddTagCategoryValueCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        var category = await dbContext.TagCategories.Include(x => x.Values)
            .FirstOrDefaultAsync(x => x.Id == command.TagCategoryId, cancellationToken).ConfigureAwait(false)
            ?? throw new NotFoundException($"Tag category {command.TagCategoryId} not found.");
        category.AddValue(command.Value);
        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return Unit.Value;
    }
}
```

Create `src/Modules/Proxies/Modules.Proxies/Features/v1/TagCategories/AddTagCategoryValue/AddTagCategoryValueCommandValidator.cs`:

```csharp
using FluentValidation;
using FSH.Modules.Proxies.Contracts.v1.TagCategories;

namespace FSH.Modules.Proxies.Features.v1.TagCategories.AddTagCategoryValue;

public sealed class AddTagCategoryValueCommandValidator : AbstractValidator<AddTagCategoryValueCommand>
{
    public AddTagCategoryValueCommandValidator()
    {
        RuleFor(x => x.TagCategoryId).NotEmpty();
        RuleFor(x => x.Value).NotEmpty().MaximumLength(128);
    }
}
```

Create `src/Modules/Proxies/Modules.Proxies/Features/v1/TagCategories/AddTagCategoryValue/AddTagCategoryValueEndpoint.cs`:

```csharp
using FSH.Framework.Shared.Identity.Authorization;
using FSH.Modules.Proxies.Contracts.Authorization;
using FSH.Modules.Proxies.Contracts.v1.TagCategories;
using Mediator;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace FSH.Modules.Proxies.Features.v1.TagCategories.AddTagCategoryValue;

public static class AddTagCategoryValueEndpoint
{
    internal static RouteHandlerBuilder MapAddTagCategoryValueEndpoint(this IEndpointRouteBuilder endpoints) =>
        endpoints.MapPost("/tag-categories/{id:guid}/values", async (Guid id, AddTagCategoryValueBody body, IMediator mediator, CancellationToken ct) =>
            {
                await mediator.Send(new AddTagCategoryValueCommand(id, body.Value), ct);
                return Results.NoContent();
            })
            .WithName("AddTagCategoryValue").WithSummary("Add a value to a tag category")
            .RequirePermission(ProxiesPermissions.Tags.Update);

    internal sealed record AddTagCategoryValueBody(string Value);
}
```

Create `src/Modules/Proxies/Modules.Proxies/Features/v1/TagCategories/RemoveTagCategoryValue/RemoveTagCategoryValueCommandHandler.cs`:

```csharp
using FSH.Framework.Core.Exceptions;
using FSH.Modules.Proxies.Contracts.v1.TagCategories;
using FSH.Modules.Proxies.Data;
using Mediator;
using Microsoft.EntityFrameworkCore;

namespace FSH.Modules.Proxies.Features.v1.TagCategories.RemoveTagCategoryValue;

public sealed class RemoveTagCategoryValueCommandHandler(ProxiesDbContext dbContext) : ICommandHandler<RemoveTagCategoryValueCommand>
{
    public async ValueTask<Unit> Handle(RemoveTagCategoryValueCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        var category = await dbContext.TagCategories.Include(x => x.Values)
            .FirstOrDefaultAsync(x => x.Id == command.TagCategoryId, cancellationToken).ConfigureAwait(false)
            ?? throw new NotFoundException($"Tag category {command.TagCategoryId} not found.");
        category.RemoveValue(command.Value);
        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return Unit.Value;
    }
}
```

Create `src/Modules/Proxies/Modules.Proxies/Features/v1/TagCategories/RemoveTagCategoryValue/RemoveTagCategoryValueCommandValidator.cs`:

```csharp
using FluentValidation;
using FSH.Modules.Proxies.Contracts.v1.TagCategories;

namespace FSH.Modules.Proxies.Features.v1.TagCategories.RemoveTagCategoryValue;

public sealed class RemoveTagCategoryValueCommandValidator : AbstractValidator<RemoveTagCategoryValueCommand>
{
    public RemoveTagCategoryValueCommandValidator()
    {
        RuleFor(x => x.TagCategoryId).NotEmpty();
        RuleFor(x => x.Value).NotEmpty();
    }
}
```

Create `src/Modules/Proxies/Modules.Proxies/Features/v1/TagCategories/RemoveTagCategoryValue/RemoveTagCategoryValueEndpoint.cs`:

```csharp
using FSH.Framework.Shared.Identity.Authorization;
using FSH.Modules.Proxies.Contracts.Authorization;
using FSH.Modules.Proxies.Contracts.v1.TagCategories;
using Mediator;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace FSH.Modules.Proxies.Features.v1.TagCategories.RemoveTagCategoryValue;

public static class RemoveTagCategoryValueEndpoint
{
    internal static RouteHandlerBuilder MapRemoveTagCategoryValueEndpoint(this IEndpointRouteBuilder endpoints) =>
        endpoints.MapDelete("/tag-categories/{id:guid}/values/{value}", async (Guid id, string value, IMediator mediator, CancellationToken ct) =>
            {
                await mediator.Send(new RemoveTagCategoryValueCommand(id, value), ct);
                return Results.NoContent();
            })
            .WithName("RemoveTagCategoryValue").WithSummary("Remove a value from a tag category")
            .RequirePermission(ProxiesPermissions.Tags.Update);
}
```

Create `src/Modules/Proxies/Modules.Proxies/Features/v1/TagCategories/ListTagCategories/ListTagCategoriesQueryHandler.cs`:

```csharp
using FSH.Modules.Proxies.Contracts.Dtos;
using FSH.Modules.Proxies.Contracts.v1.TagCategories;
using FSH.Modules.Proxies.Data;
using Mediator;
using Microsoft.EntityFrameworkCore;

namespace FSH.Modules.Proxies.Features.v1.TagCategories.ListTagCategories;

public sealed class ListTagCategoriesQueryHandler(ProxiesDbContext dbContext) : IQueryHandler<ListTagCategoriesQuery, IReadOnlyList<TagCategoryDto>>
{
    public async ValueTask<IReadOnlyList<TagCategoryDto>> Handle(ListTagCategoriesQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        return await dbContext.TagCategories.AsNoTracking().Include(x => x.Values).OrderBy(x => x.Name)
            .Select(x => new TagCategoryDto(x.Id, x.Name, x.Values.OrderBy(v => v.Value).Select(v => v.Value).ToList()))
            .ToListAsync(cancellationToken).ConfigureAwait(false);
    }
}
```

Create `src/Modules/Proxies/Modules.Proxies/Features/v1/TagCategories/ListTagCategories/ListTagCategoriesEndpoint.cs`:

```csharp
using FSH.Framework.Shared.Identity.Authorization;
using FSH.Modules.Proxies.Contracts.Authorization;
using FSH.Modules.Proxies.Contracts.v1.TagCategories;
using Mediator;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace FSH.Modules.Proxies.Features.v1.TagCategories.ListTagCategories;

public static class ListTagCategoriesEndpoint
{
    internal static RouteHandlerBuilder MapListTagCategoriesEndpoint(this IEndpointRouteBuilder endpoints) =>
        endpoints.MapGet("/tag-categories", (IMediator mediator, CancellationToken ct) => mediator.Send(new ListTagCategoriesQuery(), ct))
            .WithName("ListTagCategories").WithSummary("List tag categories with their values")
            .RequirePermission(ProxiesPermissions.Tags.View);
}
```

In `src/Modules/Proxies/Modules.Proxies/ProxiesModule.cs`, inside `MapEndpoints`, add after the existing `group.MapListTagsEndpoint();` line:

```csharp
        group.MapCreateTagCategoryEndpoint();
        group.MapUpdateTagCategoryEndpoint();
        group.MapDeleteTagCategoryEndpoint();
        group.MapAddTagCategoryValueEndpoint();
        group.MapRemoveTagCategoryValueEndpoint();
        group.MapListTagCategoriesEndpoint();
```

- [ ] **Step 5: Run tests to verify they pass**

Run: `dotnet test src/Tests/Proxies.Tests --filter "FullyQualifiedName~TagCategoryHandlerTests"`
Expected: PASS (all 7 facts)

- [ ] **Step 6: Full backend build + test run**

```bash
dotnet build src/FS.Proxy.slnx
dotnet test src/Tests/Proxies.Tests
```

Expected: clean build (0 warnings), all `Proxies.Tests` pass. Also run `dotnet test src/Tests/Architecture.Tests --filter "FullyQualifiedName~EndpointConventionTests"` — expected PASS, confirming every new `*Endpoint` class name starts with an approved verb (`Create`, `Update`, `Delete`, `Add`, `Remove`, `List`).

- [ ] **Step 7: Commit**

```bash
git add src/Modules/Proxies/Modules.Proxies.Contracts/v1/TagCategories/ \
        src/Modules/Proxies/Modules.Proxies.Contracts/Dtos/TagCategoryDto.cs \
        src/Modules/Proxies/Modules.Proxies/Features/v1/TagCategories/ \
        src/Modules/Proxies/Modules.Proxies/ProxiesModule.cs \
        src/Tests/Proxies.Tests/Handlers/TagCategoryHandlerTests.cs
git commit -m "feat(proxies): add TagCategory CRUD (create/rename/delete/add-value/remove-value/list)

Co-Authored-By: Claude Sonnet 5 <noreply@anthropic.com>"
```

---

### Task 3: Proxy tag-assignment backend (individual + multi-proxy)

**Files:**
- Create: `src/Modules/Proxies/Modules.Proxies.Contracts/v1/Proxies/SetProxyTagsCommand.cs`
- Create: `src/Modules/Proxies/Modules.Proxies.Contracts/v1/Proxies/AssignProxyTagCommand.cs`
- Create: `src/Modules/Proxies/Modules.Proxies.Contracts/v1/Proxies/UnassignProxyTagCommand.cs`
- Create: `src/Modules/Proxies/Modules.Proxies/Features/v1/Proxies/SetProxyTags/SetProxyTagsCommandHandler.cs` + `CommandValidator.cs` + `Endpoint.cs`
- Create: `src/Modules/Proxies/Modules.Proxies/Features/v1/Proxies/AssignProxyTag/AssignProxyTagCommandHandler.cs` + `CommandValidator.cs` + `Endpoint.cs`
- Create: `src/Modules/Proxies/Modules.Proxies/Features/v1/Proxies/UnassignProxyTag/UnassignProxyTagCommandHandler.cs` + `CommandValidator.cs` + `Endpoint.cs`
- Modify: `src/Modules/Proxies/Modules.Proxies/ProxiesModule.cs`
- Test: `src/Tests/Proxies.Tests/Handlers/ProxyTagAssignmentHandlerTests.cs`

**Interfaces:**
- Consumes: `CreateManualProxyCommandHandler.ResolveTagIdsAsync(ProxiesDbContext, IReadOnlyList<string>, CancellationToken) : Task<List<Guid>>` (already `internal static`, in the same assembly — no signature change), `Proxy.AssignTag(Guid)`/`UnassignTag(Guid)` (already exist), `Tag.Normalize(string)` (already exists).
- Produces: nothing consumed by later tasks in this plan beyond the HTTP endpoints themselves (Tasks 5 and 6 call these by URL, not by C# type).

- [ ] **Step 1: Write the failing tests**

Create `src/Tests/Proxies.Tests/Handlers/ProxyTagAssignmentHandlerTests.cs`:

```csharp
using FSH.Modules.Proxies.Contracts;
using FSH.Modules.Proxies.Contracts.v1.Proxies;
using FSH.Modules.Proxies.Data;
using FSH.Modules.Proxies.Domain;
using FSH.Modules.Proxies.Features.v1.Proxies.AssignProxyTag;
using FSH.Modules.Proxies.Features.v1.Proxies.SetProxyTags;
using FSH.Modules.Proxies.Features.v1.Proxies.UnassignProxyTag;
using Microsoft.EntityFrameworkCore;
using Shouldly;
using Xunit;

namespace Proxies.Tests.Handlers;

public sealed class ProxyTagAssignmentHandlerTests
{
    private static ProxiesDbContext CreateDb() =>
        Proxies.Tests.TestProxiesDbContext.Create(new DbContextOptionsBuilder<ProxiesDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    [Fact]
    public async Task SetProxyTags_Should_ReplaceFullTagSet()
    {
        await using var db = CreateDb();
        var account = ProviderAccount.Create("WebShare", ProxyProviderType.WebShare, "{}");
        var oldTag = Tag.Create("old-tag");
        var proxy = Proxy.Create(account.Id, "1.2.3.4", 8080, ProxyProtocol.Http, null, null, null);
        proxy.AssignTag(oldTag.Id);
        db.ProviderAccounts.Add(account);
        db.Tags.Add(oldTag);
        db.Proxies.Add(proxy);
        await db.SaveChangesAsync();
        var sut = new SetProxyTagsCommandHandler(db);

        await sut.Handle(new SetProxyTagsCommand(proxy.Id, ["pais:cl", "funcionalidad:licitaciones"]), CancellationToken.None);

        var reloaded = await db.Proxies.Include(x => x.TagAssignments).SingleAsync(x => x.Id == proxy.Id);
        var tagNames = await db.Tags.Where(t => reloaded.TagAssignments.Select(a => a.TagId).Contains(t.Id)).Select(t => t.Name).ToListAsync();
        tagNames.ShouldBe(["funcionalidad:licitaciones", "pais:cl"], ignoreOrder: true);
    }

    [Fact]
    public async Task AssignProxyTag_Should_CreateTagAndAssignToEveryProxy_WithoutTouchingExistingTags()
    {
        await using var db = CreateDb();
        var account = ProviderAccount.Create("WebShare", ProxyProviderType.WebShare, "{}");
        var existingTag = Tag.Create("keep-me");
        var proxy1 = Proxy.Create(account.Id, "1.1.1.1", 80, ProxyProtocol.Http, null, null, null);
        proxy1.AssignTag(existingTag.Id);
        var proxy2 = Proxy.Create(account.Id, "2.2.2.2", 80, ProxyProtocol.Http, null, null, null);
        db.ProviderAccounts.Add(account);
        db.Tags.Add(existingTag);
        db.Proxies.AddRange(proxy1, proxy2);
        await db.SaveChangesAsync();
        var sut = new AssignProxyTagCommandHandler(db);

        var touched = await sut.Handle(new AssignProxyTagCommand([proxy1.Id, proxy2.Id], "pais:cl"), CancellationToken.None);

        touched.ShouldBe(2);
        var newTag = await db.Tags.SingleAsync(t => t.Name == "pais:cl");
        var p1 = await db.Proxies.Include(x => x.TagAssignments).SingleAsync(x => x.Id == proxy1.Id);
        p1.TagAssignments.Select(a => a.TagId).ShouldContain(existingTag.Id);
        p1.TagAssignments.Select(a => a.TagId).ShouldContain(newTag.Id);
    }

    [Fact]
    public async Task UnassignProxyTag_Should_RemoveFromEveryProxy_And_ReturnZero_When_TagUnknown()
    {
        await using var db = CreateDb();
        var account = ProviderAccount.Create("WebShare", ProxyProviderType.WebShare, "{}");
        var tag = Tag.Create("pais:cl");
        var proxy = Proxy.Create(account.Id, "1.1.1.1", 80, ProxyProtocol.Http, null, null, null);
        proxy.AssignTag(tag.Id);
        db.ProviderAccounts.Add(account);
        db.Tags.Add(tag);
        db.Proxies.Add(proxy);
        await db.SaveChangesAsync();
        var sut = new UnassignProxyTagCommandHandler(db);

        var touched = await sut.Handle(new UnassignProxyTagCommand([proxy.Id], "pais:cl"), CancellationToken.None);

        touched.ShouldBe(1);
        (await db.Proxies.Include(x => x.TagAssignments).SingleAsync(x => x.Id == proxy.Id)).TagAssignments.ShouldBeEmpty();

        var unknownTagTouched = await sut.Handle(new UnassignProxyTagCommand([proxy.Id], "no-such-tag"), CancellationToken.None);
        unknownTagTouched.ShouldBe(0);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test src/Tests/Proxies.Tests --filter "FullyQualifiedName~ProxyTagAssignmentHandlerTests"`
Expected: FAIL — none of the 3 commands/handlers exist yet (compile error).

- [ ] **Step 3: Implement the Contracts**

Create `src/Modules/Proxies/Modules.Proxies.Contracts/v1/Proxies/SetProxyTagsCommand.cs`:

```csharp
using Mediator;

namespace FSH.Modules.Proxies.Contracts.v1.Proxies;

public sealed record SetProxyTagsCommand(Guid ProxyId, IReadOnlyList<string> TagNames) : ICommand;
```

Create `src/Modules/Proxies/Modules.Proxies.Contracts/v1/Proxies/AssignProxyTagCommand.cs`:

```csharp
using Mediator;

namespace FSH.Modules.Proxies.Contracts.v1.Proxies;

public sealed record AssignProxyTagCommand(IReadOnlyList<Guid> ProxyIds, string TagName) : ICommand<int>;
```

Create `src/Modules/Proxies/Modules.Proxies.Contracts/v1/Proxies/UnassignProxyTagCommand.cs`:

```csharp
using Mediator;

namespace FSH.Modules.Proxies.Contracts.v1.Proxies;

public sealed record UnassignProxyTagCommand(IReadOnlyList<Guid> ProxyIds, string TagName) : ICommand<int>;
```

- [ ] **Step 4: Implement the handlers, validators, and endpoints**

Create `src/Modules/Proxies/Modules.Proxies/Features/v1/Proxies/SetProxyTags/SetProxyTagsCommandHandler.cs`:

```csharp
using FSH.Framework.Core.Exceptions;
using FSH.Modules.Proxies.Contracts.v1.Proxies;
using FSH.Modules.Proxies.Data;
using FSH.Modules.Proxies.Features.v1.ManualProxies.CreateManualProxy;
using Mediator;
using Microsoft.EntityFrameworkCore;

namespace FSH.Modules.Proxies.Features.v1.Proxies.SetProxyTags;

/// <summary>
/// Full-replace tag assignment for one proxy — mirrors UpdateManualProxyCommandHandler's own
/// tag-diff logic exactly, generalized to every proxy (not just manual ones).
/// </summary>
public sealed class SetProxyTagsCommandHandler(ProxiesDbContext dbContext) : ICommandHandler<SetProxyTagsCommand>
{
    public async ValueTask<Unit> Handle(SetProxyTagsCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        var proxy = await dbContext.Proxies.Include(x => x.TagAssignments)
            .FirstOrDefaultAsync(x => x.Id == command.ProxyId, cancellationToken).ConfigureAwait(false)
            ?? throw new NotFoundException($"Proxy {command.ProxyId} not found.");

        var newTagIds = await CreateManualProxyCommandHandler.ResolveTagIdsAsync(dbContext, command.TagNames, cancellationToken).ConfigureAwait(false);

        foreach (var tagId in proxy.TagAssignments.Select(a => a.TagId).Except(newTagIds).ToList())
        {
            proxy.UnassignTag(tagId);
        }
        foreach (var tagId in newTagIds)
        {
            proxy.AssignTag(tagId);
        }

        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return Unit.Value;
    }
}
```

Create `src/Modules/Proxies/Modules.Proxies/Features/v1/Proxies/SetProxyTags/SetProxyTagsCommandValidator.cs`:

```csharp
using FluentValidation;
using FSH.Modules.Proxies.Contracts.v1.Proxies;

namespace FSH.Modules.Proxies.Features.v1.Proxies.SetProxyTags;

public sealed class SetProxyTagsCommandValidator : AbstractValidator<SetProxyTagsCommand>
{
    public SetProxyTagsCommandValidator()
    {
        RuleFor(x => x.ProxyId).NotEmpty();
        RuleFor(x => x.TagNames).NotNull();
    }
}
```

Create `src/Modules/Proxies/Modules.Proxies/Features/v1/Proxies/SetProxyTags/SetProxyTagsEndpoint.cs`:

```csharp
using FSH.Framework.Shared.Identity.Authorization;
using FSH.Modules.Proxies.Contracts.Authorization;
using FSH.Modules.Proxies.Contracts.v1.Proxies;
using Mediator;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace FSH.Modules.Proxies.Features.v1.Proxies.SetProxyTags;

public static class SetProxyTagsEndpoint
{
    internal static RouteHandlerBuilder MapSetProxyTagsEndpoint(this IEndpointRouteBuilder endpoints) =>
        endpoints.MapPut("/{id:guid}/tags", async (Guid id, SetProxyTagsBody body, IMediator mediator, CancellationToken ct) =>
            {
                await mediator.Send(new SetProxyTagsCommand(id, body.TagNames), ct);
                return Results.NoContent();
            })
            .WithName("SetProxyTags").WithSummary("Replace a proxy's full tag set")
            .RequirePermission(ProxiesPermissions.Tags.Update);

    internal sealed record SetProxyTagsBody(IReadOnlyList<string> TagNames);
}
```

Create `src/Modules/Proxies/Modules.Proxies/Features/v1/Proxies/AssignProxyTag/AssignProxyTagCommandHandler.cs`:

```csharp
using FSH.Modules.Proxies.Contracts.v1.Proxies;
using FSH.Modules.Proxies.Data;
using FSH.Modules.Proxies.Features.v1.ManualProxies.CreateManualProxy;
using Mediator;
using Microsoft.EntityFrameworkCore;

namespace FSH.Modules.Proxies.Features.v1.Proxies.AssignProxyTag;

public sealed class AssignProxyTagCommandHandler(ProxiesDbContext dbContext) : ICommandHandler<AssignProxyTagCommand, int>
{
    public async ValueTask<int> Handle(AssignProxyTagCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        var tagIds = await CreateManualProxyCommandHandler.ResolveTagIdsAsync(dbContext, [command.TagName], cancellationToken).ConfigureAwait(false);
        var tagId = tagIds[0];

        var proxies = await dbContext.Proxies.Include(x => x.TagAssignments)
            .Where(p => command.ProxyIds.Contains(p.Id)).ToListAsync(cancellationToken).ConfigureAwait(false);
        foreach (var proxy in proxies)
        {
            proxy.AssignTag(tagId);
        }

        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return proxies.Count;
    }
}
```

Create `src/Modules/Proxies/Modules.Proxies/Features/v1/Proxies/AssignProxyTag/AssignProxyTagCommandValidator.cs`:

```csharp
using FluentValidation;
using FSH.Modules.Proxies.Contracts.v1.Proxies;

namespace FSH.Modules.Proxies.Features.v1.Proxies.AssignProxyTag;

public sealed class AssignProxyTagCommandValidator : AbstractValidator<AssignProxyTagCommand>
{
    public AssignProxyTagCommandValidator()
    {
        RuleFor(x => x.ProxyIds).NotEmpty();
        RuleFor(x => x.TagName).NotEmpty().MaximumLength(255);
    }
}
```

Create `src/Modules/Proxies/Modules.Proxies/Features/v1/Proxies/AssignProxyTag/AssignProxyTagEndpoint.cs`:

```csharp
using FSH.Framework.Shared.Identity.Authorization;
using FSH.Modules.Proxies.Contracts.Authorization;
using FSH.Modules.Proxies.Contracts.v1.Proxies;
using Mediator;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace FSH.Modules.Proxies.Features.v1.Proxies.AssignProxyTag;

public static class AssignProxyTagEndpoint
{
    internal static RouteHandlerBuilder MapAssignProxyTagEndpoint(this IEndpointRouteBuilder endpoints) =>
        endpoints.MapPost("/tags/assign", (AssignProxyTagCommand command, IMediator mediator, CancellationToken ct) => mediator.Send(command, ct))
            .WithName("AssignProxyTag").WithSummary("Assign a tag to one or more proxies")
            .RequirePermission(ProxiesPermissions.Tags.Update);
}
```

Create `src/Modules/Proxies/Modules.Proxies/Features/v1/Proxies/UnassignProxyTag/UnassignProxyTagCommandHandler.cs`:

```csharp
using FSH.Modules.Proxies.Contracts.v1.Proxies;
using FSH.Modules.Proxies.Data;
using FSH.Modules.Proxies.Domain;
using Mediator;
using Microsoft.EntityFrameworkCore;

namespace FSH.Modules.Proxies.Features.v1.Proxies.UnassignProxyTag;

public sealed class UnassignProxyTagCommandHandler(ProxiesDbContext dbContext) : ICommandHandler<UnassignProxyTagCommand, int>
{
    public async ValueTask<int> Handle(UnassignProxyTagCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        var normalized = Tag.Normalize(command.TagName);
        var tag = await dbContext.Tags.FirstOrDefaultAsync(t => t.Name == normalized, cancellationToken).ConfigureAwait(false);
        if (tag is null)
        {
            return 0;
        }

        var proxies = await dbContext.Proxies.Include(x => x.TagAssignments)
            .Where(p => command.ProxyIds.Contains(p.Id)).ToListAsync(cancellationToken).ConfigureAwait(false);
        int touched = 0;
        foreach (var proxy in proxies)
        {
            if (proxy.TagAssignments.Any(a => a.TagId == tag.Id))
            {
                proxy.UnassignTag(tag.Id);
                touched++;
            }
        }

        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return touched;
    }
}
```

Create `src/Modules/Proxies/Modules.Proxies/Features/v1/Proxies/UnassignProxyTag/UnassignProxyTagCommandValidator.cs`:

```csharp
using FluentValidation;
using FSH.Modules.Proxies.Contracts.v1.Proxies;

namespace FSH.Modules.Proxies.Features.v1.Proxies.UnassignProxyTag;

public sealed class UnassignProxyTagCommandValidator : AbstractValidator<UnassignProxyTagCommand>
{
    public UnassignProxyTagCommandValidator()
    {
        RuleFor(x => x.ProxyIds).NotEmpty();
        RuleFor(x => x.TagName).NotEmpty().MaximumLength(255);
    }
}
```

Create `src/Modules/Proxies/Modules.Proxies/Features/v1/Proxies/UnassignProxyTag/UnassignProxyTagEndpoint.cs`:

```csharp
using FSH.Framework.Shared.Identity.Authorization;
using FSH.Modules.Proxies.Contracts.Authorization;
using FSH.Modules.Proxies.Contracts.v1.Proxies;
using Mediator;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace FSH.Modules.Proxies.Features.v1.Proxies.UnassignProxyTag;

public static class UnassignProxyTagEndpoint
{
    internal static RouteHandlerBuilder MapUnassignProxyTagEndpoint(this IEndpointRouteBuilder endpoints) =>
        endpoints.MapPost("/tags/unassign", (UnassignProxyTagCommand command, IMediator mediator, CancellationToken ct) => mediator.Send(command, ct))
            .WithName("UnassignProxyTag").WithSummary("Unassign a tag from one or more proxies")
            .RequirePermission(ProxiesPermissions.Tags.Update);
}
```

In `src/Modules/Proxies/Modules.Proxies/ProxiesModule.cs`, inside `MapEndpoints`, add after the existing `group.MapReportProxyFeedbackEndpoint();` line (or wherever the `Proxies`-group endpoints end):

```csharp
        group.MapSetProxyTagsEndpoint();
        group.MapAssignProxyTagEndpoint();
        group.MapUnassignProxyTagEndpoint();
```

- [ ] **Step 5: Run tests to verify they pass**

Run: `dotnet test src/Tests/Proxies.Tests --filter "FullyQualifiedName~ProxyTagAssignmentHandlerTests"`
Expected: PASS (all 3 facts)

- [ ] **Step 6: Full backend build + test run**

```bash
dotnet build src/FS.Proxy.slnx
dotnet test src/Tests/Proxies.Tests
```

Expected: clean build (0 warnings), all `Proxies.Tests` pass.

- [ ] **Step 7: Commit**

```bash
git add src/Modules/Proxies/Modules.Proxies.Contracts/v1/Proxies/SetProxyTagsCommand.cs \
        src/Modules/Proxies/Modules.Proxies.Contracts/v1/Proxies/AssignProxyTagCommand.cs \
        src/Modules/Proxies/Modules.Proxies.Contracts/v1/Proxies/UnassignProxyTagCommand.cs \
        src/Modules/Proxies/Modules.Proxies/Features/v1/Proxies/SetProxyTags/ \
        src/Modules/Proxies/Modules.Proxies/Features/v1/Proxies/AssignProxyTag/ \
        src/Modules/Proxies/Modules.Proxies/Features/v1/Proxies/UnassignProxyTag/ \
        src/Modules/Proxies/Modules.Proxies/ProxiesModule.cs \
        src/Tests/Proxies.Tests/Handlers/ProxyTagAssignmentHandlerTests.cs
git commit -m "feat(proxies): add SetProxyTags, AssignProxyTag, and UnassignProxyTag commands

Co-Authored-By: Claude Sonnet 5 <noreply@anthropic.com>"
```

---

### Task 4: Tag Categories admin page

**Files:**
- Create: `clients/admin/src/api/tag-categories.ts`
- Create: `clients/admin/src/components/proxies/tag-category-dialog.tsx`
- Create: `clients/admin/src/pages/proxies/tag-categories.tsx`
- Modify: `clients/admin/src/components/layout/nav-items.ts`
- Modify: `clients/admin/src/routes.tsx`
- Modify: `clients/admin/tests/helpers/shell-mocks.ts`
- Test: `clients/admin/tests/proxies/tag-categories.spec.ts`

**Interfaces:**
- Consumes: the 6 `/tag-categories*` endpoints from Task 2.
- Produces: nothing consumed by later tasks (Tasks 5/6 independently fetch `listTagCategories()` from the same new API client file).

- [ ] **Step 1: Write the failing Playwright test**

`ADMIN_PERMS` in `clients/admin/tests/helpers/shell-mocks.ts` does not yet include the `Proxies.Tags` permissions (verified: it has `Proxies.ProviderAccounts.*` and `Proxies.ManualProxies.*` but no `Proxies.Tags.*`). Add these 4 lines right after the existing `"Permissions.Proxies.ManualProxies.Delete",` line:

```ts
  "Permissions.Proxies.Tags.View",
  "Permissions.Proxies.Tags.Create",
  "Permissions.Proxies.Tags.Update",
  "Permissions.Proxies.Tags.Delete",
```

Create `clients/admin/tests/proxies/tag-categories.spec.ts`:

```ts
import { expect, test } from "@playwright/test";
import { seedAuthedSession, TEST_USER } from "../helpers/auth-seed";
import { installAdminShellMocks, ADMIN_PERMS } from "../helpers/shell-mocks";

const PAIS_CATEGORY = { id: "cat-1", name: "pais", values: ["ar", "cl"] };

test.beforeEach(async ({ page }) => {
  await seedAuthedSession(page, { ...TEST_USER, permissions: [...ADMIN_PERMS] });
  await installAdminShellMocks(page);
});

test.describe("tag categories", () => {
  test("renders a category with its values", async ({ page }) => {
    await page.route("**/api/v1/proxies/tag-categories", async (route) => {
      if (route.request().method() === "GET") {
        await route.fulfill({ status: 200, headers: { "Content-Type": "application/json" }, body: JSON.stringify([PAIS_CATEGORY]) });
      } else {
        await route.continue();
      }
    });

    await page.goto("/proxies/tag-categories");

    await expect(page.getByRole("heading", { name: "Tag Categories", exact: true })).toBeVisible({ timeout: 10_000 });
    await expect(page.getByText("pais", { exact: true })).toBeVisible();
    await expect(page.getByText("ar", { exact: true })).toBeVisible();
    await expect(page.getByText("cl", { exact: true })).toBeVisible();
  });

  test("creates a new category", async ({ page }) => {
    await page.route("**/api/v1/proxies/tag-categories", async (route) => {
      if (route.request().method() === "GET") {
        await route.fulfill({ status: 200, headers: { "Content-Type": "application/json" }, body: JSON.stringify([]) });
      } else if (route.request().method() === "POST") {
        await route.fulfill({ status: 200, headers: { "Content-Type": "application/json" }, body: JSON.stringify("cat-new") });
      } else {
        await route.continue();
      }
    });

    await page.goto("/proxies/tag-categories");
    await expect(page.getByRole("heading", { name: "Tag Categories", exact: true })).toBeVisible({ timeout: 10_000 });
    await page.getByRole("button", { name: "New category" }).click();
    await page.getByLabel("Name").fill("funcionalidad");
    await page.getByRole("button", { name: "Save" }).click();

    await expect(page.getByText("Category created", { exact: true })).toBeVisible();
  });
});
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `cd clients/admin && npx playwright test tests/proxies/tag-categories.spec.ts`
Expected: FAIL — `/proxies/tag-categories` route doesn't exist yet.

- [ ] **Step 3: Implement**

Create `clients/admin/src/api/tag-categories.ts`:

```ts
import { apiFetch } from "@/lib/api-client";

const BASE = "/api/v1/proxies/tag-categories";

export type TagCategoryDto = {
  id: string;
  name: string;
  values: string[];
};

export async function listTagCategories(): Promise<TagCategoryDto[]> {
  return apiFetch<TagCategoryDto[]>(BASE);
}

export async function createTagCategory(name: string): Promise<string> {
  return apiFetch<string>(BASE, { method: "POST", body: JSON.stringify({ name }) });
}

export async function renameTagCategory(id: string, name: string): Promise<void> {
  await apiFetch<void>(`${BASE}/${id}`, { method: "PUT", body: JSON.stringify({ name }) });
}

export async function deleteTagCategory(id: string): Promise<void> {
  await apiFetch<void>(`${BASE}/${id}`, { method: "DELETE" });
}

export async function addTagCategoryValue(categoryId: string, value: string): Promise<void> {
  await apiFetch<void>(`${BASE}/${categoryId}/values`, { method: "POST", body: JSON.stringify({ value }) });
}

export async function removeTagCategoryValue(categoryId: string, value: string): Promise<void> {
  await apiFetch<void>(`${BASE}/${categoryId}/values/${encodeURIComponent(value)}`, { method: "DELETE" });
}
```

Create `clients/admin/src/components/proxies/tag-category-dialog.tsx` (create-or-rename, single Name field — mirrors `ProviderAccountDialog`'s structure at a smaller scale):

```tsx
import { useEffect } from "react";
import { useForm } from "react-hook-form";
import { zodResolver } from "@hookform/resolvers/zod";
import { z } from "zod";
import { useMutation, useQueryClient } from "@tanstack/react-query";
import { toast } from "sonner";
import { Loader2 } from "lucide-react";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import { Dialog, DialogBody, DialogContent, DialogFooter, DialogHeader, DialogTitle } from "@/components/ui/dialog";
import { Field } from "@/components/list";
import { ApiRequestError } from "@/lib/api-client";
import { createTagCategory, renameTagCategory, type TagCategoryDto } from "@/api/tag-categories";

const schema = z.object({ name: z.string().trim().min(2, "At least 2 characters.").max(128) });
type FormValues = z.infer<typeof schema>;

export function TagCategoryDialog({
  open,
  onClose,
  category,
}: {
  open: boolean;
  onClose: () => void;
  category?: TagCategoryDto;
}) {
  const queryClient = useQueryClient();
  const isEdit = Boolean(category);

  const {
    register,
    handleSubmit,
    reset,
    formState: { errors, isSubmitting },
  } = useForm<FormValues>({ resolver: zodResolver(schema), defaultValues: { name: "" } });

  useEffect(() => {
    reset({ name: category?.name ?? "" });
  }, [category, reset]);

  const mutation = useMutation({
    mutationFn: async (values: FormValues) => {
      if (isEdit) {
        await renameTagCategory(category!.id, values.name);
      } else {
        await createTagCategory(values.name);
      }
    },
    onSuccess: () => {
      toast.success(isEdit ? "Category renamed" : "Category created");
      void queryClient.invalidateQueries({ queryKey: ["proxies", "tag-categories"] });
      onClose();
    },
    onError: (err) => {
      const detail =
        err instanceof ApiRequestError ? (err.problem?.detail ?? err.problem?.title ?? err.message) : (err as Error).message;
      toast.error(isEdit ? "Rename failed" : "Create failed", { description: detail });
    },
  });

  const submitting = isSubmitting || mutation.isPending;

  return (
    <Dialog open={open} onOpenChange={(o) => !o && onClose()}>
      <DialogContent>
        <DialogHeader>
          <DialogTitle>{isEdit ? "Rename category" : "New category"}</DialogTitle>
        </DialogHeader>
        <form onSubmit={handleSubmit((values) => mutation.mutate(values))}>
          <DialogBody className="space-y-4">
            <Field id="tc-name" label="Name" required error={errors.name?.message}>
              <Input id="tc-name" autoComplete="off" placeholder="pais" {...register("name")} />
            </Field>
          </DialogBody>
          <DialogFooter>
            <Button type="button" variant="outline" onClick={onClose} disabled={submitting}>
              Cancel
            </Button>
            <Button type="submit" disabled={submitting} className="min-w-[8.5rem]">
              {submitting ? (
                <>
                  <Loader2 className="size-4 animate-spin" aria-hidden />
                  <span>Saving…</span>
                </>
              ) : (
                "Save"
              )}
            </Button>
          </DialogFooter>
        </form>
      </DialogContent>
    </Dialog>
  );
}
```

Create `clients/admin/src/pages/proxies/tag-categories.tsx`:

```tsx
import { useState } from "react";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { toast } from "sonner";
import { Plus, Tag as TagIcon, Trash2, X } from "lucide-react";
import { EntityPageHeader, ErrorBand, LoadingRow } from "@/components/list";
import { EmptyState } from "@/components/empty-state";
import { Button } from "@/components/ui/button";
import { Badge } from "@/components/ui/badge";
import { Input } from "@/components/ui/input";
import { ApiRequestError } from "@/lib/api-client";
import { ProxiesPermissions } from "@/lib/permissions";
import { useAuth } from "@/auth/use-auth";
import {
  addTagCategoryValue,
  deleteTagCategory,
  listTagCategories,
  removeTagCategoryValue,
  type TagCategoryDto,
} from "@/api/tag-categories";
import { TagCategoryDialog } from "@/components/proxies/tag-category-dialog";

function describeError(err: unknown): string {
  if (err instanceof ApiRequestError) return err.problem?.detail ?? err.problem?.title ?? err.message;
  if (err instanceof Error) return err.message;
  return "Something went wrong.";
}

export function TagCategoriesPage() {
  const { user } = useAuth();
  const queryClient = useQueryClient();
  const [dialogState, setDialogState] = useState<{ open: boolean; category?: TagCategoryDto }>({ open: false });
  const [newValueInputs, setNewValueInputs] = useState<Record<string, string>>({});

  const canCreate = user?.permissions.includes(ProxiesPermissions.Tags.Create) ?? false;
  const canUpdate = user?.permissions.includes(ProxiesPermissions.Tags.Update) ?? false;
  const canDelete = user?.permissions.includes(ProxiesPermissions.Tags.Delete) ?? false;

  const categoriesQuery = useQuery({
    queryKey: ["proxies", "tag-categories"],
    queryFn: () => listTagCategories(),
  });

  const invalidate = () => queryClient.invalidateQueries({ queryKey: ["proxies", "tag-categories"] });

  const deleteCategoryMutation = useMutation({
    mutationFn: (id: string) => deleteTagCategory(id),
    onSuccess: () => {
      toast.success("Category deleted");
      invalidate();
    },
    onError: (err) => toast.error("Delete failed", { description: describeError(err) }),
  });

  const addValueMutation = useMutation({
    mutationFn: (input: { categoryId: string; value: string }) => addTagCategoryValue(input.categoryId, input.value),
    onSuccess: (_data, input) => {
      setNewValueInputs((prev) => ({ ...prev, [input.categoryId]: "" }));
      invalidate();
    },
    onError: (err) => toast.error("Add value failed", { description: describeError(err) }),
  });

  const removeValueMutation = useMutation({
    mutationFn: (input: { categoryId: string; value: string }) => removeTagCategoryValue(input.categoryId, input.value),
    onSuccess: () => invalidate(),
    onError: (err) => toast.error("Remove value failed", { description: describeError(err) }),
  });

  const categories = categoriesQuery.data ?? [];

  return (
    <div className="space-y-8">
      <EntityPageHeader
        icon={TagIcon}
        title="Tag Categories"
        total={categories.length}
        unit="category"
        description="Predefined dimensions (e.g. pais, funcionalidad) and their values, used to speed up tagging proxies."
      >
        {canCreate && (
          <Button onClick={() => setDialogState({ open: true })}>
            <Plus className="mr-1 h-4 w-4" /> New category
          </Button>
        )}
      </EntityPageHeader>

      {categoriesQuery.isError && <ErrorBand message={describeError(categoriesQuery.error)} />}
      {categoriesQuery.isLoading && <LoadingRow label="Loading tag categories" />}

      {!categoriesQuery.isLoading && !categoriesQuery.isError && categories.length === 0 && (
        <EmptyState
          icon={TagIcon}
          kicker="// no categories"
          title="No tag categories yet."
          description="Create one (e.g. \"pais\") and add values to speed up tagging proxies from a select instead of typing."
          action={
            canCreate ? (
              <Button onClick={() => setDialogState({ open: true })}>
                <Plus className="mr-1 h-4 w-4" /> New category
              </Button>
            ) : undefined
          }
        />
      )}

      {categories.length > 0 && (
        <ol className="space-y-4">
          {categories.map((category) => (
            <li key={category.id} className="rounded-xl border border-[var(--color-border)] p-4">
              <div className="flex items-center justify-between gap-3">
                <div className="font-mono text-[13px] font-medium">{category.name}</div>
                <div className="flex gap-2">
                  {canUpdate && (
                    <Button variant="ghost" size="sm" onClick={() => setDialogState({ open: true, category })}>
                      Rename
                    </Button>
                  )}
                  {canDelete && (
                    <Button
                      variant="ghost"
                      size="sm"
                      onClick={() => {
                        if (window.confirm(`Delete category "${category.name}"? Already-assigned tags are unaffected.`)) {
                          deleteCategoryMutation.mutate(category.id);
                        }
                      }}
                      className="text-[var(--color-destructive)] hover:bg-[oklch(from_var(--color-destructive)_l_c_h_/_0.08)]"
                    >
                      <Trash2 className="h-3.5 w-3.5" />
                    </Button>
                  )}
                </div>
              </div>

              <div className="mt-3 flex flex-wrap items-center gap-2">
                {category.values.map((value) => (
                  <Badge key={value} variant="muted" className="gap-1 font-mono">
                    {value}
                    {canUpdate && (
                      <button
                        type="button"
                        aria-label={`Remove value ${value}`}
                        onClick={() => removeValueMutation.mutate({ categoryId: category.id, value })}
                      >
                        <X className="h-3 w-3" />
                      </button>
                    )}
                  </Badge>
                ))}
                {canUpdate && (
                  <form
                    className="flex items-center gap-1"
                    onSubmit={(e) => {
                      e.preventDefault();
                      const value = (newValueInputs[category.id] ?? "").trim();
                      if (value) addValueMutation.mutate({ categoryId: category.id, value });
                    }}
                  >
                    <Input
                      aria-label={`New value for ${category.name}`}
                      placeholder="cl"
                      value={newValueInputs[category.id] ?? ""}
                      onChange={(e) => setNewValueInputs((prev) => ({ ...prev, [category.id]: e.target.value }))}
                      className="h-7 w-24 text-[12px]"
                    />
                    <Button type="submit" size="sm" variant="outline">
                      <Plus className="h-3 w-3" />
                    </Button>
                  </form>
                )}
              </div>
            </li>
          ))}
        </ol>
      )}

      <TagCategoryDialog
        open={dialogState.open}
        category={dialogState.category}
        onClose={() => setDialogState({ open: false })}
      />
    </div>
  );
}
```

In `clients/admin/src/components/layout/nav-items.ts`, add `Tag` to the `lucide-react` import list, and add a new item to the `"proxies"` section's `items` array (after the `"Manual Proxies"` entry):

```ts
      {
        to: "/proxies/tag-categories",
        label: "Tag Categories",
        icon: Tag,
        perms: [ProxiesPermissions.Tags.View],
      },
```

In `clients/admin/src/routes.tsx`, add the lazy import next to the other proxies page imports:

```ts
const TagCategoriesPage = lazyNamed(() => import("@/pages/proxies/tag-categories"), "TagCategoriesPage");
```

and a new route entry after the `"proxies/manual"` route:

```tsx
          {
            path: "proxies/tag-categories",
            element: (
              <RouteGuard perms={[ProxiesPermissions.Tags.View]}>
                <TagCategoriesPage />
              </RouteGuard>
            ),
          },
```

- [ ] **Step 4: Run the test to verify it passes**

Run: `cd clients/admin && npx playwright test tests/proxies/tag-categories.spec.ts`
Expected: PASS (both facts)

- [ ] **Step 5: Full frontend verification**

```bash
cd clients/admin
npm run build
npm run lint
npx playwright test tests/proxies/
```

Expected: build clean, lint clean (only pre-existing fast-refresh warnings), all Proxies Playwright specs pass (including the pre-existing `proxies-list.spec.ts`).

- [ ] **Step 6: Commit**

```bash
git add clients/admin/src/api/tag-categories.ts \
        clients/admin/src/components/proxies/tag-category-dialog.tsx \
        clients/admin/src/pages/proxies/tag-categories.tsx \
        clients/admin/src/components/layout/nav-items.ts \
        clients/admin/src/routes.tsx \
        clients/admin/tests/helpers/shell-mocks.ts \
        clients/admin/tests/proxies/tag-categories.spec.ts
git commit -m "feat(admin): add the Tag Categories admin page

Co-Authored-By: Claude Sonnet 5 <noreply@anthropic.com>"
```

---

### Task 5: Individual proxy tag editor

**Files:**
- Modify: `clients/admin/src/api/proxies.ts`
- Create: `clients/admin/src/components/proxies/proxy-tags-dialog.tsx`
- Modify: `clients/admin/src/pages/proxies/list.tsx`
- Test: `clients/admin/tests/proxies/proxy-tags-dialog.spec.ts`

**Interfaces:**
- Consumes: `listTagCategories()` from Task 4's `clients/admin/src/api/tag-categories.ts`; the `PUT /{id}/tags` endpoint from Task 3.
- Produces: no new exports consumed by Task 6 (the bulk dialog is independent).

- [ ] **Step 1: Write the failing Playwright test**

Create `clients/admin/tests/proxies/proxy-tags-dialog.spec.ts`:

```ts
import { expect, test } from "@playwright/test";
import { seedAuthedSession, TEST_USER } from "../helpers/auth-seed";
import { installAdminShellMocks, ADMIN_PERMS, paged } from "../helpers/shell-mocks";

const PROXY = {
  id: "11111111-1111-1111-1111-111111111111",
  host: "10.0.0.5",
  port: 3128,
  protocol: "Http",
  country: "CL",
  status: "Active",
  providerAccountId: "acc-1",
  providerAccountName: "Manual",
  providerType: "Manual",
  providerGrouping: null,
  tags: ["pais:cl"],
  createdAtUtc: "2026-01-01T00:00:00Z",
  lastRenewedAtUtc: null,
};

const PAIS_CATEGORY = { id: "cat-1", name: "pais", values: ["ar", "cl"] };

test.beforeEach(async ({ page }) => {
  await seedAuthedSession(page, { ...TEST_USER, permissions: [...ADMIN_PERMS] });
  await installAdminShellMocks(page);
  await page.route("**/api/v1/proxies/provider-accounts*", async (route) => {
    await route.fulfill({ status: 200, headers: { "Content-Type": "application/json" }, body: JSON.stringify(paged([])) });
  });
  await page.route("**/api/v1/proxies/?*", async (route) => {
    await route.fulfill({ status: 200, headers: { "Content-Type": "application/json" }, body: JSON.stringify(paged([PROXY])) });
  });
  await page.route("**/api/v1/proxies/tag-categories", async (route) => {
    await route.fulfill({ status: 200, headers: { "Content-Type": "application/json" }, body: JSON.stringify([PAIS_CATEGORY]) });
  });
});

test.describe("individual proxy tag editor", () => {
  test("pre-selects the category value matching the proxy's current tags, and submits the composed set", async ({ page }) => {
    let putBody: unknown;
    await page.route("**/api/v1/proxies/11111111-1111-1111-1111-111111111111/tags", async (route) => {
      putBody = route.request().postDataJSON();
      await route.fulfill({ status: 204 });
    });

    await page.goto("/proxies");
    await expect(page.getByRole("heading", { name: "Proxies", exact: true })).toBeVisible({ timeout: 10_000 });
    await page.getByRole("button", { name: "Tags", exact: true }).click();

    // The Select is a Radix DropdownMenu-based combobox, not a native <select> — its trigger
    // button's visible text is the current value, and options open as menuitems.
    const paisSelect = page.getByTestId("tag-category-select-pais");
    await expect(paisSelect.getByRole("button")).toHaveText("cl");
    await paisSelect.getByRole("button").click();
    await page.getByRole("menuitem", { name: "ar", exact: true }).click();
    await page.getByRole("button", { name: "Save" }).click();

    await expect.poll(() => putBody).toEqual({ tagNames: ["pais:ar"] });
  });
});
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `cd clients/admin && npx playwright test tests/proxies/proxy-tags-dialog.spec.ts`
Expected: FAIL — no "Tags" button exists on the Proxies list yet.

- [ ] **Step 3: Implement**

In `clients/admin/src/api/proxies.ts`, add after `setProxiesStatus`:

```ts
export async function setProxyTags(proxyId: string, tagNames: string[]): Promise<void> {
  await apiFetch<void>(`${BASE}/${proxyId}/tags`, { method: "PUT", body: JSON.stringify({ tagNames }) });
}
```

Create `clients/admin/src/components/proxies/proxy-tags-dialog.tsx`:

```tsx
import { useEffect, useState } from "react";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { toast } from "sonner";
import { Loader2 } from "lucide-react";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import { Select } from "@/components/ui/select";
import { Dialog, DialogBody, DialogContent, DialogFooter, DialogHeader, DialogTitle } from "@/components/ui/dialog";
import { Field } from "@/components/list";
import { ApiRequestError } from "@/lib/api-client";
import { listTagCategories } from "@/api/tag-categories";
import { setProxyTags, type ProxyDto } from "@/api/proxies";

function describeError(err: unknown): string {
  if (err instanceof ApiRequestError) return err.problem?.detail ?? err.problem?.title ?? err.message;
  if (err instanceof Error) return err.message;
  return "Something went wrong.";
}

export function ProxyTagsDialog({ open, proxy, onClose }: { open: boolean; proxy: ProxyDto | null; onClose: () => void }) {
  const queryClient = useQueryClient();
  const [selectedByCategory, setSelectedByCategory] = useState<Record<string, string>>({});
  const [customTagsInput, setCustomTagsInput] = useState("");

  const categoriesQuery = useQuery({
    queryKey: ["proxies", "tag-categories"],
    queryFn: () => listTagCategories(),
    enabled: open,
  });
  const categories = categoriesQuery.data ?? [];

  // Split the proxy's current tags into "matches a category:value" vs. everything else, so the
  // selects pre-select what's already assigned and the free-text field only shows the rest.
  useEffect(() => {
    if (!proxy || categories.length === 0) {
      setSelectedByCategory({});
      setCustomTagsInput(proxy?.tags.join(", ") ?? "");
      return;
    }
    const matched: Record<string, string> = {};
    const consumed = new Set<string>();
    for (const category of categories) {
      for (const value of category.values) {
        const composed = `${category.name}:${value}`;
        if (proxy.tags.includes(composed)) {
          matched[category.name] = value;
          consumed.add(composed);
          break;
        }
      }
    }
    setSelectedByCategory(matched);
    setCustomTagsInput(proxy.tags.filter((t) => !consumed.has(t)).join(", "));
  }, [proxy, categories]);

  const mutation = useMutation({
    mutationFn: (tagNames: string[]) => setProxyTags(proxy!.id, tagNames),
    onSuccess: () => {
      toast.success("Tags updated");
      void queryClient.invalidateQueries({ queryKey: ["proxies", "list"] });
      onClose();
    },
    onError: (err) => toast.error("Update failed", { description: describeError(err) }),
  });

  if (!proxy) return null;

  function handleSubmit() {
    const composed = categories
      .map((c) => (selectedByCategory[c.name] ? `${c.name}:${selectedByCategory[c.name]}` : null))
      .filter((t): t is string => t !== null);
    const custom = customTagsInput
      .split(",")
      .map((t) => t.trim())
      .filter(Boolean);
    mutation.mutate([...composed, ...custom]);
  }

  return (
    <Dialog open={open} onOpenChange={(o) => !o && onClose()}>
      <DialogContent>
        <DialogHeader>
          <DialogTitle>
            Tags for {proxy.host}:{proxy.port}
          </DialogTitle>
        </DialogHeader>
        <DialogBody className="space-y-4">
          {categories.map((category) => (
            <Field key={category.id} id={`tag-cat-${category.id}`} label={category.name}>
              {/* data-testid gives Playwright an unambiguous handle — the Select component
                  (Radix DropdownMenu-based, not a native <select>) doesn't accept id/aria-label. */}
              <div data-testid={`tag-category-select-${category.name}`}>
                <Select
                  value={selectedByCategory[category.name] ?? ""}
                  onChange={(v) => setSelectedByCategory((prev) => ({ ...prev, [category.name]: v }))}
                  options={category.values.map((v) => ({ value: v, label: v }))}
                  placeholder="— none —"
                  className="w-full"
                  minWidth="100%"
                />
              </div>
            </Field>
          ))}
          <Field id="tag-custom" label="Other tags" hint="Comma-separated — anything not covered by a category above.">
            <Input id="tag-custom" value={customTagsInput} onChange={(e) => setCustomTagsInput(e.target.value)} />
          </Field>
        </DialogBody>
        <DialogFooter>
          <Button type="button" variant="outline" onClick={onClose} disabled={mutation.isPending}>
            Cancel
          </Button>
          <Button type="button" onClick={handleSubmit} disabled={mutation.isPending} className="min-w-[8.5rem]">
            {mutation.isPending ? (
              <>
                <Loader2 className="size-4 animate-spin" aria-hidden />
                <span>Saving…</span>
              </>
            ) : (
              "Save"
            )}
          </Button>
        </DialogFooter>
      </DialogContent>
    </Dialog>
  );
}
```

In `clients/admin/src/pages/proxies/list.tsx`:

Add `Tag as TagIcon` to the `lucide-react` import, and import the new dialog:

```ts
import { ProxyTagsDialog } from "@/components/proxies/proxy-tags-dialog";
```

Add state near the other `useState` calls in `ProxiesListPage`:

```tsx
  const [tagsDialogProxy, setTagsDialogProxy] = useState<ProxyDto | null>(null);
```

Pass an `onEditTags` callback into both `ProxyDesktopRow` and `ProxyMobileCard` (mirroring how `onEnable`/`onDisable` are already passed):

```tsx
                onEnable={() => enableMutation.mutate({ proxyIds: [proxy.id] })}
                onDisable={() => disableMutation.mutate({ proxyIds: [proxy.id] })}
                onEditTags={() => setTagsDialogProxy(proxy)}
```

(add this line to both the `<ProxyDesktopRow ... />` and `<ProxyMobileCard ... />` call sites, right after their existing `onDisable` prop)

In the `ProxyDesktopRow` function, add `onEditTags: () => void` to its props type and destructured params, and change the actions cell:

```tsx
        <div className="flex items-center justify-end gap-1">
          {canUpdate ? (
            <Button variant="ghost" size="sm" onClick={onEditTags}>
              <TagIcon className="h-3.5 w-3.5" />
              <span className="sr-only sm:not-sr-only sm:ml-1">Tags</span>
            </Button>
          ) : null}
          {canUpdate ? (
            proxy.status === "Active" ? (
              <Button variant="outline" size="sm" disabled={busy} onClick={onDisable}>
                Disable
              </Button>
            ) : (
              <Button size="sm" disabled={busy} onClick={onEnable}>
                Enable
              </Button>
            )
          ) : null}
        </div>
```

In `ProxyMobileCard`, add `onEditTags: () => void` to its props type and destructured params, and change the bottom action area:

```tsx
      {canUpdate && (
        <div className="mt-3 flex gap-2">
          <Button variant="outline" size="sm" onClick={onEditTags} className="flex-1">
            <TagIcon className="mr-1 h-3.5 w-3.5" /> Tags
          </Button>
          {proxy.status === "Active" ? (
            <Button variant="outline" size="sm" disabled={busy} onClick={onDisable} className="flex-1">
              Disable
            </Button>
          ) : (
            <Button size="sm" disabled={busy} onClick={onEnable} className="flex-1">
              Enable
            </Button>
          )}
        </div>
      )}
```

Finally, render the dialog once at the bottom of `ProxiesListPage`'s returned JSX (as a sibling to the closing `</div>`, e.g. right after the `<Pagination .../>` block):

```tsx
      <ProxyTagsDialog open={tagsDialogProxy !== null} proxy={tagsDialogProxy} onClose={() => setTagsDialogProxy(null)} />
```

- [ ] **Step 4: Run the test to verify it passes**

Run: `cd clients/admin && npx playwright test tests/proxies/proxy-tags-dialog.spec.ts`
Expected: PASS

- [ ] **Step 5: Full frontend verification**

```bash
cd clients/admin
npm run build
npm run lint
npx playwright test tests/proxies/
```

Expected: build clean, lint clean, all Proxies Playwright specs pass (including `proxies-list.spec.ts` — the new "Tags" button must not break its existing selectors; `getByRole("button", { name: "Disable", exact: true })` still resolves to exactly one button since "Tags" has a different accessible name).

- [ ] **Step 6: Commit**

```bash
git add clients/admin/src/api/proxies.ts \
        clients/admin/src/components/proxies/proxy-tags-dialog.tsx \
        clients/admin/src/pages/proxies/list.tsx \
        clients/admin/tests/proxies/proxy-tags-dialog.spec.ts
git commit -m "feat(admin): add the individual proxy tag editor

Co-Authored-By: Claude Sonnet 5 <noreply@anthropic.com>"
```

---

### Task 6: Bulk tag editor

**Files:**
- Modify: `clients/admin/src/api/proxies.ts`
- Create: `clients/admin/src/components/proxies/bulk-tag-dialog.tsx`
- Modify: `clients/admin/src/pages/proxies/list.tsx`
- Test: `clients/admin/tests/proxies/bulk-tag-dialog.spec.ts`

**Interfaces:**
- Consumes: `listTagCategories()` from Task 4; the `POST /tags/assign` and `POST /tags/unassign` endpoints from Task 3.

- [ ] **Step 1: Write the failing Playwright test**

Create `clients/admin/tests/proxies/bulk-tag-dialog.spec.ts`:

```ts
import { expect, test } from "@playwright/test";
import { seedAuthedSession, TEST_USER } from "../helpers/auth-seed";
import { installAdminShellMocks, ADMIN_PERMS, paged } from "../helpers/shell-mocks";

const PROXY = {
  id: "11111111-1111-1111-1111-111111111111",
  host: "10.0.0.5",
  port: 3128,
  protocol: "Http",
  country: "CL",
  status: "Active",
  providerAccountId: "acc-1",
  providerAccountName: "Manual",
  providerType: "Manual",
  providerGrouping: null,
  tags: [],
  createdAtUtc: "2026-01-01T00:00:00Z",
  lastRenewedAtUtc: null,
};

const PAIS_CATEGORY = { id: "cat-1", name: "pais", values: ["ar", "cl"] };

test.beforeEach(async ({ page }) => {
  await seedAuthedSession(page, { ...TEST_USER, permissions: [...ADMIN_PERMS] });
  await installAdminShellMocks(page);
  await page.route("**/api/v1/proxies/provider-accounts*", async (route) => {
    await route.fulfill({ status: 200, headers: { "Content-Type": "application/json" }, body: JSON.stringify(paged([])) });
  });
  await page.route("**/api/v1/proxies/?*", async (route) => {
    await route.fulfill({ status: 200, headers: { "Content-Type": "application/json" }, body: JSON.stringify(paged([PROXY])) });
  });
  await page.route("**/api/v1/proxies/tag-categories", async (route) => {
    await route.fulfill({ status: 200, headers: { "Content-Type": "application/json" }, body: JSON.stringify([PAIS_CATEGORY]) });
  });
});

test.describe("bulk tag editor", () => {
  test("adds a category-selected tag to every checked proxy", async ({ page }) => {
    let assignBody: unknown;
    await page.route("**/api/v1/proxies/tags/assign", async (route) => {
      assignBody = route.request().postDataJSON();
      await route.fulfill({ status: 200, headers: { "Content-Type": "application/json" }, body: JSON.stringify(1) });
    });

    await page.goto("/proxies");
    await expect(page.getByRole("heading", { name: "Proxies", exact: true })).toBeVisible({ timeout: 10_000 });
    await page.getByRole("checkbox", { name: /Select 10.0.0.5:3128/ }).check();
    await page.getByRole("button", { name: "Manage tags" }).click();
    // The Select is a Radix DropdownMenu-based combobox, not a native <select>.
    await page.getByTestId("bulk-add-category-select").getByRole("button").click();
    await page.getByRole("menuitem", { name: "pais", exact: true }).click();
    await page.getByTestId("bulk-add-value-select").getByRole("button").click();
    await page.getByRole("menuitem", { name: "cl", exact: true }).click();
    await page.getByRole("button", { name: "Add to 1 selected" }).click();

    await expect.poll(() => assignBody).toEqual({ proxyIds: ["11111111-1111-1111-1111-111111111111"], tagName: "pais:cl" });
  });
});
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `cd clients/admin && npx playwright test tests/proxies/bulk-tag-dialog.spec.ts`
Expected: FAIL — no "Manage tags" button exists yet.

- [ ] **Step 3: Implement**

In `clients/admin/src/api/proxies.ts`, add after `setProxyTags`:

```ts
export async function assignProxyTag(proxyIds: string[], tagName: string): Promise<number> {
  return apiFetch<number>(`${BASE}/tags/assign`, { method: "POST", body: JSON.stringify({ proxyIds, tagName }) });
}

export async function unassignProxyTag(proxyIds: string[], tagName: string): Promise<number> {
  return apiFetch<number>(`${BASE}/tags/unassign`, { method: "POST", body: JSON.stringify({ proxyIds, tagName }) });
}
```

Create `clients/admin/src/components/proxies/bulk-tag-dialog.tsx`:

```tsx
import { useState } from "react";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { toast } from "sonner";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import { Select } from "@/components/ui/select";
import { Dialog, DialogBody, DialogContent, DialogFooter, DialogHeader, DialogTitle } from "@/components/ui/dialog";
import { Field } from "@/components/list";
import { ApiRequestError } from "@/lib/api-client";
import { listTagCategories } from "@/api/tag-categories";
import { assignProxyTag, unassignProxyTag } from "@/api/proxies";

function describeError(err: unknown): string {
  if (err instanceof ApiRequestError) return err.problem?.detail ?? err.problem?.title ?? err.message;
  if (err instanceof Error) return err.message;
  return "Something went wrong.";
}

export function BulkTagDialog({
  open,
  proxyIds,
  onClose,
}: {
  open: boolean;
  proxyIds: string[];
  onClose: () => void;
}) {
  const queryClient = useQueryClient();

  const [addCategory, setAddCategory] = useState("");
  const [addValue, setAddValue] = useState("");
  const [addCustom, setAddCustom] = useState("");
  const [removeCategory, setRemoveCategory] = useState("");
  const [removeValue, setRemoveValue] = useState("");
  const [removeCustom, setRemoveCustom] = useState("");

  const categoriesQuery = useQuery({
    queryKey: ["proxies", "tag-categories"],
    queryFn: () => listTagCategories(),
    enabled: open,
  });
  const categories = categoriesQuery.data ?? [];

  function resolveTagName(category: string, value: string, custom: string): string | null {
    if (custom.trim()) return custom.trim();
    if (category && value) return `${category}:${value}`;
    return null;
  }

  const assignMutation = useMutation({
    mutationFn: (tagName: string) => assignProxyTag(proxyIds, tagName),
    onSuccess: (count) => {
      toast.success(count === 1 ? "Tag added to 1 proxy" : `Tag added to ${count} proxies`);
      void queryClient.invalidateQueries({ queryKey: ["proxies", "list"] });
      setAddCategory("");
      setAddValue("");
      setAddCustom("");
    },
    onError: (err) => toast.error("Add failed", { description: describeError(err) }),
  });

  const unassignMutation = useMutation({
    mutationFn: (tagName: string) => unassignProxyTag(proxyIds, tagName),
    onSuccess: (count) => {
      toast.success(count === 1 ? "Tag removed from 1 proxy" : `Tag removed from ${count} proxies`);
      void queryClient.invalidateQueries({ queryKey: ["proxies", "list"] });
      setRemoveCategory("");
      setRemoveValue("");
      setRemoveCustom("");
    },
    onError: (err) => toast.error("Remove failed", { description: describeError(err) }),
  });

  const addCategoryValues = categories.find((c) => c.name === addCategory)?.values ?? [];
  const removeCategoryValues = categories.find((c) => c.name === removeCategory)?.values ?? [];

  return (
    <Dialog open={open} onOpenChange={(o) => !o && onClose()}>
      <DialogContent>
        <DialogHeader>
          <DialogTitle>Manage tags for {proxyIds.length} selected</DialogTitle>
        </DialogHeader>
        <DialogBody className="space-y-6">
          <div className="space-y-2">
            <div className="text-[11.5px] font-semibold uppercase tracking-wider text-[var(--color-muted-foreground)]">
              Add tag
            </div>
            <Field id="bulk-add-category" label="Category (add)">
              <div data-testid="bulk-add-category-select">
                <Select
                  value={addCategory}
                  onChange={(v) => {
                    setAddCategory(v);
                    setAddValue("");
                  }}
                  options={categories.map((c) => ({ value: c.name, label: c.name }))}
                  placeholder="— choose —"
                  className="w-full"
                  minWidth="100%"
                />
              </div>
            </Field>
            <Field id="bulk-add-value" label="Value (add)">
              <div data-testid="bulk-add-value-select">
                <Select
                  value={addValue}
                  onChange={setAddValue}
                  options={addCategoryValues.map((v) => ({ value: v, label: v }))}
                  placeholder="— choose —"
                  className="w-full"
                  minWidth="100%"
                />
              </div>
            </Field>
            <Field id="bulk-add-custom" label="Or custom tag">
              <Input id="bulk-add-custom" value={addCustom} onChange={(e) => setAddCustom(e.target.value)} />
            </Field>
            <Button
              type="button"
              size="sm"
              disabled={assignMutation.isPending || resolveTagName(addCategory, addValue, addCustom) === null}
              onClick={() => {
                const tagName = resolveTagName(addCategory, addValue, addCustom);
                if (tagName) assignMutation.mutate(tagName);
              }}
            >
              Add to {proxyIds.length} selected
            </Button>
          </div>

          <div className="space-y-2 border-t border-[var(--color-border)] pt-4">
            <div className="text-[11.5px] font-semibold uppercase tracking-wider text-[var(--color-muted-foreground)]">
              Remove tag
            </div>
            <Field id="bulk-remove-category" label="Category (remove)">
              <div data-testid="bulk-remove-category-select">
                <Select
                  value={removeCategory}
                  onChange={(v) => {
                    setRemoveCategory(v);
                    setRemoveValue("");
                  }}
                  options={categories.map((c) => ({ value: c.name, label: c.name }))}
                  placeholder="— choose —"
                  className="w-full"
                  minWidth="100%"
                />
              </div>
            </Field>
            <Field id="bulk-remove-value" label="Value (remove)">
              <div data-testid="bulk-remove-value-select">
                <Select
                  value={removeValue}
                  onChange={setRemoveValue}
                  options={removeCategoryValues.map((v) => ({ value: v, label: v }))}
                  placeholder="— choose —"
                  className="w-full"
                  minWidth="100%"
                />
              </div>
            </Field>
            <Field id="bulk-remove-custom" label="Or custom tag">
              <Input id="bulk-remove-custom" value={removeCustom} onChange={(e) => setRemoveCustom(e.target.value)} />
            </Field>
            <Button
              type="button"
              size="sm"
              variant="outline"
              disabled={unassignMutation.isPending || resolveTagName(removeCategory, removeValue, removeCustom) === null}
              onClick={() => {
                const tagName = resolveTagName(removeCategory, removeValue, removeCustom);
                if (tagName) unassignMutation.mutate(tagName);
              }}
            >
              Remove from {proxyIds.length} selected
            </Button>
          </div>
        </DialogBody>
        <DialogFooter>
          <Button type="button" variant="outline" onClick={onClose}>
            Close
          </Button>
        </DialogFooter>
      </DialogContent>
    </Dialog>
  );
}
```

In `clients/admin/src/pages/proxies/list.tsx`:

Import the new dialog:

```ts
import { BulkTagDialog } from "@/components/proxies/bulk-tag-dialog";
```

Add state near `tagsDialogProxy` (from Task 5):

```tsx
  const [bulkTagDialogOpen, setBulkTagDialogOpen] = useState(false);
```

In the bulk-actions block (where "Enable selected"/"Disable selected" render), add a third button:

```tsx
        {canUpdate && selected.size > 0 && (
          <div className="ml-auto flex gap-2">
            <Button
              size="sm"
              disabled={mutationBusy}
              onClick={() => enableMutation.mutate({ proxyIds: [...selected] })}
            >
              Enable selected ({selected.size})
            </Button>
            <Button
              variant="outline"
              size="sm"
              disabled={mutationBusy}
              onClick={() => disableMutation.mutate({ proxyIds: [...selected] })}
            >
              Disable selected
            </Button>
            <Button variant="outline" size="sm" onClick={() => setBulkTagDialogOpen(true)}>
              Manage tags
            </Button>
          </div>
        )}
```

Render the dialog once at the bottom of the page's JSX, next to `<ProxyTagsDialog .../>`:

```tsx
      <BulkTagDialog open={bulkTagDialogOpen} proxyIds={[...selected]} onClose={() => setBulkTagDialogOpen(false)} />
```

- [ ] **Step 4: Run the test to verify it passes**

Run: `cd clients/admin && npx playwright test tests/proxies/bulk-tag-dialog.spec.ts`
Expected: PASS

- [ ] **Step 5: Full frontend verification**

```bash
cd clients/admin
npm run build
npm run lint
npx playwright test tests/proxies/
```

Expected: build clean, lint clean, every Proxies Playwright spec passes (including Tasks 4/5's new specs and the pre-existing `proxies-list.spec.ts`).

- [ ] **Step 6: Commit**

```bash
git add clients/admin/src/api/proxies.ts \
        clients/admin/src/components/proxies/bulk-tag-dialog.tsx \
        clients/admin/src/pages/proxies/list.tsx \
        clients/admin/tests/proxies/bulk-tag-dialog.spec.ts
git commit -m "feat(admin): add the bulk proxy tag editor (add/remove across selection)

Co-Authored-By: Claude Sonnet 5 <noreply@anthropic.com>"
```

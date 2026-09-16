# Proxy Catalog Fix + Batch Feedback Endpoint — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Correct two errors in the seeded tag catalog, and add a batched proxy-feedback endpoint so scrapers can report many usage outcomes in one call without running the policy engine once per event.

**Architecture:** Phase 0 fixes reference data: `Attachments` is removed from the `entityType` category (it belongs to `operationType` only) and the `QuoteAgreementHardwareStorare` typo is corrected — in source for fresh environments, and through the existing admin API for QA/Production, because `SeedTagCategoriesAsync` only seeds into an empty catalog. Phase 1 adds `POST /api/v1/proxies/feedback/batch` alongside the existing per-event endpoint: it inserts every accepted `ProxyUsageEvent` in one `SaveChangesAsync`, then invokes `IPolicyEvaluationService` once per distinct proxy that carried a negative outcome, rather than once per event.

**Tech Stack:** .NET 10, EF Core 10, Mediator 3.x (source-gen), FluentValidation 12.x, xUnit + Shouldly + NSubstitute, Testcontainers (integration only).

**Spec:** `docs/superpowers/specs/2026-09-14-proxy-client-sdk-design.md` (§4 for the batch endpoint, §5.1 for the catalog corrections, §10 for phase ordering)

## Global Constraints

Copied verbatim from `AGENTS.md` "Golden rules" and "Coding style". Every task's requirements implicitly include this section.

- **Do NOT modify `src/BuildingBlocks`.** Nothing in this plan requires it.
- **Mediator handlers must be `public sealed`**, return `ValueTask<T>`, and `.ConfigureAwait(false)` every await.
- **Every command handler needs a validator** named `{CommandName}Validator`. Enforced by `Architecture.Tests`.
- **Propagate `CancellationToken`** into every EF/IO call.
- **Structured logging only** — no string interpolation in log messages.
- **A module references another module only through its `.Contracts` project.** All new contracts go in `Modules.Proxies.Contracts`.
- File-scoped namespaces · 4-space indent · explicit types (`var` only when the right-hand side is obvious) · `is null` / `is not null` · records for DTOs · `ArgumentNullException.ThrowIfNull` guards on handler inputs.
- **`TreatWarningsAsErrors` is on** and `AnalysisMode=AllEnabledByDefault`. A warning fails the build. Build with `dotnet build src/FS.Proxy.slnx`.
- **No new module is being registered**, so the four-place registration ritual does not apply. `ProxiesModule` already appears in `Program.cs` and `DbMigrator/Program.cs`.
- Documentation lives in **this** repository (golden rule 10's external docs repo belongs to the upstream project, not this fork).

---

## File Structure

**Phase 0**

| File | Responsibility |
|---|---|
| `src/Modules/Proxies/Modules.Proxies/Data/TagCategorySeedData.cs` | *Modify.* The reference catalog. Two value corrections. |
| `src/Tests/Proxies.Tests/Data/TagCategorySeedDataTests.cs` | *Create.* Guards the two corrections plus a general no-duplicates invariant, so a future edit cannot silently reintroduce either. |
| `docs/runbooks/2026-09-14-tag-catalog-correction.md` | *Create.* Operator runbook for QA/Production, which the source change does not reach. |

**Phase 1**

| File | Responsibility |
|---|---|
| `src/Modules/Proxies/Modules.Proxies.Contracts/Dtos/ProxyFeedbackEvent.cs` | *Create.* One reported outcome. The unit of a batch. |
| `src/Modules/Proxies/Modules.Proxies.Contracts/Dtos/BatchFeedbackResult.cs` | *Create.* Response shape: how many accepted, which proxy ids were rejected. |
| `src/Modules/Proxies/Modules.Proxies.Contracts/v1/Proxies/ReportProxyFeedbackBatchCommand.cs` | *Create.* The command. |
| `src/Modules/Proxies/Modules.Proxies/Features/v1/Proxies/ReportProxyFeedbackBatch/ReportProxyFeedbackBatchCommandValidator.cs` | *Create.* Batch size cap and per-event rules. |
| `src/Modules/Proxies/Modules.Proxies/Features/v1/Proxies/ReportProxyFeedbackBatch/ReportProxyFeedbackBatchCommandHandler.cs` | *Create.* Bulk insert + one policy evaluation per distinct negatively-reported proxy. |
| `src/Modules/Proxies/Modules.Proxies/Features/v1/Proxies/ReportProxyFeedbackBatch/ReportProxyFeedbackBatchEndpoint.cs` | *Create.* `POST /feedback/batch` under the consumer policy. |
| `src/Modules/Proxies/Modules.Proxies/ProxiesModule.cs` | *Modify.* One `using` + one `group.Map…()` call. |
| `src/Tests/Proxies.Tests/Validators/ReportProxyFeedbackBatchValidatorTests.cs` | *Create.* |
| `src/Tests/Proxies.Tests/Handlers/ReportProxyFeedbackBatchHandlerTests.cs` | *Create.* |
| `src/Tests/Integration.Tests/Tests/Proxies/ProxyFeedbackBatchTests.cs` | *Create.* End-to-end under real API-key auth. |

---

# PHASE 0 — Catalog corrections

### Task 1: Correct the seeded tag catalog

Two errors confirmed during spec review: `Attachments` is seeded under both `entityType` and `operationType` when it belongs only to `operationType`, and `QuoteAgreementHardwareStorare` is a typo for `QuoteAgreementHardwareStorage`.

Note that a proxy used for tender attachments is tagged with the **combination** `entitytype:tender` + `operationtype:attachments` — `RequestProxiesQueryHandler` ANDs every tag in a request — so removing the `entityType` duplicate loses no expressiveness.

**Files:**
- Modify: `src/Modules/Proxies/Modules.Proxies/Data/TagCategorySeedData.cs`
- Test: `src/Tests/Proxies.Tests/Data/TagCategorySeedDataTests.cs` (create, and create the `Data` directory)

**Interfaces:**
- Consumes: `FSH.Modules.Proxies.Data.TagCategorySeedData.Categories`, typed `IReadOnlyList<(string Name, IReadOnlyList<string> Values)>`.
- Produces: nothing new. The corrected catalog is consumed by `ProxiesDbInitializer.SeedTagCategoriesAsync` and, later, by the SDK's `ProxyTags` constants (a future phase).

- [ ] **Step 1: Write the failing test**

Create `src/Tests/Proxies.Tests/Data/TagCategorySeedDataTests.cs`:

```csharp
using FSH.Modules.Proxies.Data;
using Shouldly;
using Xunit;

namespace Proxies.Tests.Data;

public sealed class TagCategorySeedDataTests
{
    [Fact]
    public void Attachments_Should_Belong_To_OperationType_Only()
    {
        var owningCategories = TagCategorySeedData.Categories
            .Where(category => category.Values.Contains("Attachments", StringComparer.OrdinalIgnoreCase))
            .Select(category => category.Name)
            .ToList();

        // An attachment run is the combination entitytype:tender + operationtype:attachments.
        // Keeping the value under entityType as well makes two different tags look interchangeable,
        // and a pool tagged with the wrong one silently resolves to zero proxies.
        owningCategories.ShouldBe(["operationType"]);
    }

    [Fact]
    public void EntityType_Should_Spell_QuoteAgreementHardwareStorage_Correctly()
    {
        var entityType = TagCategorySeedData.Categories.Single(category => category.Name == "entityType");

        entityType.Values.ShouldContain("QuoteAgreementHardwareStorage");
        entityType.Values.ShouldNotContain("QuoteAgreementHardwareStorare");
    }

    [Fact]
    public void Every_Category_Should_Have_Distinct_Values()
    {
        foreach (var (name, values) in TagCategorySeedData.Categories)
        {
            values.Distinct(StringComparer.OrdinalIgnoreCase).Count()
                .ShouldBe(values.Count, $"category '{name}' declares a duplicate value");
        }
    }
}
```

- [ ] **Step 2: Run the tests to verify the first two fail**

```bash
dotnet test src/Tests/Proxies.Tests/Proxies.Tests.csproj --filter "FullyQualifiedName~TagCategorySeedDataTests"
```

Expected: `Attachments_Should_Belong_To_OperationType_Only` FAILS (actual is `["entityType", "operationType"]`), `EntityType_Should_Spell_QuoteAgreementHardwareStorage_Correctly` FAILS (value not found), `Every_Category_Should_Have_Distinct_Values` PASSES.

- [ ] **Step 3: Apply the two corrections**

In `src/Modules/Proxies/Modules.Proxies/Data/TagCategorySeedData.cs`, inside the `entityType` category, delete the `"Attachments",` line and change `"QuoteAgreementHardwareStorare"` to `"QuoteAgreementHardwareStorage"`. The `entityType` list becomes exactly:

```csharp
        ("entityType", [
            "Tender",
            "PurchaseOrder",
            "PAC",
            "RFI",
            "BigPurchase",
            "QuoteAgreement",
            "QuoteAgreementHardwareStorage",
            "QuoteAgreementTransportation",
            "Claim",
            "DirectDeal",
            "QuoteRequest",
            "QuickBid",
        ]),
```

Leave the `operationType` category untouched — it already reads `("operationType", ["Attachments"])`.

- [ ] **Step 4: Run the tests to verify they pass**

```bash
dotnet test src/Tests/Proxies.Tests/Proxies.Tests.csproj --filter "FullyQualifiedName~TagCategorySeedDataTests"
```

Expected: 3 passed.

- [ ] **Step 5: Build to confirm no analyzer regression**

```bash
dotnet build src/FS.Proxy.slnx
```

Expected: build succeeded, 0 warnings (warnings are errors in this repo).

- [ ] **Step 6: Commit**

```bash
git add src/Modules/Proxies/Modules.Proxies/Data/TagCategorySeedData.cs src/Tests/Proxies.Tests/Data/TagCategorySeedDataTests.cs
git commit -m "fix(proxies): correct seeded tag catalog

Attachments belongs to operationType only; an attachment run is the
combination entitytype:tender + operationtype:attachments, which the
AND-semantics of RequestProxiesQueryHandler already expresses. The
entityType duplicate made two different tags look interchangeable.

QuoteAgreementHardwareStorare was a typo for Storage.

Guard tests pin both corrections so a future edit cannot silently
reintroduce either.

Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>"
```

---

### Task 2: Runbook for QA and Production

**The source change from Task 1 does not reach deployed environments.** `ProxiesDbInitializer.SeedTagCategoriesAsync` opens with `if (await dbContext.TagCategories.AnyAsync(cancellationToken)) return;` — it seeds only into an empty catalog. QA and Production already have rows, so they need an explicit data correction.

No migration is required: `AddTagCategoryValue` and `RemoveTagCategoryValue` already exist as admin endpoints.

The subtle part is the third step. `TagCategory` has **no foreign key** to `Tag` or `ProxyTagAssignment` — deliberately, per the tag-categories spec — so removing a catalog value leaves any already-assigned tag row in place. A proxy left tagged `entitytype:attachments` would then match a tag nobody ever requests, and would silently drop out of every pool. This is cheap to check now, before proxy tagging starts in earnest, and expensive later.

**Files:**
- Create: `docs/runbooks/2026-09-14-tag-catalog-correction.md`

**Interfaces:**
- Consumes: `GET /api/v1/proxies/tag-categories` (`ListTagCategories`, requires `ProxiesPermissions.Tags.View`); `POST /api/v1/proxies/tag-categories/{id:guid}/values` with body `{"value":"…"}` (`AddTagCategoryValue`, requires `Tags.Update`); `DELETE /api/v1/proxies/tag-categories/{id:guid}/values/{value}` (`RemoveTagCategoryValue`, requires `Tags.Update`); `GET /api/v1/proxies?Tags=…` (`ListProxies`, requires `ProviderAccounts.View`). All take a JWT bearer token, not an API key.
- Produces: nothing consumed by later tasks. This task is operational.

- [ ] **Step 1: Write the runbook**

Create `docs/runbooks/2026-09-14-tag-catalog-correction.md`:

````markdown
# Runbook — Tag catalog correction (QA + Production)

Companion to `docs/superpowers/specs/2026-09-14-proxy-client-sdk-design.md` §5.1.

## Why this is needed

`ProxiesDbInitializer.SeedTagCategoriesAsync` seeds the tag catalog **only when the
`TagCategories` table is empty**. Correcting `TagCategorySeedData.cs` therefore fixes new
environments and nothing else. QA and Production must be corrected through the admin API.

## What changes

1. Remove the value `Attachments` from the category `entityType`. It belongs to `operationType`
   only; an attachment run is the combination `entitytype:tender` + `operationtype:attachments`.
2. In `entityType`, replace `QuoteAgreementHardwareStorare` with `QuoteAgreementHardwareStorage`.

## Before you start

- A JWT for a user holding `ProxiesPermissions.Tags.Update` and `ProxiesPermissions.ProviderAccounts.View`.
- `BASE` set to the environment root, e.g. `export BASE=https://proxy-qa.falconsoft.cl`.
- `TOKEN` set to that JWT.

Run the whole sequence against **QA first**, verify, then repeat against Production.

## Step 1 — Find the `entityType` category id

```bash
curl -s -H "Authorization: Bearer $TOKEN" "$BASE/api/v1/proxies/tag-categories" \
  | jq -r '.[] | select(.name=="entityType") | .id'
```

Export it: `export CAT=<the guid printed above>`

## Step 2 — Check nothing is already tagged with the values being removed

```bash
curl -s -H "Authorization: Bearer $TOKEN" \
  "$BASE/api/v1/proxies?Tags=entitytype:attachments&PageSize=1" | jq '.totalCount'

curl -s -H "Authorization: Bearer $TOKEN" \
  "$BASE/api/v1/proxies?Tags=entitytype:quoteagreementhardwarestorare&PageSize=1" | jq '.totalCount'
```

Both must print `0`.

**If either is non-zero, stop.** Those proxies carry a tag that is about to stop appearing in the
catalog. The catalog has no foreign key to `Tag`, so removing the value here will not remove the
assignment — the proxies would keep a tag nobody requests and quietly fall out of every pool.
Re-tag them first (`entitytype:attachments` → `entitytype:tender` + `operationtype:attachments`;
`…storare` → `…storage`) using the proxy tagging UI, then re-run this step.

## Step 3 — Apply the corrections

```bash
curl -s -X DELETE -H "Authorization: Bearer $TOKEN" \
  "$BASE/api/v1/proxies/tag-categories/$CAT/values/Attachments"

curl -s -X DELETE -H "Authorization: Bearer $TOKEN" \
  "$BASE/api/v1/proxies/tag-categories/$CAT/values/QuoteAgreementHardwareStorare"

curl -s -X POST -H "Authorization: Bearer $TOKEN" -H "Content-Type: application/json" \
  -d '{"value":"QuoteAgreementHardwareStorage"}' \
  "$BASE/api/v1/proxies/tag-categories/$CAT/values"
```

Each returns `204 No Content`.

## Step 4 — Verify

```bash
curl -s -H "Authorization: Bearer $TOKEN" "$BASE/api/v1/proxies/tag-categories" \
  | jq '.[] | select(.name=="entityType" or .name=="operationType") | {name, values}'
```

Expected: `entityType.values` contains `QuoteAgreementHardwareStorage` and contains neither
`Attachments` nor `QuoteAgreementHardwareStorare`. `operationType.values` is `["Attachments"]`.

## Rollback

Re-add what was removed:

```bash
curl -s -X POST -H "Authorization: Bearer $TOKEN" -H "Content-Type: application/json" \
  -d '{"value":"Attachments"}' "$BASE/api/v1/proxies/tag-categories/$CAT/values"
```

The catalog is advisory and has no foreign keys, so adding and removing values never touches
assigned tags, proxies, or policies. Rollback is safe at any point.
````

- [ ] **Step 2: Verify the routes quoted in the runbook match the code**

```bash
grep -n "MapGet\|MapPost\|MapDelete" \
  src/Modules/Proxies/Modules.Proxies/Features/v1/TagCategories/ListTagCategories/ListTagCategoriesEndpoint.cs \
  src/Modules/Proxies/Modules.Proxies/Features/v1/TagCategories/AddTagCategoryValue/AddTagCategoryValueEndpoint.cs \
  src/Modules/Proxies/Modules.Proxies/Features/v1/TagCategories/RemoveTagCategoryValue/RemoveTagCategoryValueEndpoint.cs
```

Expected: `"/tag-categories"`, `"/tag-categories/{id:guid}/values"`, `"/tag-categories/{id:guid}/values/{value}"`. If any differ, correct the runbook before committing.

- [ ] **Step 3: Commit**

```bash
git add docs/runbooks/2026-09-14-tag-catalog-correction.md
git commit -m "docs: add runbook for correcting the tag catalog in QA and Production

SeedTagCategoriesAsync only seeds into an empty catalog, so the source
fix in the previous commit does not reach deployed environments. The
runbook drives the existing Add/RemoveTagCategoryValue endpoints and,
critically, checks first that no proxy already carries the values being
removed: the catalog has no FK to Tag, so a stale assignment would
survive and match nothing.

Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>"
```

- [ ] **Step 4: Execute the runbook against QA, then Production**

This is an operator step, not a code step. Record the `totalCount` values observed in Step 2 of the runbook in the PR description, so the reviewer can see whether any re-tagging was needed.

---

# PHASE 1 — Batch feedback endpoint

### Task 3: Batch feedback contracts and validator

**Files:**
- Create: `src/Modules/Proxies/Modules.Proxies.Contracts/Dtos/ProxyFeedbackEvent.cs`
- Create: `src/Modules/Proxies/Modules.Proxies.Contracts/Dtos/BatchFeedbackResult.cs`
- Create: `src/Modules/Proxies/Modules.Proxies.Contracts/v1/Proxies/ReportProxyFeedbackBatchCommand.cs`
- Create: `src/Modules/Proxies/Modules.Proxies/Features/v1/Proxies/ReportProxyFeedbackBatch/ReportProxyFeedbackBatchCommandValidator.cs`
- Test: `src/Tests/Proxies.Tests/Validators/ReportProxyFeedbackBatchValidatorTests.cs`

**Interfaces:**
- Consumes: `FSH.Modules.Proxies.Contracts.UsageEventOutcome` (enum: `Success`, `Failure`, `Banned`, `Timeout`).
- Produces, for Tasks 4 and 5:
  - `ProxyFeedbackEvent(Guid ProxyId, UsageEventOutcome Outcome, string? Detail)`
  - `BatchFeedbackResult(int Accepted, IReadOnlyList<Guid> Rejected)`
  - `ReportProxyFeedbackBatchCommand(IReadOnlyList<ProxyFeedbackEvent> Events, string? ReporterIdentifier) : ICommand<BatchFeedbackResult>`
  - `ReportProxyFeedbackBatchCommandValidator`

- [ ] **Step 1: Write the failing test**

Create `src/Tests/Proxies.Tests/Validators/ReportProxyFeedbackBatchValidatorTests.cs`:

```csharp
using FSH.Modules.Proxies.Contracts;
using FSH.Modules.Proxies.Contracts.Dtos;
using FSH.Modules.Proxies.Contracts.v1.Proxies;
using FSH.Modules.Proxies.Features.v1.Proxies.ReportProxyFeedbackBatch;
using Shouldly;
using Xunit;

namespace Proxies.Tests.Validators;

public sealed class ReportProxyFeedbackBatchValidatorTests
{
    private readonly ReportProxyFeedbackBatchCommandValidator _validator = new();

    private static ProxyFeedbackEvent AnEvent(string? detail = null) =>
        new(Guid.NewGuid(), UsageEventOutcome.Banned, detail);

    [Fact]
    public void Should_Pass_When_BatchIsValid()
    {
        var command = new ReportProxyFeedbackBatchCommand([AnEvent("mercadopublico:robot-check")], null);

        _validator.Validate(command).IsValid.ShouldBeTrue();
    }

    [Fact]
    public void Should_Fail_When_BatchIsEmpty()
    {
        var command = new ReportProxyFeedbackBatchCommand([], null);

        _validator.Validate(command).IsValid.ShouldBeFalse();
    }

    [Fact]
    public void Should_Pass_When_BatchIsAtMaxSize()
    {
        var events = Enumerable.Range(0, 200).Select(_ => AnEvent()).ToList();
        var command = new ReportProxyFeedbackBatchCommand(events, null);

        _validator.Validate(command).IsValid.ShouldBeTrue();
    }

    [Fact]
    public void Should_Fail_When_BatchExceedsMaxSize()
    {
        var events = Enumerable.Range(0, 201).Select(_ => AnEvent()).ToList();
        var command = new ReportProxyFeedbackBatchCommand(events, null);

        _validator.Validate(command).IsValid.ShouldBeFalse();
    }

    [Fact]
    public void Should_Fail_When_AnyEventHasEmptyProxyId()
    {
        var command = new ReportProxyFeedbackBatchCommand(
            [AnEvent(), new ProxyFeedbackEvent(Guid.Empty, UsageEventOutcome.Failure, null)], null);

        _validator.Validate(command).IsValid.ShouldBeFalse();
    }

    [Fact]
    public void Should_Fail_When_AnyEventHasUnknownOutcome()
    {
        var command = new ReportProxyFeedbackBatchCommand(
            [new ProxyFeedbackEvent(Guid.NewGuid(), (UsageEventOutcome)99, null)], null);

        _validator.Validate(command).IsValid.ShouldBeFalse();
    }

    [Fact]
    public void Should_Fail_When_AnyEventDetailExceeds2048Chars()
    {
        var command = new ReportProxyFeedbackBatchCommand([AnEvent(new string('a', 2049))], null);

        _validator.Validate(command).IsValid.ShouldBeFalse();
    }

    [Fact]
    public void Should_Pass_When_DetailIsAt2048Chars()
    {
        var command = new ReportProxyFeedbackBatchCommand([AnEvent(new string('a', 2048))], null);

        _validator.Validate(command).IsValid.ShouldBeTrue();
    }
}
```

- [ ] **Step 2: Run the test to verify it fails**

```bash
dotnet test src/Tests/Proxies.Tests/Proxies.Tests.csproj --filter "FullyQualifiedName~ReportProxyFeedbackBatchValidatorTests"
```

Expected: compilation FAILS — `ProxyFeedbackEvent`, `BatchFeedbackResult`, `ReportProxyFeedbackBatchCommand` and `ReportProxyFeedbackBatchCommandValidator` do not exist.

- [ ] **Step 3: Create the two DTOs**

`src/Modules/Proxies/Modules.Proxies.Contracts/Dtos/ProxyFeedbackEvent.cs`:

```csharp
namespace FSH.Modules.Proxies.Contracts.Dtos;

/// <summary>One reported proxy usage outcome. The unit of a batched feedback submission.</summary>
/// <remarks>
/// Deliberately carries no timestamp. <c>ProxyUsageEvent.Create</c> stamps <c>OccurredAtUtc</c> with
/// server time and accepts no external value; with policy windows measured in minutes and clients
/// flushing every ten seconds, server time is accurate enough, and accepting a client clock would
/// invite skew and manipulation for no benefit.
/// </remarks>
public sealed record ProxyFeedbackEvent(Guid ProxyId, UsageEventOutcome Outcome, string? Detail);
```

`src/Modules/Proxies/Modules.Proxies.Contracts/Dtos/BatchFeedbackResult.cs`:

```csharp
namespace FSH.Modules.Proxies.Contracts.Dtos;

/// <summary>
/// Outcome of a batched feedback submission. <paramref name="Rejected"/> lists the distinct proxy ids
/// that no longer exist — a proxy can be retired between the moment a client leases it and the moment
/// the buffered event is flushed, and failing the whole batch for that would make the client retry a
/// submission that can never succeed.
/// </summary>
public sealed record BatchFeedbackResult(int Accepted, IReadOnlyList<Guid> Rejected);
```

- [ ] **Step 4: Create the command**

`src/Modules/Proxies/Modules.Proxies.Contracts/v1/Proxies/ReportProxyFeedbackBatchCommand.cs`:

```csharp
using FSH.Modules.Proxies.Contracts.Dtos;
using Mediator;

namespace FSH.Modules.Proxies.Contracts.v1.Proxies;

public sealed record ReportProxyFeedbackBatchCommand(
    IReadOnlyList<ProxyFeedbackEvent> Events, string? ReporterIdentifier) : ICommand<BatchFeedbackResult>;
```

- [ ] **Step 5: Create the validator**

`src/Modules/Proxies/Modules.Proxies/Features/v1/Proxies/ReportProxyFeedbackBatch/ReportProxyFeedbackBatchCommandValidator.cs`:

```csharp
using FluentValidation;
using FSH.Modules.Proxies.Contracts.v1.Proxies;

namespace FSH.Modules.Proxies.Features.v1.Proxies.ReportProxyFeedbackBatch;

public sealed class ReportProxyFeedbackBatchCommandValidator : AbstractValidator<ReportProxyFeedbackBatchCommand>
{
    /// <summary>
    /// Ceiling on one submission. Chosen to sit in the same order of magnitude as the cap of 50 on
    /// <c>RequestProxiesQuery.Count</c>: large enough that a client flushing every ten seconds never
    /// splits a normal batch, small enough that one request cannot fan out into an unbounded number
    /// of inserts and policy evaluations.
    /// </summary>
    private const int MaxBatchSize = 200;

    public ReportProxyFeedbackBatchCommandValidator()
    {
        RuleFor(x => x.Events).NotEmpty();
        RuleFor(x => x.Events.Count).InclusiveBetween(1, MaxBatchSize).When(x => x.Events is not null);
        RuleForEach(x => x.Events).ChildRules(each =>
        {
            each.RuleFor(e => e.ProxyId).NotEmpty();
            each.RuleFor(e => e.Outcome).IsInEnum();
            each.RuleFor(e => e.Detail).MaximumLength(2048);
        });
    }
}
```

- [ ] **Step 6: Run the test to verify it passes**

```bash
dotnet test src/Tests/Proxies.Tests/Proxies.Tests.csproj --filter "FullyQualifiedName~ReportProxyFeedbackBatchValidatorTests"
```

Expected: 8 passed.

- [ ] **Step 7: Commit**

```bash
git add src/Modules/Proxies/Modules.Proxies.Contracts/Dtos/ProxyFeedbackEvent.cs \
        src/Modules/Proxies/Modules.Proxies.Contracts/Dtos/BatchFeedbackResult.cs \
        src/Modules/Proxies/Modules.Proxies.Contracts/v1/Proxies/ReportProxyFeedbackBatchCommand.cs \
        src/Modules/Proxies/Modules.Proxies/Features/v1/Proxies/ReportProxyFeedbackBatch/ReportProxyFeedbackBatchCommandValidator.cs \
        src/Tests/Proxies.Tests/Validators/ReportProxyFeedbackBatchValidatorTests.cs
git commit -m "feat(proxies): add batched feedback contracts and validator

Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>"
```

---

### Task 4: Batch feedback handler

The point of the whole endpoint lives here. `ReportProxyFeedbackCommandHandler` calls `IPolicyEvaluationService.EvaluateAsync` once per event, and each call issues two queries plus a possible disable and renewal. At 10k–500k proxied requests a day that inline evaluation becomes the bottleneck. The batch handler inserts once and evaluates **once per distinct proxy that carried a negative outcome**.

Restricting to negative outcomes is a refinement over the spec's wording ("one evaluation per distinct proxyId"). It is safe and strictly cheaper: `PolicyEvaluationService` only counts events where `Outcome != Success`, so evaluating a proxy whose batch contained nothing but successes cannot change any decision.

**Files:**
- Create: `src/Modules/Proxies/Modules.Proxies/Features/v1/Proxies/ReportProxyFeedbackBatch/ReportProxyFeedbackBatchCommandHandler.cs`
- Test: `src/Tests/Proxies.Tests/Handlers/ReportProxyFeedbackBatchHandlerTests.cs`

**Interfaces:**
- Consumes: `ReportProxyFeedbackBatchCommand`, `ProxyFeedbackEvent`, `BatchFeedbackResult` (Task 3); `ProxiesDbContext`; `IPolicyEvaluationService.EvaluateAsync(Guid, CancellationToken)`; `ProxyUsageEvent.Create(Guid proxyId, UsageEventSource source, UsageEventOutcome outcome, Guid? healthCheckTargetId, Guid? reportedByApiClientId, string? detail)`.
- Produces, for Task 5: `ReportProxyFeedbackBatchCommandHandler(ProxiesDbContext, IPolicyEvaluationService)`.

- [ ] **Step 1: Write the failing test**

Create `src/Tests/Proxies.Tests/Handlers/ReportProxyFeedbackBatchHandlerTests.cs`:

```csharp
using FSH.Modules.Proxies.Contracts;
using FSH.Modules.Proxies.Contracts.Dtos;
using FSH.Modules.Proxies.Contracts.v1.Proxies;
using FSH.Modules.Proxies.Domain;
using FSH.Modules.Proxies.Features.v1.Proxies.ReportProxyFeedbackBatch;
using FSH.Modules.Proxies.Services;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using Shouldly;
using Xunit;

namespace Proxies.Tests.Handlers;

public sealed class ReportProxyFeedbackBatchHandlerTests
{
    private static FSH.Modules.Proxies.Data.ProxiesDbContext CreateDb() =>
        Proxies.Tests.TestProxiesDbContext.Create(new DbContextOptionsBuilder<FSH.Modules.Proxies.Data.ProxiesDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    private static Proxy NewProxy(string host) =>
        Proxy.Create(ManualProviderAccount.Id, host, 80, ProxyProtocol.Http, null, null, null);

    [Fact]
    public async Task Handle_Should_EvaluatePolicy_OncePerDistinctProxy_NotOncePerEvent()
    {
        await using var db = CreateDb();
        var first = NewProxy("1.1.1.1");
        var second = NewProxy("2.2.2.2");
        db.Proxies.AddRange(first, second);
        await db.SaveChangesAsync();
        var policyService = Substitute.For<IPolicyEvaluationService>();
        var sut = new ReportProxyFeedbackBatchCommandHandler(db, policyService);

        // Six events spread over two proxies.
        var events = new List<ProxyFeedbackEvent>
        {
            new(first.Id, UsageEventOutcome.Banned, null),
            new(first.Id, UsageEventOutcome.Timeout, null),
            new(first.Id, UsageEventOutcome.Failure, null),
            new(second.Id, UsageEventOutcome.Banned, null),
            new(second.Id, UsageEventOutcome.Failure, null),
            new(second.Id, UsageEventOutcome.Timeout, null),
        };

        var result = await sut.Handle(new ReportProxyFeedbackBatchCommand(events, null), CancellationToken.None);

        result.Accepted.ShouldBe(6);
        (await db.ProxyUsageEvents.CountAsync()).ShouldBe(6);
        await policyService.Received(1).EvaluateAsync(first.Id, Arg.Any<CancellationToken>());
        await policyService.Received(1).EvaluateAsync(second.Id, Arg.Any<CancellationToken>());
        await policyService.Received(2).EvaluateAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_Should_NotEvaluatePolicy_ForProxiesWithOnlySuccesses()
    {
        await using var db = CreateDb();
        var proxy = NewProxy("1.1.1.1");
        db.Proxies.Add(proxy);
        await db.SaveChangesAsync();
        var policyService = Substitute.For<IPolicyEvaluationService>();
        var sut = new ReportProxyFeedbackBatchCommandHandler(db, policyService);

        var events = new List<ProxyFeedbackEvent>
        {
            new(proxy.Id, UsageEventOutcome.Success, null),
            new(proxy.Id, UsageEventOutcome.Success, null),
        };

        var result = await sut.Handle(new ReportProxyFeedbackBatchCommand(events, null), CancellationToken.None);

        // The events are still recorded...
        result.Accepted.ShouldBe(2);
        (await db.ProxyUsageEvents.CountAsync()).ShouldBe(2);
        // ...but PolicyEvaluationService only counts Outcome != Success, so evaluating is pure cost.
        await policyService.DidNotReceive().EvaluateAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_Should_AcceptKnownProxies_And_RejectUnknownOnes()
    {
        await using var db = CreateDb();
        var known = NewProxy("1.1.1.1");
        db.Proxies.Add(known);
        await db.SaveChangesAsync();
        var missing = Guid.NewGuid();
        var sut = new ReportProxyFeedbackBatchCommandHandler(db, Substitute.For<IPolicyEvaluationService>());

        var events = new List<ProxyFeedbackEvent>
        {
            new(known.Id, UsageEventOutcome.Banned, null),
            new(missing, UsageEventOutcome.Banned, null),
        };

        var result = await sut.Handle(new ReportProxyFeedbackBatchCommand(events, null), CancellationToken.None);

        result.Accepted.ShouldBe(1);
        result.Rejected.ShouldBe([missing]);
        (await db.ProxyUsageEvents.CountAsync()).ShouldBe(1);
    }

    [Fact]
    public async Task Handle_Should_ResolveKnownApiClientAsReporter()
    {
        await using var db = CreateDb();
        var proxy = NewProxy("1.1.1.1");
        var reporter = ApiClient.Create("tag-scraper", "hash");
        db.Proxies.Add(proxy);
        db.ApiClients.Add(reporter);
        await db.SaveChangesAsync();
        var sut = new ReportProxyFeedbackBatchCommandHandler(db, Substitute.For<IPolicyEvaluationService>());

        await sut.Handle(
            new ReportProxyFeedbackBatchCommand(
                [new ProxyFeedbackEvent(proxy.Id, UsageEventOutcome.Banned, "robot-check")],
                reporter.Id.ToString()),
            CancellationToken.None);

        var stored = await db.ProxyUsageEvents.SingleAsync();
        stored.ReportedByApiClientId.ShouldBe(reporter.Id);
        stored.Source.ShouldBe(UsageEventSource.ConsumerFeedback);
        stored.Detail.ShouldBe("robot-check");
    }

    [Fact]
    public async Task Handle_Should_LeaveReporterNull_When_IdentifierIsNotAKnownApiClient()
    {
        await using var db = CreateDb();
        var proxy = NewProxy("1.1.1.1");
        db.Proxies.Add(proxy);
        await db.SaveChangesAsync();
        var sut = new ReportProxyFeedbackBatchCommandHandler(db, Substitute.For<IPolicyEvaluationService>());

        await sut.Handle(
            new ReportProxyFeedbackBatchCommand(
                [new ProxyFeedbackEvent(proxy.Id, UsageEventOutcome.Failure, null)],
                "a-jwt-user-id-not-an-api-client"),
            CancellationToken.None);

        (await db.ProxyUsageEvents.SingleAsync()).ReportedByApiClientId.ShouldBeNull();
    }

    [Fact]
    public async Task Handle_Should_ReturnEmptyResult_When_NoProxyInBatchExists()
    {
        await using var db = CreateDb();
        var policyService = Substitute.For<IPolicyEvaluationService>();
        var sut = new ReportProxyFeedbackBatchCommandHandler(db, policyService);
        var missing = Guid.NewGuid();

        var result = await sut.Handle(
            new ReportProxyFeedbackBatchCommand([new ProxyFeedbackEvent(missing, UsageEventOutcome.Banned, null)], null),
            CancellationToken.None);

        // Unlike the single-event endpoint, an unknown proxy must NOT throw NotFoundException: the
        // client would retry the batch forever.
        result.Accepted.ShouldBe(0);
        result.Rejected.ShouldBe([missing]);
        await policyService.DidNotReceive().EvaluateAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }
}
```

- [ ] **Step 2: Run the test to verify it fails**

```bash
dotnet test src/Tests/Proxies.Tests/Proxies.Tests.csproj --filter "FullyQualifiedName~ReportProxyFeedbackBatchHandlerTests"
```

Expected: compilation FAILS — `ReportProxyFeedbackBatchCommandHandler` does not exist.

- [ ] **Step 3: Write the handler**

`src/Modules/Proxies/Modules.Proxies/Features/v1/Proxies/ReportProxyFeedbackBatch/ReportProxyFeedbackBatchCommandHandler.cs`:

```csharp
using FSH.Modules.Proxies.Contracts;
using FSH.Modules.Proxies.Contracts.Dtos;
using FSH.Modules.Proxies.Contracts.v1.Proxies;
using FSH.Modules.Proxies.Data;
using FSH.Modules.Proxies.Domain;
using FSH.Modules.Proxies.Services;
using Mediator;
using Microsoft.EntityFrameworkCore;

namespace FSH.Modules.Proxies.Features.v1.Proxies.ReportProxyFeedbackBatch;

/// <summary>
/// Batched sibling of <see cref="ReportProxyFeedback.ReportProxyFeedbackCommandHandler"/>. Two
/// deliberate differences from the single-event handler:
///
/// 1. An unknown proxy is rejected, not thrown on. A proxy can be retired between the moment a client
///    leases it and the moment its buffered event is flushed; a <c>NotFoundException</c> would fail the
///    whole batch and the client would retry a submission that can never succeed.
/// 2. The policy engine runs once per distinct proxy carrying a NEGATIVE outcome, not once per event.
///    <see cref="PolicyEvaluationService"/> counts only <c>Outcome != Success</c> events, so a proxy
///    whose batch held nothing but successes cannot change any decision — evaluating it is pure cost.
/// </summary>
public sealed class ReportProxyFeedbackBatchCommandHandler(
    ProxiesDbContext dbContext, IPolicyEvaluationService policyEvaluationService)
    : ICommandHandler<ReportProxyFeedbackBatchCommand, BatchFeedbackResult>
{
    public async ValueTask<BatchFeedbackResult> Handle(
        ReportProxyFeedbackBatchCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        var requestedProxyIds = command.Events.Select(e => e.ProxyId).Distinct().ToList();

        var knownProxyIds = (await dbContext.Proxies
            .Where(p => requestedProxyIds.Contains(p.Id))
            .Select(p => p.Id)
            .ToListAsync(cancellationToken).ConfigureAwait(false))
            .ToHashSet();

        Guid? reporterId = null;
        if (Guid.TryParse(command.ReporterIdentifier, out var parsed) &&
            await dbContext.ApiClients.AnyAsync(c => c.Id == parsed, cancellationToken).ConfigureAwait(false))
        {
            reporterId = parsed;
        }

        int accepted = 0;
        foreach (var feedback in command.Events)
        {
            if (!knownProxyIds.Contains(feedback.ProxyId))
            {
                continue;
            }

            dbContext.ProxyUsageEvents.Add(ProxyUsageEvent.Create(
                feedback.ProxyId, UsageEventSource.ConsumerFeedback, feedback.Outcome,
                healthCheckTargetId: null, reporterId, feedback.Detail));
            accepted++;
        }

        if (accepted > 0)
        {
            await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }

        var proxiesToEvaluate = command.Events
            .Where(e => e.Outcome != UsageEventOutcome.Success && knownProxyIds.Contains(e.ProxyId))
            .Select(e => e.ProxyId)
            .Distinct();

        foreach (var proxyId in proxiesToEvaluate)
        {
            await policyEvaluationService.EvaluateAsync(proxyId, cancellationToken).ConfigureAwait(false);
        }

        var rejected = requestedProxyIds.Where(id => !knownProxyIds.Contains(id)).ToList();
        return new BatchFeedbackResult(accepted, rejected);
    }
}
```

- [ ] **Step 4: Run the test to verify it passes**

```bash
dotnet test src/Tests/Proxies.Tests/Proxies.Tests.csproj --filter "FullyQualifiedName~ReportProxyFeedbackBatchHandlerTests"
```

Expected: 6 passed.

- [ ] **Step 5: Run the whole Proxies suite and build**

```bash
dotnet test src/Tests/Proxies.Tests/Proxies.Tests.csproj
dotnet build src/FS.Proxy.slnx
```

Expected: all tests pass; build succeeds with 0 warnings. `Architecture.Tests` enforces that every command handler has a matching `{Name}Validator` — `ReportProxyFeedbackBatchCommandValidator` from Task 3 satisfies that. If `Architecture.Tests` was not run above, run it now: `dotnet test src/Tests/Architecture.Tests/Architecture.Tests.csproj`.

- [ ] **Step 6: Commit**

```bash
git add src/Modules/Proxies/Modules.Proxies/Features/v1/Proxies/ReportProxyFeedbackBatch/ReportProxyFeedbackBatchCommandHandler.cs \
        src/Tests/Proxies.Tests/Handlers/ReportProxyFeedbackBatchHandlerTests.cs
git commit -m "feat(proxies): add batched feedback handler

Inserts every accepted event in one SaveChangesAsync, then runs the
policy engine once per distinct proxy carrying a negative outcome
rather than once per event. A batch of 200 events over 20 proxies drops
from 200 evaluations to at most 20.

Unknown proxy ids are rejected rather than thrown on: a proxy can be
retired between lease and flush, and failing the batch would make the
client retry forever.

Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>"
```

---

### Task 5: Endpoint, module wiring, and end-to-end test

**Files:**
- Create: `src/Modules/Proxies/Modules.Proxies/Features/v1/Proxies/ReportProxyFeedbackBatch/ReportProxyFeedbackBatchEndpoint.cs`
- Modify: `src/Modules/Proxies/Modules.Proxies/ProxiesModule.cs` (one `using`, one call inside `MapEndpoints`)
- Test: `src/Tests/Integration.Tests/Tests/Proxies/ProxyFeedbackBatchTests.cs` (create, and create the `Proxies` directory)

**Interfaces:**
- Consumes: `ReportProxyFeedbackBatchCommand`, `ProxyFeedbackEvent`, `BatchFeedbackResult` (Task 3); `ReportProxyFeedbackBatchCommandHandler` (Task 4); `ApiKeyAuthenticationDefaults.ConsumerPolicyName` (`"ProxiesConsumerAccess"`) and `ApiKeyAuthenticationDefaults.HeaderName` (`"X-Api-Key"`).
- Produces: route `POST /api/v1/proxies/feedback/batch`, endpoint name `"ReportProxyFeedbackBatch"`. The SDK (a later phase) is its only intended caller.

**Route safety note:** the sibling endpoint is `POST /{id:guid}/feedback`. `/feedback/batch` cannot collide with it, because the `:guid` constraint rejects the literal segment `feedback`.

- [ ] **Step 1: Write the endpoint**

`src/Modules/Proxies/Modules.Proxies/Features/v1/Proxies/ReportProxyFeedbackBatch/ReportProxyFeedbackBatchEndpoint.cs`:

```csharp
using System.Security.Claims;
using FSH.Modules.Proxies.Authentication;
using FSH.Modules.Proxies.Contracts.Dtos;
using FSH.Modules.Proxies.Contracts.v1.Proxies;
using Mediator;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace FSH.Modules.Proxies.Features.v1.Proxies.ReportProxyFeedbackBatch;

public static class ReportProxyFeedbackBatchEndpoint
{
    internal static RouteHandlerBuilder MapReportProxyFeedbackBatchEndpoint(this IEndpointRouteBuilder endpoints)
    {
        return endpoints.MapPost("/feedback/batch",
                async (ReportProxyFeedbackBatchBody body, ClaimsPrincipal user, IMediator mediator, CancellationToken ct) =>
                {
                    string? reporterIdentifier = user.FindFirst(ClaimTypes.NameIdentifier)?.Value;
                    var result = await mediator.Send(
                        new ReportProxyFeedbackBatchCommand(body.Events, reporterIdentifier), ct);
                    return Results.Ok(result);
                })
            .WithName("ReportProxyFeedbackBatch")
            .WithSummary("Report the outcome of using several proxies in a single call")
            .RequireAuthorization(ApiKeyAuthenticationDefaults.ConsumerPolicyName);
    }

    internal sealed record ReportProxyFeedbackBatchBody(IReadOnlyList<ProxyFeedbackEvent> Events);
}
```

- [ ] **Step 2: Wire it into the module**

In `src/Modules/Proxies/Modules.Proxies/ProxiesModule.cs`, add the `using` next to the existing `ReportProxyFeedback` one, keeping alphabetical order:

```csharp
using FSH.Modules.Proxies.Features.v1.Proxies.ReportProxyFeedback;
using FSH.Modules.Proxies.Features.v1.Proxies.ReportProxyFeedbackBatch;
```

and register the route immediately after the existing feedback endpoint inside `MapEndpoints`:

```csharp
        group.MapReportProxyFeedbackEndpoint();
        group.MapReportProxyFeedbackBatchEndpoint();
```

- [ ] **Step 3: Build**

```bash
dotnet build src/FS.Proxy.slnx
```

Expected: build succeeded, 0 warnings.

- [ ] **Step 4: Write the integration test**

Requires Docker — `Integration.Tests` uses Testcontainers.

Facts this test relies on, all verified against the code: `Program.cs:27` registers
`JsonStringEnumConverter`, so enums travel as strings in both directions; `POST /api/v1/proxies/api-clients`
takes `CreateApiClientCommand(string Name)` and returns `CreateApiClientResult(Guid Id, string PlaintextKey)`;
`POST /api/v1/proxies/manual-proxies` takes `CreateManualProxyCommand(string Host, int Port, ProxyProtocol
Protocol, string? Username, string? PlaintextPassword, IReadOnlyList<string> TagNames)` and returns a bare
`Guid`; `ProxyProtocol` is `{ Http, Https, Socks5 }`. `GlobalUsings.cs` already provides `Shouldly`, `Xunit`,
`System.Net`, `System.Net.Http.Json` and `System.Net.Http.Headers`.

Create `src/Tests/Integration.Tests/Tests/Proxies/ProxyFeedbackBatchTests.cs`:

```csharp
using FSH.Modules.Proxies.Contracts.Dtos;
using Integration.Tests.Infrastructure;
#pragma warning disable CA1707 // Test method names use underscores by convention

namespace Integration.Tests.Tests.Proxies;

/// <summary>
/// End-to-end cover for <c>POST /api/v1/proxies/feedback/batch</c>. Proves three things the handler
/// unit tests cannot: that <c>ApiKeyAuthenticationHandler</c> admits an <c>X-Api-Key</c>-only request,
/// that <c>ProxiesConsumerAuthorizationHandler</c> does not reject an API-key principal, and that the
/// route does not collide with the sibling <c>POST /{id:guid}/feedback</c>.
/// </summary>
[Collection(FshCollectionDefinition.Name)]
public sealed class ProxyFeedbackBatchTests
{
    private const string ProxiesBasePath = "/api/v1/proxies";

    private readonly FshWebApplicationFactory _factory;
    private readonly AuthHelper _auth;

    public ProxyFeedbackBatchTests(FshWebApplicationFactory factory)
    {
        _factory = factory;
        _auth = new AuthHelper(factory);
    }

    [Fact]
    public async Task PostBatch_Should_AcceptKnownProxies_And_RejectUnknown_UnderApiKeyAuth()
    {
        using var admin = await _auth.CreateRootAdminClientAsync();

        // 1. Issue an API key. The plaintext key is returned exactly once — here.
        var apiClientResponse = await admin.PostAsJsonAsync(
            $"{ProxiesBasePath}/api-clients",
            new { name = $"batch-feedback-test-{Guid.NewGuid():N}" });
        apiClientResponse.StatusCode.ShouldBe(HttpStatusCode.OK);
        var apiClient = await apiClientResponse.Content.ReadFromJsonAsync<CreateApiClientResult>();
        apiClient.ShouldNotBeNull();

        // 2. Create a real proxy to report against. Proxy.Create leaves it in Testing status, which is
        //    fine: the batch handler checks existence, not status.
        var proxyResponse = await admin.PostAsJsonAsync($"{ProxiesBasePath}/manual-proxies", new
        {
            host = "203.0.113.10",
            port = 8080,
            protocol = "Http",
            username = (string?)null,
            plaintextPassword = (string?)null,
            tagNames = Array.Empty<string>(),
        });
        proxyResponse.StatusCode.ShouldBe(HttpStatusCode.OK);
        var proxyId = await proxyResponse.Content.ReadFromJsonAsync<Guid>();

        // 3. A client carrying ONLY the API key — no bearer token.
        using var consumer = _factory.CreateClient();
        consumer.DefaultRequestHeaders.Add("X-Api-Key", apiClient.PlaintextKey);

        var unknownProxyId = Guid.NewGuid();
        var batchResponse = await consumer.PostAsJsonAsync($"{ProxiesBasePath}/feedback/batch", new
        {
            events = new[]
            {
                new { proxyId, outcome = "Banned", detail = (string?)"mercadopublico:robot-check" },
                new { proxyId, outcome = "Timeout", detail = (string?)null },
                new { proxyId, outcome = "Success", detail = (string?)null },
                new { proxyId = unknownProxyId, outcome = "Failure", detail = (string?)null },
            },
        });

        batchResponse.StatusCode.ShouldBe(HttpStatusCode.OK);
        var result = await batchResponse.Content.ReadFromJsonAsync<BatchFeedbackResult>();
        result.ShouldNotBeNull();
        result.Accepted.ShouldBe(3);
        result.Rejected.ShouldBe([unknownProxyId]);
    }

    [Fact]
    public async Task PostBatch_Should_Return401_When_ApiKeyIsMissing()
    {
        using var anonymous = _factory.CreateClient();

        var response = await anonymous.PostAsJsonAsync($"{ProxiesBasePath}/feedback/batch", new
        {
            events = new[]
            {
                new { proxyId = Guid.NewGuid(), outcome = "Banned", detail = (string?)null },
            },
        });

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }
}
```

**If the first test fails with a tenant-resolution error rather than an assertion failure, stop and
report it — that is a genuine finding, not a broken test.** `CreateRootAdminClientAsync` sends a
`tenant` header; the API-key client deliberately does not. If Finbuckle's middleware rejects the
request for that reason, then every scraper must send a tenant header alongside its API key, and that
requirement belongs in the spec (§7 Configuration) and in the SDK before phase 2 starts. Finding this
now, in a test, is much cheaper than finding it in a scraper.

- [ ] **Step 5: Run the integration test**

```bash
dotnet test src/Tests/Integration.Tests/Integration.Tests.csproj --filter "FullyQualifiedName~ProxyFeedbackBatchTests"
```

Expected: PASS. If Docker is unavailable the test cannot run — do not delete it or weaken it; note the skip in the PR description and have a reviewer with Docker confirm.

- [ ] **Step 6: Run the full backend suite**

```bash
dotnet test src/FS.Proxy.slnx
```

Expected: all green.

- [ ] **Step 7: Commit**

```bash
git add src/Modules/Proxies/Modules.Proxies/Features/v1/Proxies/ReportProxyFeedbackBatch/ReportProxyFeedbackBatchEndpoint.cs \
        src/Modules/Proxies/Modules.Proxies/ProxiesModule.cs \
        src/Tests/Integration.Tests/Tests/Proxies/ProxyFeedbackBatchTests.cs
git commit -m "feat(proxies): expose POST /api/v1/proxies/feedback/batch

Same consumer policy as /request and /{id}/feedback. The per-event
endpoint is unchanged, for consumers that do not buffer.

Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>"
```

- [ ] **Step 8: Document the endpoint**

Add the new endpoint to the Proxies module documentation in this repository (per the Global Constraints, docs live here, not in the upstream docs repo). Record: route, auth scheme, request and response shapes, the 200-event cap, partial-acceptance semantics, and the note that idempotency is deliberately not implemented yet.

```bash
git add -A docs/
git commit -m "docs: document the batched proxy feedback endpoint

Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>"
```

---

## Out of scope for this plan

Tracked in the spec, not implemented here:

- The client SDK itself (spec §1, §2, §3 client side, §5 `ProxyTags`, §8) — phases 2 through 5.
- Idempotency on the batch endpoint (spec §4.4).
- The non-atomic round-robin cursor in `RequestProxiesQueryHandler.ResolveRoundRobinAsync` (spec follow-ups).
- Slugifying `source` tag values (spec §5.4).
- Creating one API key per scraper — operational, and a prerequisite for phase 3, not phase 1.

# Proxy Tag Categories + Assignment UI — Design Spec

Status: Approved
Date: 2026-09-04
Author: Claude Sonnet 5 (with Eduardo Jimenez)
Related: `docs/superpowers/specs/2026-09-02-proxy-management-service-design.md` (parent spec — introduced free-form Tags), `docs/superpowers/specs/2026-09-03-provider-sync-brightdata-webshare-design.md` (sibling — the sync work that surfaced this gap: newly-synced proxies have no UI path to get tagged at all)

## Context

The Proxy Management Service shipped a fully free-form Tag system (`Tag`, `ProxyTagAssignment`) by deliberate design choice — tags drive policy assignment, health-check-target assignment, and list filtering, and the client's usage pattern (e.g. a US-geolocated proxy deliberately used for Peru traffic) argued against fixed dimensions. But two gaps emerged once real proxies were actually synced and used:

1. **No UI path to tag a provider-synced proxy at all.** Only the Manual Proxy dialog exposes a tags field (comma-separated free text, resolved via `CreateManualProxyCommandHandler.ResolveTagIdsAsync`); WebShare/BrightData-synced proxies have no create/update command an admin can reach, so they can never be tagged today, individually or in bulk.
2. **Free-text entry is slow and error-prone for the common case.** Almost every real tag follows a `category:value` convention (`pais:cl`, `funcionalidad:licitaciones`) that today exists only as a human convention, typed by hand each time.

This spec adds both a tagging UI for all proxies (individual + bulk) and an optional structured catalog (`TagCategory` + `TagCategoryValue`) so the common case is select-driven, while preserving the original free-form flexibility for anything that doesn't fit a category.

## Decisions carried from discussion

- **Hybrid, not category-only.** A proxy's tags can be assigned via category+value selects, free text, or a mix. No tag is ever *required* to belong to a category.
- **The catalog never touches `Tag`/`ProxyTagAssignment`.** `TagCategory`/`TagCategoryValue` is a separate, purely-advisory catalog. Picking Category=`pais` + Value=`cl` composes the exact string `"pais:cl"` and sends it through the same `Tag.Normalize` (trim + lowercase) path that free-typed tags already go through. This means zero changes to policy assignment, health-check-target assignment, the existing tag filter on the Proxies list, or the Manual Proxy dialog's own free-text tags field — they all keep operating on flat `Tag` rows exactly as today.
- **Bulk tag editing scope: add and remove** (not full replace — different selected proxies may have different existing tags, so bulk can only be additive/subtractive on one tag at a time).

## Data model

Two new entities in `Modules.Proxies`, following the same child-collection pattern already used by `Proxy`/`ProxyTagAssignment`:

```csharp
public sealed class TagCategory : AggregateRoot<Guid>, IGlobalEntity
{
    public string Name { get; private set; } = default!;              // normalized: trim + lowercase, e.g. "pais"
    private readonly List<TagCategoryValue> _values = [];
    public IReadOnlyCollection<TagCategoryValue> Values => _values;

    public static TagCategory Create(string name);
    public void Rename(string name);
    public void AddValue(string value);                                // normalized trim+lowercase; throws if already present (case-insensitive) in this category
    public void RemoveValue(string value);
}

// Plain child entity, not its own aggregate root — mirrors ProxyTagAssignment's shape exactly.
// Composite key (TagCategoryId, Value): values are never renamed, only added/removed, so no
// separate surrogate id is needed.
public sealed class TagCategoryValue : IGlobalEntity
{
    public Guid TagCategoryId { get; private set; }
    public string Value { get; private set; } = default!;             // normalized: trim + lowercase, e.g. "cl"
}
```

EF: two new tables, `TagCategories` and `TagCategoryValues` (composite PK/unique index on `(TagCategoryId, Value)`, FK `TagCategoryId` with cascade delete — deleting a category deletes its values; it does **not** touch any `Tag`/`ProxyTagAssignment` row already composed from it). One migration, additive only.

## Backend

New feature folder `Features/v1/TagCategories/`, mirroring the existing `Policies`/`HealthCheckTargets` CRUD slices file-for-file (`{Verb}{Entity}Command.cs` in Contracts, `{Verb}{Entity}/{Verb}{Entity}CommandHandler.cs` + `Validator.cs` + `Endpoint.cs` in the runtime project):

| Command/Query | Endpoint | Permission |
|---|---|---|
| `CreateTagCategoryCommand(string Name) : ICommand<Guid>` | `POST /tag-categories` | `Tags.Create` |
| `RenameTagCategoryCommand(Guid Id, string Name) : ICommand` | `PUT /tag-categories/{id}` | `Tags.Update` |
| `DeleteTagCategoryCommand(Guid Id) : ICommand` | `DELETE /tag-categories/{id}` | `Tags.Delete` |
| `AddTagCategoryValueCommand(Guid TagCategoryId, string Value) : ICommand` | `POST /tag-categories/{id}/values` | `Tags.Update` |
| `RemoveTagCategoryValueCommand(Guid TagCategoryId, string Value) : ICommand` | `DELETE /tag-categories/{id}/values/{value}` | `Tags.Update` |
| `ListTagCategoriesQuery : IQuery<IReadOnlyList<TagCategoryDto>>` | `GET /tag-categories` | `Tags.View` |

`TagCategoryDto(Guid Id, string Name, IReadOnlyList<string> Values)`.

All permission checks reuse the existing `ProxiesPermissions.Tags` resource (already defined, currently unused by any admin UI) — no new permission group.

New feature folder `Features/v1/Proxies/` additions (proxy-tag assignment, independent of the category catalog):

| Command | Endpoint | Permission |
|---|---|---|
| `SetProxyTagsCommand(Guid ProxyId, IReadOnlyList<string> TagNames) : ICommand` | `PUT /{id}/tags` | `Tags.Update` |
| `BulkAssignProxyTagCommand(IReadOnlyList<Guid> ProxyIds, string TagName) : ICommand<int>` | `POST /tags/assign` | `Tags.Update` |
| `BulkUnassignProxyTagCommand(IReadOnlyList<Guid> ProxyIds, string TagName) : ICommand<int>` | `POST /tags/unassign` | `Tags.Update` |

`SetProxyTagsCommandHandler` mirrors `UpdateManualProxyCommandHandler`'s existing tag-diff logic exactly (load the proxy with `.Include(x => x.TagAssignments)`, resolve `TagNames` via `CreateManualProxyCommandHandler.ResolveTagIdsAsync` — already `internal`, callable from this handler in the same assembly — unassign what's no longer present, assign what's new). `BulkAssignProxyTagCommandHandler` resolves/creates the single tag via the same helper, then calls `proxy.AssignTag(tagId)` for every matched proxy (idempotent — `AssignTag` already no-ops if already assigned) and returns the count touched. `BulkUnassignProxyTagCommandHandler` looks up the tag by normalized name (no-op / returns 0 if it doesn't exist) and calls `UnassignTag` for every matched proxy.

## Frontend

**New page**: `clients/admin/src/pages/proxies/tag-categories.tsx` (+ nav item + route + `TagCategoriesPermissions`-style gate reusing `ProxiesPermissions.Tags`), following the existing list+dialog CRUD pattern (`provider-accounts.tsx` is the closest reference). List of categories; each row expands to show its values with inline add/remove; category rename/delete actions.

**New API client**: `clients/admin/src/api/tag-categories.ts` (`listTagCategories`, `createTagCategory`, `renameTagCategory`, `deleteTagCategory`, `addTagCategoryValue`, `removeTagCategoryValue`).

**Individual proxy tag editor** — new `clients/admin/src/components/proxies/proxy-tags-dialog.tsx`, opened via a new "Tags" button per row on the Proxies list:
- Fetches the category catalog (`listTagCategories`) and the proxy's current `tags: string[]` (already on `ProxyDto`).
- Renders one `<Select>` per category, each pre-selected to the value `v` in `category.values` for which `${category.name}:${v}` matches one of the proxy's current tags, if any (else "— none —").
- A separate free-text input holds whatever current tags don't match any category's `{name}:{value}` pattern — editable as comma-separated text, same convention as the Manual Proxy dialog's tags field.
- On submit: compose the selected category values into `{category}:{value}` strings, combine with the parsed free-text tags, call `setProxyTags(proxyId, allTagNames)` (full replace, matching the command's semantics).

**Bulk tag editor** — new `clients/admin/src/components/proxies/bulk-tag-dialog.tsx`, opened via a "Manage tags" button that appears next to "Enable selected"/"Disable selected" once ≥1 proxy is checked:
- Two sections, "Add tag" and "Remove tag", each offering a Category→Value select pair *or* a "custom tag" toggle that swaps in a free-text input.
- "Add tag" calls `bulkAssignProxyTag(selectedIds, tagName)`; "Remove tag" calls `bulkUnassignProxyTag(selectedIds, tagName)`. Both report the affected count via toast, matching the existing enable/disable bulk actions' UX.

No changes to `manual-proxy-dialog.tsx` — its existing free-text tags field keeps working unmodified; a manual proxy can also be tagged through the new individual editor since both paths write identical `Tag` rows.

## Testing

- Domain: `TagCategory.Create`/`Rename`/`AddValue` (including duplicate-value rejection)/`RemoveValue`.
- Handlers: each of the 6 `TagCategories` CRUD handlers; `SetProxyTagsCommandHandler` (create+update+remove-all paths, matching `UpdateManualProxyCommandHandler`'s existing test shape); `BulkAssignProxyTagCommandHandler`/`BulkUnassignProxyTagCommandHandler` (multi-proxy, idempotency, unknown-tag-name-on-unassign returns 0).
- Frontend: Playwright coverage for the new Tag Categories page (create category, add/remove value, delete category) and for both the individual and bulk proxy-tag dialogs (category-select composes the right string; free-text path still works; bulk add/remove call the right endpoints with the right selected IDs).

## Non-Goals

- No reverse-migration of existing free-text tags into categories — a tag typed before this feature shipped simply shows up in the individual editor's free-text field until an admin chooses to re-express it via a category (or not).
- No enforcement that a `TagCategoryValue` deletion also removes matching `Tag`/`ProxyTagAssignment` rows already in use — the catalog is advisory only, by design (see Decisions above); already-assigned tags are unaffected by catalog changes.
- No seed data — categories and values start empty; the admin populates them via the new CRUD page.

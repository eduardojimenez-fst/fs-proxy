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

- A JWT for a user holding `ProxiesPermissions.Tags.View`, `ProxiesPermissions.Tags.Update`, and
  `ProxiesPermissions.ProviderAccounts.View` (Steps 1 and 4 call `GET /tag-categories`, which
  requires `Tags.View`; Step 3's `POST`/`DELETE` require `Tags.Update`; Step 2's `GET /proxies`
  requires `ProviderAccounts.View`).
- `BASE` set to the environment root: QA is
  `export BASE=https://proxy-api-qa.falcontenders.com`, Production is
  `export BASE=https://proxy-api.falcontenders.com`.
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

The two `DELETE`s are safe to re-run (`TagCategory.RemoveValue` uses `RemoveAll` and is a no-op on
a value that is already gone). The `POST` is **not** safely re-runnable — `TagCategory.AddValue`
throws `InvalidOperationException` on a duplicate value. If it already returned `204` once, skip it
on any re-run of this step.

```bash
curl -s -X DELETE -H "Authorization: Bearer $TOKEN" -w '%{http_code}\n' \
  "$BASE/api/v1/proxies/tag-categories/$CAT/values/Attachments"

curl -s -X DELETE -H "Authorization: Bearer $TOKEN" -w '%{http_code}\n' \
  "$BASE/api/v1/proxies/tag-categories/$CAT/values/QuoteAgreementHardwareStorare"

curl -s -X POST -H "Authorization: Bearer $TOKEN" -H "Content-Type: application/json" \
  -w '%{http_code}\n' \
  -d '{"value":"QuoteAgreementHardwareStorage"}' \
  "$BASE/api/v1/proxies/tag-categories/$CAT/values"
```

Each prints `204` (the `-w` status code appended after the empty body).

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

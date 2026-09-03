# Provider Sync: BrightData + WebShare — Design Spec

Status: Approved
Date: 2026-09-03
Author: Claude Sonnet 5 (with Eduardo Jimenez)
Related: `docs/superpowers/specs/2026-09-02-proxy-management-service-design.md` (parent spec — the Proxy Management Service module this extends)

## Context

The Proxy Management Service module (delivered 2026-09-02) shipped `IProxyProviderAdapter` implementations for WebShare, Oxylabs, BrightData, and Manual, plus an hourly sync job and a manual sync-now endpoint — but the WebShare and BrightData adapters were built without validating against real provider responses. This spec closes that gap: it documents real, read-only API investigation against both providers' production accounts (WebShare and BrightData each supplied a live, read-only API key for this purpose) and defines the corrected data model and adapter behavior.

BrightData's data shape turned out to be fundamentally different from what was assumed, which is why this needed its own design pass rather than a direct bugfix.

## Research findings (empirical, read-only, against real production accounts)

### WebShare

`GET https://proxy.webshare.io/api/v2/proxy/list/?mode=direct&page=1&page_size=25` returns individually-addressable proxies:

```json
{
  "count": 100, "next": "...page=2...", "previous": null,
  "results": [{
    "id": "d-17151685319", "username": "jgwcycpg", "password": "ytz1gdtc8ymc",
    "proxy_address": "64.137.37.190", "port": 6780, "valid": true,
    "country_code": "CL", "city_name": "Santiago", "asn_name": "Latitude.Sh",
    "asn_number": 396356, "high_country_confidence": true
  }]
}
```

Confirmed facts:
- Each result is a real, directly-dialable proxy: its own `proxy_address`/`port`.
- `username`/`password` are the same across every result in the account (an account/plan-level credential, not per-IP) — confirmed against `GET /proxy/config/`, which returns the identical pair.
- `country_code` (ISO2) is present per-record and currently discarded by the adapter — this is the "provider geolocation" the common model needs.
- Pagination is real: this account has exactly 100 proxies (fits current hardcoded `page_size=100` in one call by coincidence), but the adapter doesn't follow `next` — the moment an account exceeds 100, proxies silently go unsynced. This is a real, pre-existing bug (already flagged as a residual finding in the parent module's final review) and is fixed as part of this work since we're already in this file.
- The account's active plan is "Proxy List" only; "Static Residential" and "Rotating Residential" are inactive on this account and their response shapes are **not verified** — out of scope here (see Non-Goals).

### BrightData

BrightData organizes proxies into **zones** — a fundamentally different unit than WebShare's flat per-IP list. Findings, in the order that shaped the design:

1. `GET /status` → account health only (`{"status":"active","customer":"importfalcon",...}`). `customer` here is a **display name**, not usable in proxy usernames (confirmed by the account owner).
2. `GET /zone/get_active_zones` → `[{"name":"zone1new","type":"dc"}, {"name":"residential_proxy_cl","type":"res_rotating"}, ...]` — the list of purchased zone "products."
3. `GET /zone?zone={name}` → full zone config, critically including the **zone password** (one password shared by every IP in that zone) and `plan.{product,type,country,default_country,ips_type,ips}`. The `ips` array in this response is **not reliable** for enumerating real IPs (a multi-country zone returned the literal sentinel `["any"]` here despite genuinely having 20 real, enumerable IPs — see point 5).
4. `GET /zone/route_ips?zone={name}` → bare newline-list of IPs, **only for zones where the plan is a static/enumerable type**; returns HTTP 403 ("Static routes not found") for `res_rotating` zones. No per-IP country.
5. `GET /zone/ips?zone={name}` → `{"ips":[{"ip":"...","maxmind":"cc","ext":{}}]}` — a **strict superset of `route_ips`**: same IPs, plus real per-IP country (via MaxMind geolocation). Verified to return HTTP 400 ("Wrong zone plan") for the same `res_rotating` zone that `route_ips` 403'd on, and to succeed (200) even for the multi-country zone whose `/zone?zone=X` response misleadingly showed `ips:["any"]`. **This makes `route_ips` redundant — `/zone/ips` is the only per-IP endpoint the adapter needs.**
6. **Ground truth validation**: the account owner created a fresh zone (`datacenter_new_proxy_manager`, a dedicated multi-country static zone) and exported its proxy list from the BrightData UI:
   ```
   brd.superproxy.io:44445:brd-customer-hl_c775be64-zone-datacenter_new_proxy_manager-ip-5.182.126.105:kayals23zy79
   ```
   `GET /zone/ips?zone=datacenter_new_proxy_manager` returned the exact same 20 IPs, same order, and `GET /zone?zone=datacenter_new_proxy_manager` returned the matching password (`kayals23zy79`). This confirms:
   - Connection host is **always** the shared gateway `brd.superproxy.io`, never an individual IP directly.
   - Port (`44445` in this account) is **account-wide** — confirmed by the account owner ("todos [las zonas] tienen el mismo [puerto]") — but is BrightData-account-specific configuration, not a public constant, and isn't exposed by any read-only endpoint tried (`/customer`, `/user`, `/account` all 404). Must be admin-supplied.
   - The username's customer segment (`hl_c775be64`) is **not** `/status`'s `customer` field (confirmed by the account owner: `"importfalcon"` is a commercial display name, unused in connection strings) and is not exposed by any endpoint tried. Must be admin-supplied.
   - A specific IP is pinned via a `-ip-{ip}` username suffix while still dialing the same shared gateway host:port.

No mutating BrightData or WebShare endpoint was called at any point; only `GET` requests were made, per the account owner's explicit read-only constraint.

## Common data model

Two new fields, added to `ProviderProxyRecord` (`src/Modules/Proxies/Modules.Proxies/Providers/ProviderProxyRecord.cs`) and to the `Proxy` domain entity (`src/Modules/Proxies/Modules.Proxies/Domain/Proxy.cs`, requiring an EF migration):

- **`Country`** (`string?`, ISO2) — the provider's *reported geolocation* of the exit IP. Sync-only, not user-editable. Explicitly **not** the same concept as the existing free-form Tag system's usage: an operator may deliberately tag a US-geolocated proxy for Peru traffic if it performs better there. This field is informational/filterable only and never drives policy or auto-tagging.
- **`ProviderGrouping`** (`string?`) — the provider's own product/category label: the BrightData zone name, or `"Proxy List"` for WebShare. Informational/filterable only, independent of the Tag system.

Propagates to: `ProxyDto`, `ListProxiesQueryHandler`, and the admin `Proxies` list page/table (two new columns/filters). Oxylabs and Manual adapters pass `null` for both (no real Oxylabs account was available to validate against; see Non-Goals).

## BrightData adapter algorithm

`BrightDataCredentials` grows from `{ApiToken, Zone}` to:

```csharp
public sealed record BrightDataCredentials(
    string ApiToken, string Zone, string CustomerId,
    int GatewayPort, string GatewayHost = "brd.superproxy.io");
```

`CustomerId` and `GatewayPort` are admin-supplied at `ProviderAccount` creation (pasted as part of the same JSON blob the admin already pastes into the existing credentials textarea — see UI note below); `GatewayHost` defaults to the confirmed public gateway but stays overridable.

Sync flow per `ProviderAccount` (still **one BrightData zone per `ProviderAccount`** — no auto-discovery of every zone on the API token; the account has 11 active zones today, several of which are ad-hoc test zones the account owner does not want pulled into the pool automatically):

1. `GET /zone?zone={Zone}` — get the zone password and plan metadata. Non-200 → `ProviderSyncResult.Failed(...)`.
2. `GET /zone/ips?zone={Zone}`:
   - **200** → one `ProviderProxyRecord` per returned IP:
     - `ExternalId`: `"{zone}:{ip}"`
     - `Host`: `credentials.GatewayHost`
     - `Port`: `credentials.GatewayPort`
     - `Username`: `$"brd-customer-{credentials.CustomerId}-zone-{credentials.Zone}-ip-{ip}"`
     - `Password`: the zone password from step 1
     - `Country`: the per-IP `maxmind` value from this response
     - `ProviderGrouping`: `credentials.Zone`
   - **400 ("Wrong zone plan")** → the zone has no enumerable IP roster (rotating): a single `ProviderProxyRecord` representing the whole zone:
     - `ExternalId`: `"{zone}:pool"`
     - `Host`/`Port`: same as above
     - `Username`: `$"brd-customer-{credentials.CustomerId}-zone-{credentials.Zone}"` (no `-ip-` suffix — BrightData rotates internally)
     - `Password`: the zone password from step 1
     - `Country`: `plan.default_country` (falling back to `plan.country` if `default_country` is absent) from step 1's response, only when it is a single value (no embedded space); otherwise `null`
     - `ProviderGrouping`: `credentials.Zone`
   - Any other non-success status → `ProviderSyncResult.Failed(...)`.

`route_ips` is dropped entirely from the adapter — `/zone/ips` is a strict superset (same IPs plus country) and the only per-IP call needed.

`RenewProxyAsync` stays `Unsupported()`, unchanged. The account owner raised the possibility that BrightData documents a per-zone or per-IP renewal call, but this was never confirmed and confirming it would require a mutating request against a production account — explicitly out of scope for this read-only investigation. Revisit separately if/when that's confirmed through non-destructive means (BrightData support, a sandbox account, or documentation the account owner can point to directly).

## WebShare adapter changes

Two changes to the existing, already-correct-in-shape adapter:

1. **Pagination**: follow `next` across pages instead of relying on a single `page_size=100` call, so accounts with more than 100 proxies sync completely.
2. **Field mapping**: populate `Country` from `country_code` and `ProviderGrouping` with the literal `"Proxy List"` (the only plan type validated — see Non-Goals).

## Admin UI touch

The `ProviderAccount` credentials input remains the existing single JSON textarea in `provider-account-dialog.tsx` (no per-field form redesign — out of scope, confirmed with the account owner). The one required change: the textarea's placeholder is currently a hardcoded `{"apiKey":"..."}` regardless of the selected `providerType`, which would now actively mislead an admin configuring BrightData (5 keys, different names). Swap the placeholder to reflect the selected `providerType`:

- WebShare: `{"apiKey":"..."}`
- BrightData: `{"apiToken":"...","zone":"...","customerId":"...","gatewayPort":44445}` (omit `gatewayHost` from the example since it defaults)
- Oxylabs: unchanged from today

The `Proxies` list page gains `Country` and `ProviderGrouping` as displayed columns (and reasonable filter support, matching the existing filter patterns on that page).

## Testing

Adapter tests use the real captured JSON shapes from this investigation as fixtures rather than invented ones:
- BrightData: a static/enumerable zone response (from `/zone/ips`, 200), a rotating zone response (400 "Wrong zone plan"), and the `/zone?zone=X` config response, covering both branches of the algorithm plus the zone-lookup failure path.
- WebShare: a multi-page `/proxy/list/` fixture pair (to exercise the new pagination loop) with `country_code` populated.

Existing malformed-credentials-JSON handling (`JsonException` → `ProviderSyncResult.Failed`) carries over unchanged for both adapters and gets a fixture reflecting the new BrightData credentials shape.

## Non-Goals (explicitly out of scope for this iteration)

- **Oxylabs**: receives the two new `ProviderProxyRecord` fields as `null` so it keeps compiling; its own endpoint/shape is not re-validated (no real Oxylabs account was available).
- **WebShare Static Residential / Rotating Residential**: inactive on the investigated account; their response shape is unconfirmed and not modeled. Revisit against the account owner's other WebShare account, which has them active.
- **BrightData renewal**: `RenewProxyAsync` stays unsupported; no renewal endpoint was confirmed (and confirming one would require a mutating call, which this investigation deliberately avoided).
- **BrightData multi-zone auto-discovery**: a `ProviderAccount` still maps to exactly one zone; `GET /zone/get_active_zones` is not wired into any auto-provisioning flow. (It could later back a "pick a zone" dropdown in the account-creation UI — a nice-to-have, not required now.)
- **Admin UI credentials form redesign**: stays a single raw-JSON textarea; only its placeholder text becomes provider-aware.

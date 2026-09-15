# Module: Proxies

Manages a pool of forward proxies used by scraper clients: manual + provider-synced proxies
(WebShare/BrightData/Oxylabs), a tag/tag-category catalog, auto-disable policy profiles, active
health checks, and dual-auth (API key + JWT) consumer endpoints for leasing a proxy and reporting
usage feedback. Module `Order = 650`.

**Entities / DbContext:** `Proxy`, `ApiClient`, `ProxyUsageEvent`, `Tag` / `TagCategory`,
`PolicyProfile`, `ProviderAccount`, `HealthCheckTarget`. `ProxiesDbContext` (tenant-filtered).

## Consumer-facing feedback endpoints

Both accept **either** authentication scheme through the `ApiKeyAuthenticationDefaults.ConsumerPolicyName`
(`"ProxiesConsumerAccess"`) policy, enforced by `ProxiesConsumerAuthorizationHandler`:

- `X-Api-Key: <key>` (`ApiKeyAuthenticationDefaults.HeaderName`) — an API-key principal. Possession of a
  valid, enabled key issued via `POST /api-clients` *is* the authorization; no extra permission check runs.
- A JWT bearer token from an authenticated user holding `ProxiesPermissions.Consumers.Request`.

### `POST /api/v1/proxies/{id:guid}/feedback` — single event

Reports one outcome for one proxy. Body: `{ "outcome": "Success|Failure|Banned|Timeout", "detail": "<string?>" }`.
Returns `204 No Content`. See `ReportProxyFeedbackEndpoint`.

### `POST /api/v1/proxies/feedback/batch` — batched events

Lets a scraper flush many reported outcomes in a single call instead of one HTTP round trip per
event. Endpoint name `"ReportProxyFeedbackBatch"` (`ReportProxyFeedbackBatchEndpoint`).

- **Auth:** same `ProxiesConsumerAccess` policy as the single-event endpoint above — `X-Api-Key` or a
  JWT carrying `Proxies.Consumers.Request`.
- **Request body:**

  ```json
  {
    "events": [
      { "proxyId": "<guid>", "outcome": "Banned", "detail": "mercadopublico:robot-check" },
      { "proxyId": "<guid>", "outcome": "Success", "detail": null }
    ]
  }
  ```

  `outcome` is one of `UsageEventOutcome`: `Success`, `Failure`, `Banned`, `Timeout` (serialized as
  strings — `Program.cs` registers `JsonStringEnumConverter`). `detail` is optional, max 2048 chars.

- **200-event cap:** `events` must contain 1–200 items (`ReportProxyFeedbackBatchCommandValidator.MaxBatchSize`).
  A null, empty, or oversized `events` fails validation with `400`, not an exception — a JSON body can
  deserialize a missing `events` property to `null` despite the non-nullable annotation, and the
  validator is written to handle that rather than dereference it.
- **Response `200 OK`:** `BatchFeedbackResult { accepted: int, rejected: Guid[] }`.
- **Partial-acceptance semantics:** an event whose `proxyId` no longer exists is *rejected, not thrown
  on* — a proxy can be retired between the moment a scraper leases it and the moment its buffered event
  is flushed, and failing the whole batch for that would make the client retry a submission that can
  never succeed. Unknown ids are deduplicated into `rejected`; every event for a still-existing proxy in
  the same batch is still persisted (`accepted` counts events, not distinct proxies).
- **Policy evaluation:** `IPolicyEvaluationService.EvaluateAsync` runs once per **distinct** accepted
  proxy that carries at least one non-`Success` outcome in the batch — not once per event — since a
  proxy whose batch held nothing but successes cannot change any auto-disable decision.
- **Idempotency is deliberately not implemented.** Retrying a batch (e.g. after a client-side timeout on
  a response that actually succeeded) re-inserts every event a second time; there is no dedup key or
  idempotency-key header on this endpoint. Tracked as spec follow-up, not handled here.
- **Route safety:** `/feedback/batch` cannot collide with the sibling `/{id:guid}/feedback` route — the
  `:guid` constraint rejects the literal segment `feedback`.

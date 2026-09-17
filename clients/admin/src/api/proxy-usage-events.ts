import { apiFetch } from "@/lib/api-client";
import type { PagedResponse } from "@/lib/api-types";

const BASE = "/api/v1/proxies/usage-events";

/** Who produced the event: the periodic active probe, or a consumer via the feedback endpoints. */
export type UsageEventSource = "SystemHealthCheck" | "ConsumerFeedback";
export type UsageEventOutcome = "Success" | "Failure" | "Banned" | "Timeout";

export type ProxyUsageEventDto = {
  id: string;
  proxyId: string;
  proxyHost: string;
  proxyPort: number;
  /** Auth username of the proxy. Providers often put its egress IP here, so it identifies the proxy. */
  proxyUsername: string | null;
  source: UsageEventSource;
  outcome: UsageEventOutcome;
  reportedByApiClientId: string | null;
  /** Null for system health checks, and for feedback whose reporting API client was deleted. */
  reportedByApiClientName: string | null;
  healthCheckTargetId: string | null;
  detail: string | null;
  occurredAtUtc: string;
};

export type ListProxyUsageEventsParams = {
  proxyId?: string;
  outcome?: UsageEventOutcome;
  source?: UsageEventSource;
  /** Selects exactly what the policy engine counts — every outcome except Success. */
  failuresOnly?: boolean;
  fromUtc?: string;
  toUtc?: string;
  pageNumber?: number;
  pageSize?: number;
};

export async function listProxyUsageEvents(
  params: ListProxyUsageEventsParams = {},
): Promise<PagedResponse<ProxyUsageEventDto>> {
  const query = new URLSearchParams();
  query.set("pageNumber", String(params.pageNumber ?? 1));
  query.set("pageSize", String(params.pageSize ?? 20));
  if (params.proxyId) query.set("proxyId", params.proxyId);
  if (params.outcome) query.set("outcome", params.outcome);
  if (params.source) query.set("source", params.source);
  if (params.failuresOnly) query.set("failuresOnly", "true");
  if (params.fromUtc) query.set("fromUtc", params.fromUtc);
  if (params.toUtc) query.set("toUtc", params.toUtc);
  return apiFetch<PagedResponse<ProxyUsageEventDto>>(`${BASE}?${query.toString()}`);
}

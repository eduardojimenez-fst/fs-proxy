import { apiFetch } from "@/lib/api-client";
import type { PagedResponse } from "@/lib/api-types";
import type { ProxyProviderType } from "./provider-accounts";

const BASE = "/api/v1/proxies";

export type ProxyProtocol = "Http" | "Https" | "Socks5";
export type ProxyStatus = "Active" | "Disabled" | "Banned" | "Testing" | "Retired";

export type ProxyDto = {
  id: string;
  host: string;
  port: number;
  protocol: ProxyProtocol;
  status: ProxyStatus;
  providerAccountId: string;
  providerAccountName: string;
  providerType: ProxyProviderType;
  tags: string[];
  createdAtUtc: string;
  lastRenewedAtUtc: string | null;
};

export type ListProxiesParams = {
  tags?: string[];
  status?: ProxyStatus;
  providerAccountId?: string;
  pageNumber?: number;
  pageSize?: number;
};

export async function listProxies(params: ListProxiesParams = {}): Promise<PagedResponse<ProxyDto>> {
  const query = new URLSearchParams();
  query.set("pageNumber", String(params.pageNumber ?? 1));
  query.set("pageSize", String(params.pageSize ?? 20));
  if (params.status) query.set("status", params.status);
  if (params.providerAccountId) query.set("providerAccountId", params.providerAccountId);
  for (const tag of params.tags ?? []) query.append("tags", tag);
  return apiFetch<PagedResponse<ProxyDto>>(`${BASE}/?${query.toString()}`);
}

export type SetProxiesStatusInput = { proxyIds?: string[]; tagId?: string };

/**
 * Both endpoints return the number of proxies affected (SetProxiesStatusCommand
 * is `ICommand<int>` server-side, returned as the raw response body — not 204).
 */
export async function enableProxies(input: SetProxiesStatusInput): Promise<number> {
  return apiFetch<number>(`${BASE}/enable`, {
    method: "POST",
    body: JSON.stringify({ proxyIds: input.proxyIds ?? null, tagId: input.tagId ?? null }),
  });
}

export async function disableProxies(input: SetProxiesStatusInput): Promise<number> {
  return apiFetch<number>(`${BASE}/disable`, {
    method: "POST",
    body: JSON.stringify({ proxyIds: input.proxyIds ?? null, tagId: input.tagId ?? null }),
  });
}

/** Convenience wrapper matching the brief's `setProxiesStatus` interface name. */
export async function setProxiesStatus(input: SetProxiesStatusInput, status: "Active" | "Disabled"): Promise<number> {
  return status === "Active" ? enableProxies(input) : disableProxies(input);
}

export async function setProxyTags(proxyId: string, tagNames: string[]): Promise<void> {
  await apiFetch<void>(`${BASE}/${proxyId}/tags`, { method: "PUT", body: JSON.stringify({ tagNames }) });
}

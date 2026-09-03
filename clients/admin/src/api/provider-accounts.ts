import { apiFetch } from "@/lib/api-client";
import type { PagedResponse } from "@/lib/api-types";

const BASE = "/api/v1/proxies/provider-accounts";

export type ProxyProviderType = "WebShare" | "Oxylabs" | "BrightData" | "Manual";

export type ProviderAccountDto = {
  id: string;
  name: string;
  providerType: ProxyProviderType;
  isEnabled: boolean;
  lastSyncedAtUtc: string | null;
  lastSyncStatus: string | null;
  consecutiveSyncFailures: number;
};

export async function listProviderAccounts(pageNumber = 1, pageSize = 20): Promise<PagedResponse<ProviderAccountDto>> {
  const query = new URLSearchParams({ pageNumber: String(pageNumber), pageSize: String(pageSize) });
  return apiFetch<PagedResponse<ProviderAccountDto>>(`${BASE}?${query.toString()}`);
}

export type CreateProviderAccountInput = { name: string; providerType: ProxyProviderType; plaintextCredentials: string };

export async function createProviderAccount(input: CreateProviderAccountInput): Promise<string> {
  return apiFetch<string>(`${BASE}`, { method: "POST", body: JSON.stringify(input) });
}

export type UpdateProviderAccountInput = { id: string; name: string; plaintextCredentials?: string; isEnabled: boolean };

export async function updateProviderAccount(input: UpdateProviderAccountInput): Promise<void> {
  await apiFetch<void>(`${BASE}/${input.id}`, {
    method: "PUT",
    body: JSON.stringify({ name: input.name, plaintextCredentials: input.plaintextCredentials ?? null, isEnabled: input.isEnabled }),
  });
}

export async function deleteProviderAccount(id: string): Promise<void> {
  await apiFetch<void>(`${BASE}/${id}`, { method: "DELETE" });
}

/** Triggers an immediate sync; returns the number of proxies synced. */
export async function syncProviderAccountNow(id: string): Promise<number> {
  return apiFetch<number>(`${BASE}/${id}/sync`, { method: "POST" });
}

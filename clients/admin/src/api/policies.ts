import { apiFetch } from "@/lib/api-client";

const BASE = "/api/v1/proxies";

/**
 * `Manual` records events but never acts — the proxy is only ever disabled by an operator.
 * The other two auto-disable; `AutoDisableAndRenew` additionally asks the provider for a
 * replacement. When a proxy's tags resolve to several profiles, the most restrictive wins
 * (AutoDisableAndRenew > AutoDisable > Manual).
 */
export type PolicyProfileType = "Manual" | "AutoDisable" | "AutoDisableAndRenew";

export type PolicyProfileDto = {
  id: string;
  name: string;
  type: PolicyProfileType;
  failureThreshold: number;
  windowMinutes: number;
  minDistinctReporters: number;
};

export type PolicyProfileInput = {
  name: string;
  type: PolicyProfileType;
  failureThreshold: number;
  windowMinutes: number;
  minDistinctReporters: number;
};

export async function listPolicyProfiles(): Promise<PolicyProfileDto[]> {
  return apiFetch<PolicyProfileDto[]>(`${BASE}/policies`);
}

export async function createPolicyProfile(input: PolicyProfileInput): Promise<string> {
  return apiFetch<string>(`${BASE}/policies`, { method: "POST", body: JSON.stringify(input) });
}

export async function updatePolicyProfile(id: string, input: PolicyProfileInput): Promise<void> {
  await apiFetch<void>(`${BASE}/policies/${id}`, { method: "PUT", body: JSON.stringify(input) });
}

export async function deletePolicyProfile(id: string): Promise<void> {
  await apiFetch<void>(`${BASE}/policies/${id}`, { method: "DELETE" });
}

/** At most one policy per tag — assigning replaces whatever the tag had. */
export async function assignPolicyToTag(tagId: string, policyProfileId: string): Promise<void> {
  await apiFetch<void>(`${BASE}/tags/${tagId}/policy/${policyProfileId}`, { method: "POST" });
}

export async function unassignPolicyFromTag(tagId: string): Promise<void> {
  await apiFetch<void>(`${BASE}/tags/${tagId}/policy`, { method: "DELETE" });
}

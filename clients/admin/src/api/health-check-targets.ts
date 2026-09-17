import { apiFetch } from "@/lib/api-client";

const BASE = "/api/v1/proxies";

export type HealthCheckTargetDto = {
  id: string;
  name: string;
  testUrl: string;
  /** Null means "any 2xx–3xx counts as success". */
  expectedStatusCode: number | null;
  /** Null means the body is not inspected. */
  expectedBodyKeyword: string | null;
  timeoutMs: number;
};

export type HealthCheckTargetInput = {
  name: string;
  testUrl: string;
  expectedStatusCode?: number | null;
  expectedBodyKeyword?: string | null;
  timeoutMs: number;
};

export async function listHealthCheckTargets(): Promise<HealthCheckTargetDto[]> {
  return apiFetch<HealthCheckTargetDto[]>(`${BASE}/health-check-targets`);
}

export async function createHealthCheckTarget(input: HealthCheckTargetInput): Promise<string> {
  return apiFetch<string>(`${BASE}/health-check-targets`, { method: "POST", body: JSON.stringify(input) });
}

export async function updateHealthCheckTarget(id: string, input: HealthCheckTargetInput): Promise<void> {
  await apiFetch<void>(`${BASE}/health-check-targets/${id}`, { method: "PUT", body: JSON.stringify(input) });
}

export async function deleteHealthCheckTarget(id: string): Promise<void> {
  await apiFetch<void>(`${BASE}/health-check-targets/${id}`, { method: "DELETE" });
}

/** At most one target per tag — assigning replaces whatever the tag had. */
export async function assignHealthCheckTargetToTag(tagId: string, healthCheckTargetId: string): Promise<void> {
  await apiFetch<void>(`${BASE}/tags/${tagId}/health-check-target/${healthCheckTargetId}`, { method: "POST" });
}

export async function unassignHealthCheckTargetFromTag(tagId: string): Promise<void> {
  await apiFetch<void>(`${BASE}/tags/${tagId}/health-check-target`, { method: "DELETE" });
}

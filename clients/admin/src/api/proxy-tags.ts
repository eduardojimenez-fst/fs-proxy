import { apiFetch } from "@/lib/api-client";

const BASE = "/api/v1/proxies/tags";

export type TagDto = {
  id: string;
  name: string;
  policyProfileId: string | null;
  policyProfileName: string | null;
  healthCheckTargetId: string | null;
  healthCheckTargetName: string | null;
};

export async function listProxyTags(): Promise<TagDto[]> {
  return apiFetch<TagDto[]>(`${BASE}`);
}

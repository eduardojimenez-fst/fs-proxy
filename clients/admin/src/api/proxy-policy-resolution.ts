import { apiFetch } from "@/lib/api-client";
import type { PolicyProfileDto } from "./policies";

const BASE = "/api/v1/proxies";

export type ResolvedHealthCheckTargetDto = {
  /** Null for the configured fallback target — no tag of this proxy assigns one. */
  id: string | null;
  name: string;
  testUrl: string;
  expectedStatusCode: number | null;
  expectedBodyKeyword: string | null;
  timeoutMs: number;
  fromTag: string | null;
};

export type PolicyCandidateDto = {
  tagId: string;
  tagName: string;
  profile: PolicyProfileDto;
  isWinner: boolean;
};

export type ProxyPolicyResolutionDto = {
  proxyId: string;
  tags: string[];
  /** Null means nothing will ever auto-disable this proxy, however many failures it collects. */
  policy: PolicyProfileDto | null;
  policyFromTag: string | null;
  candidates: PolicyCandidateDto[];
  failuresInWindow: number | null;
  distinctReportersInWindow: number | null;
  windowStartUtc: string | null;
  healthCheckTargets: ResolvedHealthCheckTargetDto[];
  usingDefaultHealthCheckTarget: boolean;
};

/** Explains the auto-disable decision for one proxy, computed by the same resolver the engine uses. */
export async function getProxyPolicyResolution(proxyId: string): Promise<ProxyPolicyResolutionDto> {
  return apiFetch<ProxyPolicyResolutionDto>(`${BASE}/${proxyId}/policy-resolution`);
}

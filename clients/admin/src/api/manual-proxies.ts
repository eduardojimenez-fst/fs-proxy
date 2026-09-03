import { apiFetch } from "@/lib/api-client";
import type { ProxyProtocol } from "./proxies";

const BASE = "/api/v1/proxies/manual-proxies";

export type CreateManualProxyInput = {
  host: string;
  port: number;
  protocol: ProxyProtocol;
  username?: string;
  plaintextPassword?: string;
  tagNames: string[];
};

export async function createManualProxy(input: CreateManualProxyInput): Promise<string> {
  return apiFetch<string>(`${BASE}`, { method: "POST", body: JSON.stringify(input) });
}

export type UpdateManualProxyInput = CreateManualProxyInput & { id: string };

export async function updateManualProxy(input: UpdateManualProxyInput): Promise<void> {
  const { id, ...body } = input;
  await apiFetch<void>(`${BASE}/${id}`, { method: "PUT", body: JSON.stringify(body) });
}

export async function deleteManualProxy(id: string): Promise<void> {
  await apiFetch<void>(`${BASE}/${id}`, { method: "DELETE" });
}

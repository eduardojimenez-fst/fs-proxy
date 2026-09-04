import { apiFetch } from "@/lib/api-client";

const BASE = "/api/v1/proxies/tag-categories";

export type TagCategoryDto = {
  id: string;
  name: string;
  values: string[];
};

export async function listTagCategories(): Promise<TagCategoryDto[]> {
  return apiFetch<TagCategoryDto[]>(BASE);
}

export async function createTagCategory(name: string): Promise<string> {
  return apiFetch<string>(BASE, { method: "POST", body: JSON.stringify({ name }) });
}

export async function renameTagCategory(id: string, name: string): Promise<void> {
  await apiFetch<void>(`${BASE}/${id}`, { method: "PUT", body: JSON.stringify({ name }) });
}

export async function deleteTagCategory(id: string): Promise<void> {
  await apiFetch<void>(`${BASE}/${id}`, { method: "DELETE" });
}

export async function addTagCategoryValue(categoryId: string, value: string): Promise<void> {
  await apiFetch<void>(`${BASE}/${categoryId}/values`, { method: "POST", body: JSON.stringify({ value }) });
}

export async function removeTagCategoryValue(categoryId: string, value: string): Promise<void> {
  await apiFetch<void>(`${BASE}/${categoryId}/values/${encodeURIComponent(value)}`, { method: "DELETE" });
}

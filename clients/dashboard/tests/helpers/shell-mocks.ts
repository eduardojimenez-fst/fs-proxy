import type { Page, Route } from "@playwright/test";
import { mockJsonResponse } from "./api-mocks";

/**
 * A profile body that satisfies the topbar avatar + settings/profile read.
 * Matches the runtime UserDto shape the dashboard expects.
 */
export const DEFAULT_PROFILE = {
  id: "u-test-1",
  userName: "alice",
  email: "alice@acme.com",
  firstName: "Alice",
  lastName: "Nguyen",
  phoneNumber: "",
  isActive: true,
  emailConfirmed: true,
  twoFactorEnabled: false,
  imageUrl: null,
} as const;

/**
 * Mock every API call the authenticated AppShell fires on load so any
 * protected page can be visited in isolation without hanging on the
 * topbar's notification/chat badges or the SSE/realtime providers.
 *
 * ORDERING: Playwright matches the MOST RECENTLY registered route first.
 * We register broad globs first and the more-specific ones last. Callers
 * register their page-specific mocks AFTER calling this, so those win over
 * these defaults (e.g. a chat spec can return real channels).
 */
export async function installShellMocks(page: Page): Promise<void> {
  // Baseline runtime config, so the suite doesn't depend on what this project's
  // public/config.json happens to say (a project may hide modules there).
  // Registered first, so a per-test mockRuntimeConfig/withDisabledModules wins.
  await mockRuntimeConfig(page);

  // Long-lived realtime transports — abort so they neither keep the network
  // busy nor spew reconnect noise. The shell simply shows an "offline" dot.
  await page.route("**/api/v1/sse/**", (r: Route) => r.abort());
  await page.route("**/negotiate**", (r: Route) => r.abort());
  await page.route("**/api/v1/realtime/**", (r: Route) => r.abort());

  // Notifications — register the list glob first, then the more specific
  // unread-count (so the count request resolves to a number, not []).
  await mockJsonResponse(page, "**/api/v1/notifications**", []);
  await mockJsonResponse(page, "**/api/v1/notifications/unread-count**", 0);

  // Topbar chat unread badge.
  await mockJsonResponse(page, "**/api/v1/chat/channels**", []);

  // Defensive: profile + permissions (harmless if a page re-reads them).
  await mockJsonResponse(page, "**/api/v1/identity/profile", DEFAULT_PROFILE);
  await mockJsonResponse(page, "**/api/v1/identity/permissions", []);

  // Tenant status drives the global expiry/grace banner mounted in the
  // AppShell. Default to a healthy, far-future tenant so the banner stays
  // hidden; specs that exercise the banner override this after the call.
  await mockJsonResponse(page, "**/api/v1/tenants/me/status**", {
    id: "acme",
    name: "Acme Corp",
    isActive: true,
    validUpto: new Date(Date.now() + 365 * 24 * 60 * 60 * 1000).toISOString(),
    hasConnectionString: false,
    adminEmail: "admin@acme.com",
    issuer: null,
    plan: "Scale",
    expiryState: "Active",
    graceEndsUtc: new Date(Date.now() + 372 * 24 * 60 * 60 * 1000).toISOString(),
  });
}

/** Build a Playwright-shaped paged response body. */
export function paged<T>(items: T[], overrides: Partial<{ pageNumber: number; pageSize: number; totalCount: number; totalPages: number }> = {}) {
  const pageSize = overrides.pageSize ?? 20;
  const totalCount = overrides.totalCount ?? items.length;
  return {
    items,
    pageNumber: overrides.pageNumber ?? 1,
    pageSize,
    totalCount,
    totalPages: overrides.totalPages ?? Math.max(1, Math.ceil(totalCount / pageSize)),
    hasPrevious: (overrides.pageNumber ?? 1) > 1,
    hasNext: (overrides.pageNumber ?? 1) < (overrides.totalPages ?? Math.max(1, Math.ceil(totalCount / pageSize))),
  };
}

/**
 * The runtime config the app boots with in tests.
 *
 * Stubbed rather than served from `public/config.json` so a suite is hermetic:
 * a project generated from this template can hide modules in its own
 * config.json (see src/lib/modules.ts) without the inherited specs failing on
 * routes that no longer exist. `disabledModules: []` means "show everything".
 */
export const TEST_RUNTIME_CONFIG = {
  apiBase: "",
  defaultTenant: "root",
  demoMode: false,
  inactivityIdleMs: 1_200_000,
  inactivityWarningMs: 60_000,
  disabledModules: [] as readonly string[] | string,
};

/**
 * Serve /config.json with the test defaults, plus any overrides.
 *
 * `loadRuntimeConfig()` fetches once at boot with `cache: "no-store"`, so this
 * must be registered before navigation. Playwright runs the most recently
 * registered handler first, so a per-test call after `installShellMocks` wins.
 */
export async function mockRuntimeConfig(
  page: Page,
  overrides: Partial<typeof TEST_RUNTIME_CONFIG> = {},
): Promise<void> {
  await page.route("**/config.json", (route: Route) =>
    route.fulfill({
      status: 200,
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ ...TEST_RUNTIME_CONFIG, ...overrides }),
    }),
  );
}

/**
 * Boot the app with these UI modules hidden.
 *
 * Passing `[]` is a meaningful case, not a no-op: it asserts that an empty list
 * hides nothing (the failure mode this feature must never have). Accepts the
 * comma-separated string form too, which is what envsubst renders.
 */
export async function withDisabledModules(
  page: Page,
  modules: readonly string[] | string,
): Promise<void> {
  await mockRuntimeConfig(page, { disabledModules: modules });
}

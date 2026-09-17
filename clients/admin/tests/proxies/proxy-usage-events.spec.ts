import { expect, test } from "@playwright/test";
import { seedAuthedSession, TEST_USER } from "../helpers/auth-seed";
import { installAdminShellMocks, ADMIN_PERMS, paged } from "../helpers/shell-mocks";

const PROXY = {
  id: "11111111-1111-1111-1111-111111111111",
  host: "10.0.0.5",
  port: 3128,
  protocol: "Http",
  geolocation: "CL",
  status: "Disabled",
  providerAccountId: "acc-1",
  providerAccountName: "Manual",
  providerType: "Manual",
  providerGrouping: null,
  kind: null,
  tags: ["pais:cl"],
  createdAtUtc: "2026-01-01T00:00:00Z",
  lastRenewedAtUtc: null,
  username: "200.1.2.3",
  successCount24h: 4,
  failureCount24h: 6,
  lastEventAtUtc: "2026-09-17T10:00:00Z",
};

const EVENTS = [
  {
    id: "e1",
    proxyId: PROXY.id,
    proxyHost: PROXY.host,
    proxyPort: PROXY.port,
    source: "ConsumerFeedback",
    outcome: "Banned",
    reportedByApiClientId: "c1",
    reportedByApiClientName: "tender-grabber",
    healthCheckTargetId: null,
    detail: "HTTP 403 from target",
    occurredAtUtc: "2026-09-17T10:00:00Z",
  },
  {
    id: "e2",
    proxyId: PROXY.id,
    proxyHost: PROXY.host,
    proxyPort: PROXY.port,
    source: "SystemHealthCheck",
    outcome: "Success",
    reportedByApiClientId: null,
    reportedByApiClientName: null,
    healthCheckTargetId: "t1",
    detail: null,
    occurredAtUtc: "2026-09-17T09:45:00Z",
  },
];

test.beforeEach(async ({ page }) => {
  await seedAuthedSession(page, { ...TEST_USER, permissions: [...ADMIN_PERMS] });
  await installAdminShellMocks(page);
  await page.route("**/api/v1/proxies/provider-accounts*", async (route) => {
    await route.fulfill({ status: 200, headers: { "Content-Type": "application/json" }, body: JSON.stringify(paged([])) });
  });
  await page.route("**/api/v1/proxies/tag-categories*", async (route) => {
    await route.fulfill({ status: 200, headers: { "Content-Type": "application/json" }, body: JSON.stringify([]) });
  });
  await page.route("**/api/v1/proxies/?*", async (route) => {
    await route.fulfill({ status: 200, headers: { "Content-Type": "application/json" }, body: JSON.stringify(paged([PROXY])) });
  });
});

test.describe("proxy usage events", () => {
  test("shows the rolling 24h success/failure counts in the list", async ({ page }) => {
    await page.goto("/proxies");
    await expect(page.getByRole("heading", { name: "Proxies", exact: true })).toBeVisible({ timeout: 10_000 });

    const cell = page.getByRole("listitem").getByTitle(/4 successful \/ 6 failed in the last 24h/);
    await expect(cell).toBeVisible();
    await expect(cell).toHaveText("4 / 6");
  });

  test("renders a dash when the proxy has no events in the window", async ({ page }) => {
    await page.route("**/api/v1/proxies/?*", async (route) => {
      const body = paged([{ ...PROXY, successCount24h: 0, failureCount24h: 0, lastEventAtUtc: null }]);
      await route.fulfill({ status: 200, headers: { "Content-Type": "application/json" }, body: JSON.stringify(body) });
    });

    await page.goto("/proxies");
    await expect(page.getByRole("heading", { name: "Proxies", exact: true })).toBeVisible({ timeout: 10_000 });

    await expect(page.getByRole("listitem").getByTitle("No events in the last 24 hours")).toBeVisible();
  });

  test("opens the activity timeline scoped to the proxy and renders both event sources", async ({ page }) => {
    let lastUrl = "";
    await page.route("**/api/v1/proxies/usage-events*", async (route) => {
      lastUrl = route.request().url();
      await route.fulfill({ status: 200, headers: { "Content-Type": "application/json" }, body: JSON.stringify(paged(EVENTS)) });
    });

    await page.goto("/proxies");
    await expect(page.getByRole("heading", { name: "Proxies", exact: true })).toBeVisible({ timeout: 10_000 });
    await page.getByRole("button", { name: "View activity for 10.0.0.5:3128" }).click();

    await expect(page.getByRole("dialog").getByText("Activity", { exact: true })).toBeVisible();
    await expect.poll(() => new URL(lastUrl).searchParams.get("proxyId")).toBe(PROXY.id);

    const dialog = page.getByRole("dialog");
    // The badge is uppercased in CSS; the DOM text stays as the API returned it.
    await expect(dialog.getByText("Banned", { exact: true })).toBeVisible();
    await expect(dialog.getByText("HTTP 403 from target", { exact: true })).toBeVisible();
    await expect(dialog.getByText("tender-grabber", { exact: true })).toBeVisible();
    // The system probe has no reporter — it is labelled as a health check instead.
    await expect(dialog.getByText("health check", { exact: true })).toBeVisible();
  });

  test("filters the timeline to failures only", async ({ page }) => {
    let lastUrl = "";
    await page.route("**/api/v1/proxies/usage-events*", async (route) => {
      lastUrl = route.request().url();
      await route.fulfill({ status: 200, headers: { "Content-Type": "application/json" }, body: JSON.stringify(paged([EVENTS[0]])) });
    });

    await page.goto("/proxies");
    await expect(page.getByRole("heading", { name: "Proxies", exact: true })).toBeVisible({ timeout: 10_000 });
    await page.getByRole("button", { name: "View activity for 10.0.0.5:3128" }).click();
    await expect(page.getByRole("dialog")).toBeVisible();

    await page.getByRole("button", { name: "Failures only" }).click();

    await expect.poll(() => new URL(lastUrl).searchParams.get("failuresOnly")).toBe("true");
  });

  test("shows an empty state when the proxy has no recorded activity", async ({ page }) => {
    await page.route("**/api/v1/proxies/usage-events*", async (route) => {
      await route.fulfill({ status: 200, headers: { "Content-Type": "application/json" }, body: JSON.stringify(paged([])) });
    });

    await page.goto("/proxies");
    await expect(page.getByRole("heading", { name: "Proxies", exact: true })).toBeVisible({ timeout: 10_000 });
    await page.getByRole("button", { name: "View activity for 10.0.0.5:3128" }).click();

    await expect(page.getByText("No activity recorded yet.", { exact: true })).toBeVisible();
  });

  test("explains which policy governs the proxy and how close it is to tripping", async ({ page }) => {
    await page.route("**/api/v1/proxies/usage-events*", async (route) => {
      await route.fulfill({ status: 200, headers: { "Content-Type": "application/json" }, body: JSON.stringify(paged(EVENTS)) });
    });
    await page.route("**/api/v1/proxies/*/policy-resolution", async (route) => {
      await route.fulfill({
        status: 200,
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({
          proxyId: PROXY.id,
          tags: ["pais:cl", "uso:critico"],
          policy: {
            id: "p1",
            name: "Strict — scraping",
            type: "AutoDisableAndRenew",
            failureThreshold: 3,
            windowMinutes: 30,
            minDistinctReporters: 2,
          },
          policyFromTag: "uso:critico",
          candidates: [{ tagId: "t1", tagName: "uso:critico", profile: {}, isWinner: true }, { tagId: "t2", tagName: "pais:cl", profile: {}, isWinner: false }],
          failuresInWindow: 2,
          distinctReportersInWindow: 1,
          windowStartUtc: "2026-09-17T09:30:00Z",
          healthCheckTargets: [
            { id: "h1", name: "Mercado Publico", testUrl: "https://www.mercadopublico.cl", expectedStatusCode: 200, expectedBodyKeyword: null, timeoutMs: 8000, fromTag: "pais:cl" },
          ],
          usingDefaultHealthCheckTarget: false,
        }),
      });
    });

    await page.goto("/proxies");
    await expect(page.getByRole("heading", { name: "Proxies", exact: true })).toBeVisible({ timeout: 10_000 });
    await page.getByRole("button", { name: "View activity for 10.0.0.5:3128" }).click();

    const dialog = page.getByRole("dialog");
    await expect(dialog.getByText("Strict — scraping", { exact: true })).toBeVisible();
    await expect(dialog.getByText(/via tag/)).toContainText("uso:critico");
    // Several tags carried a policy — the UI must say the shown one won on restrictiveness.
    await expect(dialog.getByText(/most restrictive of several/)).toBeVisible();
    await expect(dialog.getByText("2/3 failures", { exact: true })).toBeVisible();
    await expect(dialog.getByText("1/2 reporters", { exact: true })).toBeVisible();
    await expect(dialog.getByText("Mercado Publico", { exact: true })).toBeVisible();
  });

  test("says plainly when no policy governs the proxy", async ({ page }) => {
    await page.route("**/api/v1/proxies/usage-events*", async (route) => {
      await route.fulfill({ status: 200, headers: { "Content-Type": "application/json" }, body: JSON.stringify(paged(EVENTS)) });
    });
    await page.route("**/api/v1/proxies/*/policy-resolution", async (route) => {
      await route.fulfill({
        status: 200,
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({
          proxyId: PROXY.id,
          tags: [],
          policy: null,
          policyFromTag: null,
          candidates: [],
          failuresInWindow: null,
          distinctReportersInWindow: null,
          windowStartUtc: null,
          healthCheckTargets: [
            { id: null, name: "Default (configured fallback)", testUrl: "https://www.google.com/generate_204", expectedStatusCode: null, expectedBodyKeyword: null, timeoutMs: 5000, fromTag: null },
          ],
          usingDefaultHealthCheckTarget: true,
        }),
      });
    });

    await page.goto("/proxies");
    await expect(page.getByRole("heading", { name: "Proxies", exact: true })).toBeVisible({ timeout: 10_000 });
    await page.getByRole("button", { name: "View activity for 10.0.0.5:3128" }).click();

    const dialog = page.getByRole("dialog");
    await expect(dialog.getByText("No policy governs this proxy.", { exact: true })).toBeVisible();
    await expect(dialog.getByText(/no tags, and policies are assigned per tag/)).toBeVisible();
    // The default target is a generic connectivity check — the panel must not let that pass silently.
    await expect(dialog.getByText(/generic connectivity check, not your destination/)).toBeVisible();
  });

  test("hides the activity button without the UsageEvents.View permission", async ({ page }) => {
    await seedAuthedSession(page, {
      ...TEST_USER,
      permissions: ADMIN_PERMS.filter((p) => p !== "Permissions.Proxies.UsageEvents.View"),
    });

    await page.goto("/proxies");
    await expect(page.getByRole("heading", { name: "Proxies", exact: true })).toBeVisible({ timeout: 10_000 });

    await expect(page.getByRole("button", { name: "View activity for 10.0.0.5:3128" })).toHaveCount(0);
  });
});

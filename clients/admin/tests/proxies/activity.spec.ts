import { expect, test } from "@playwright/test";
import { seedAuthedSession, TEST_USER } from "../helpers/auth-seed";
import { installAdminShellMocks, ADMIN_PERMS, paged } from "../helpers/shell-mocks";

const LONG_USER = "brd-customer-hl_c775be64-zone-datacenter_new_proxy_manager-ip-136.242.113.201";

const EVENTS = [
  {
    id: "e1",
    proxyId: "11111111-1111-1111-1111-111111111111",
    proxyHost: "brd.superproxy.io",
    proxyPort: 33335,
    proxyUsername: LONG_USER,
    source: "ConsumerFeedback",
    outcome: "Banned",
    reportedByApiClientId: "c1",
    reportedByApiClientName: "tender-grabber",
    healthCheckTargetId: null,
    detail: "HTTP 403 Forbidden",
    occurredAtUtc: "2026-09-17T10:00:00Z",
  },
  {
    id: "e2",
    proxyId: "22222222-2222-2222-2222-222222222222",
    proxyHost: "10.0.0.5",
    proxyPort: 3128,
    proxyUsername: null,
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
});

test.describe("fleet activity", () => {
  test("lists events across every proxy, newest first", async ({ page }) => {
    let lastUrl = "";
    await page.route("**/api/v1/proxies/usage-events*", async (route) => {
      lastUrl = route.request().url();
      await route.fulfill({ status: 200, headers: { "Content-Type": "application/json" }, body: JSON.stringify(paged(EVENTS)) });
    });

    await page.goto("/proxies/activity");

    await expect(page.getByRole("heading", { name: "Activity", exact: true })).toBeVisible({ timeout: 10_000 });
    // Fleet-wide: the request must NOT be scoped to a single proxy.
    await expect.poll(() => new URL(lastUrl).searchParams.has("proxyId")).toBe(false);

    await expect(page.getByRole("listitem").getByText("brd.superproxy.io:33335", { exact: true })).toBeVisible();
    await expect(page.getByRole("listitem").getByText("10.0.0.5:3128", { exact: true })).toBeVisible();
    await expect(page.getByRole("listitem").getByText("tender-grabber", { exact: true })).toBeVisible();
    await expect(page.getByRole("listitem").getByText("health check", { exact: true })).toBeVisible();
    await expect(page.getByRole("listitem").getByText("2026-09-17 10:00:00Z", { exact: true })).toBeVisible();
  });

  test("shows the proxy user in full, and nothing for a proxy with no auth", async ({ page }) => {
    await page.route("**/api/v1/proxies/usage-events*", async (route) => {
      await route.fulfill({ status: 200, headers: { "Content-Type": "application/json" }, body: JSON.stringify(paged(EVENTS)) });
    });

    await page.setViewportSize({ width: 1440, height: 900 });
    await page.goto("/proxies/activity");
    await expect(page.getByRole("heading", { name: "Activity", exact: true })).toBeVisible({ timeout: 10_000 });

    const userCell = page.getByRole("listitem").getByTitle(LONG_USER);
    await expect(userCell).toHaveText(LONG_USER);
    // Text alone does not prove it is readable — CSS clipping leaves it in the DOM.
    const hidden = await userCell.evaluate((el) => el.scrollHeight - el.clientHeight);
    expect(hidden).toBeLessThanOrEqual(1);

    // The second event's proxy has no auth username: no stray element for it.
    await expect(page.getByRole("listitem")).toHaveCount(2);
  });

  test("defaults to the last 24 hours and sends a fromUtc bound", async ({ page }) => {
    let lastUrl = "";
    await page.route("**/api/v1/proxies/usage-events*", async (route) => {
      lastUrl = route.request().url();
      await route.fulfill({ status: 200, headers: { "Content-Type": "application/json" }, body: JSON.stringify(paged(EVENTS)) });
    });

    await page.goto("/proxies/activity");
    await expect(page.getByRole("heading", { name: "Activity", exact: true })).toBeVisible({ timeout: 10_000 });

    const from = new URL(lastUrl).searchParams.get("fromUtc");
    expect(from).not.toBeNull();
    const hoursAgo = (Date.now() - Date.parse(from!)) / 3600_000;
    expect(hoursAgo).toBeGreaterThan(23.5);
    expect(hoursAgo).toBeLessThan(24.5);
  });

  test("drops the time bound when the range is All time", async ({ page }) => {
    let lastUrl = "";
    await page.route("**/api/v1/proxies/usage-events*", async (route) => {
      lastUrl = route.request().url();
      await route.fulfill({ status: 200, headers: { "Content-Type": "application/json" }, body: JSON.stringify(paged(EVENTS)) });
    });

    await page.goto("/proxies/activity");
    await expect(page.getByRole("heading", { name: "Activity", exact: true })).toBeVisible({ timeout: 10_000 });

    await page.getByTestId("activity-range-select").getByRole("button").click();
    await page.getByRole("menuitem", { name: "All time", exact: true }).click();

    await expect.poll(() => new URL(lastUrl).searchParams.has("fromUtc")).toBe(false);
  });

  test("filters to failures only", async ({ page }) => {
    let lastUrl = "";
    await page.route("**/api/v1/proxies/usage-events*", async (route) => {
      lastUrl = route.request().url();
      await route.fulfill({ status: 200, headers: { "Content-Type": "application/json" }, body: JSON.stringify(paged([EVENTS[0]])) });
    });

    await page.goto("/proxies/activity");
    await expect(page.getByRole("heading", { name: "Activity", exact: true })).toBeVisible({ timeout: 10_000 });

    await page.getByRole("button", { name: "Failures only" }).click();

    await expect.poll(() => new URL(lastUrl).searchParams.get("failuresOnly")).toBe("true");
  });

  test("shows an empty state when the range has no events", async ({ page }) => {
    await page.route("**/api/v1/proxies/usage-events*", async (route) => {
      await route.fulfill({ status: 200, headers: { "Content-Type": "application/json" }, body: JSON.stringify(paged([])) });
    });

    await page.goto("/proxies/activity");

    await expect(page.getByText("No events in this range.", { exact: true })).toBeVisible({ timeout: 10_000 });
  });

  test("is not reachable without the UsageEvents.View permission", async ({ page }) => {
    await seedAuthedSession(page, {
      ...TEST_USER,
      permissions: ADMIN_PERMS.filter((p) => p !== "Permissions.Proxies.UsageEvents.View"),
    });
    await page.route("**/api/v1/proxies/usage-events*", async (route) => {
      await route.fulfill({ status: 200, headers: { "Content-Type": "application/json" }, body: JSON.stringify(paged(EVENTS)) });
    });

    await page.goto("/proxies/activity");

    await expect(page.getByRole("heading", { name: "Activity", exact: true })).toHaveCount(0);
  });
});

import { expect, test } from "@playwright/test";
import { seedAuthedSession, TEST_USER } from "../helpers/auth-seed";
import { installAdminShellMocks, ADMIN_PERMS, paged } from "../helpers/shell-mocks";

const PROXY_CL = {
  id: "11111111-1111-1111-1111-111111111111",
  host: "10.0.0.5",
  port: 3128,
  protocol: "Http",
  geolocation: "CL",
  status: "Active",
  providerAccountId: "acc-1",
  providerAccountName: "Manual",
  providerType: "Manual",
  providerGrouping: null,
  tags: ["pais:cl"],
  createdAtUtc: "2026-01-01T00:00:00Z",
  lastRenewedAtUtc: null,
};

const PROXY_RESIDENTIAL = {
  ...PROXY_CL,
  id: "22222222-2222-2222-2222-222222222222",
  host: "10.0.0.6",
  kind: "Residential",
};

const TAG_CATEGORIES = [
  { id: "cat-1", name: "country", values: ["CL", "AR"] },
  { id: "cat-2", name: "entityType", values: ["Tender", "PurchaseOrder"] },
];

test.beforeEach(async ({ page }) => {
  await seedAuthedSession(page, { ...TEST_USER, permissions: [...ADMIN_PERMS] });
  await installAdminShellMocks(page);
  await page.route("**/api/v1/proxies/provider-accounts*", async (route) => {
    await route.fulfill({ status: 200, headers: { "Content-Type": "application/json" }, body: JSON.stringify(paged([])) });
  });
  await page.route("**/api/v1/proxies/tag-categories*", async (route) => {
    await route.fulfill({ status: 200, headers: { "Content-Type": "application/json" }, body: JSON.stringify(TAG_CATEGORIES) });
  });
});

test.describe("proxies list", () => {
  test("renders a proxy row from the mock", async ({ page }) => {
    await page.route("**/api/v1/proxies/?*", async (route) => {
      await route.fulfill({ status: 200, headers: { "Content-Type": "application/json" }, body: JSON.stringify(paged([PROXY_CL])) });
    });

    await page.goto("/proxies");

    await expect(page.getByRole("heading", { name: "Proxies", exact: true })).toBeVisible({ timeout: 10_000 });
    // The same text renders in both the (CSS-hidden-on-desktop) mobile card
    // and the desktop table row; both are still present in the accessibility
    // tree, so scope to the desktop <li> row to avoid a strict-mode clash.
    await expect(page.getByRole("listitem").getByText("10.0.0.5:3128", { exact: true })).toBeVisible();
  });

  test("shows the empty state when no proxies match", async ({ page }) => {
    await page.route("**/api/v1/proxies/?*", async (route) => {
      await route.fulfill({ status: 200, headers: { "Content-Type": "application/json" }, body: JSON.stringify(paged([])) });
    });

    await page.goto("/proxies");

    await expect(page.getByText("No proxies match these filters.", { exact: true })).toBeVisible({ timeout: 10_000 });
  });

  test("calls the disable endpoint when clicking Disable on an active proxy", async ({ page }) => {
    await page.route("**/api/v1/proxies/?*", async (route) => {
      await route.fulfill({ status: 200, headers: { "Content-Type": "application/json" }, body: JSON.stringify(paged([PROXY_CL])) });
    });
    let disableCalled = false;
    await page.route("**/api/v1/proxies/disable", async (route) => {
      disableCalled = true;
      await route.fulfill({ status: 200, headers: { "Content-Type": "application/json" }, body: JSON.stringify(1) });
    });

    await page.goto("/proxies");
    await expect(page.getByRole("heading", { name: "Proxies", exact: true })).toBeVisible({ timeout: 10_000 });
    await page.getByRole("button", { name: "Disable", exact: true }).click();

    await expect.poll(() => disableCalled).toBe(true);
  });


  test("filters by a catalog category/value pair via the dynamic tag picker", async ({ page }) => {
    let lastUrl = "";
    await page.route("**/api/v1/proxies/?*", async (route) => {
      lastUrl = route.request().url();
      await route.fulfill({ status: 200, headers: { "Content-Type": "application/json" }, body: JSON.stringify(paged([])) });
    });

    await page.goto("/proxies");
    await expect(page.getByRole("heading", { name: "Proxies", exact: true })).toBeVisible({ timeout: 10_000 });

    // The Select is a Radix DropdownMenu-based combobox, not a native <select>.
    await page.getByTestId("proxies-tag-category-select").getByRole("button").click();
    await page.getByRole("menuitem", { name: "country", exact: true }).click();
    await page.getByTestId("proxies-tag-value-select").getByRole("button").click();
    await page.getByRole("menuitem", { name: /CL/ }).click();
    await page.getByRole("button", { name: "Add category tag filter" }).click();

    await expect(page.getByText("country:CL", { exact: true })).toBeVisible();
    await expect.poll(() => new URL(lastUrl).searchParams.getAll("tags")).toEqual(["country:CL"]);

    // Removing the chip clears the filter.
    await page.getByRole("button", { name: "Remove tag filter country:CL" }).click();
    await expect(page.getByText("country:CL", { exact: true })).toHaveCount(0);
  });

  test("filters by a custom free-text tag", async ({ page }) => {
    let lastUrl = "";
    await page.route("**/api/v1/proxies/?*", async (route) => {
      lastUrl = route.request().url();
      await route.fulfill({ status: 200, headers: { "Content-Type": "application/json" }, body: JSON.stringify(paged([])) });
    });

    await page.goto("/proxies");
    await expect(page.getByRole("heading", { name: "Proxies", exact: true })).toBeVisible({ timeout: 10_000 });

    await page.getByRole("searchbox", { name: "Custom tag filter" }).fill("legacy-note");
    await page.getByRole("button", { name: "Add custom tag filter" }).click();

    await expect(page.getByText("legacy-note", { exact: true })).toBeVisible();
    await expect.poll(() => new URL(lastUrl).searchParams.getAll("tags")).toEqual(["legacy-note"]);
  });

  test("shows the provider-reported geolocation next to the protocol", async ({ page }) => {
    await page.route("**/api/v1/proxies/?*", async (route) => {
      await route.fulfill({ status: 200, headers: { "Content-Type": "application/json" }, body: JSON.stringify(paged([PROXY_CL])) });
    });

    await page.goto("/proxies");

    await expect(page.getByRole("heading", { name: "Proxies", exact: true })).toBeVisible({ timeout: 10_000 });
    await expect(page.getByRole("listitem").getByText("Http · 🇨🇱 CL", { exact: true })).toBeVisible();

  });

  test("filters by ProxyKind", async ({ page }) => {
    let lastUrl = "";
    await page.route("**/api/v1/proxies/?*", async (route) => {
      lastUrl = route.request().url();
      await route.fulfill({ status: 200, headers: { "Content-Type": "application/json" }, body: JSON.stringify(paged([PROXY_RESIDENTIAL])) });
    });

    await page.goto("/proxies");
    await expect(page.getByRole("heading", { name: "Proxies", exact: true })).toBeVisible({ timeout: 10_000 });
    await expect(page.getByRole("listitem").getByText("Http · 🇨🇱 CL · Residential", { exact: true })).toBeVisible();

    await page.getByTestId("proxies-kind-select").getByRole("button").click();
    await page.getByRole("menuitem", { name: "Residential", exact: true }).click();

    await expect.poll(() => new URL(lastUrl).searchParams.get("kind")).toBe("Residential");
  });
});

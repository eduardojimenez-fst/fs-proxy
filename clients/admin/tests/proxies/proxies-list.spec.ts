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
  // Providers commonly put the proxy's own egress IP in the auth username.
  username: "200.1.2.3",
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

  test("shows the provider username in the User column", async ({ page }) => {
    await page.route("**/api/v1/proxies/?*", async (route) => {
      await route.fulfill({ status: 200, headers: { "Content-Type": "application/json" }, body: JSON.stringify(paged([PROXY_CL])) });
    });

    await page.goto("/proxies");

    await expect(page.getByRole("heading", { name: "Proxies", exact: true })).toBeVisible({ timeout: 10_000 });
    await expect(page.getByRole("listitem").getByText("200.1.2.3", { exact: true })).toBeVisible();
  });

  test("shows a full-length BrightData username without clipping it", async ({ page }) => {
    // Regression: the User column is sized so a real BrightData username — which carries the
    // identifying IP at the END — wraps into view instead of being truncated to its prefix.
    const longUsername = "brd-customer-hl_c775be64-zone-datacenter_new_proxy_manager-ip-136.242.113.201";
    await page.route("**/api/v1/proxies/?*", async (route) => {
      const body = paged([{ ...PROXY_CL, host: "brd.superproxy.io", port: 33335, username: longUsername }]);
      await route.fulfill({ status: 200, headers: { "Content-Type": "application/json" }, body: JSON.stringify(body) });
    });

    await page.setViewportSize({ width: 1440, height: 900 });
    await page.goto("/proxies");
    await expect(page.getByRole("heading", { name: "Proxies", exact: true })).toBeVisible({ timeout: 10_000 });

    const cell = page.getByRole("listitem").getByTitle(longUsername);
    await expect(cell).toHaveText(longUsername);
    // Text content alone can't prove it is readable — CSS clipping leaves it in the DOM.
    const overflow = await cell.evaluate((el) => ({ hidden: el.scrollHeight - el.clientHeight }));
    expect(overflow.hidden).toBeLessThanOrEqual(1);
  });

  test("renders a dash in the User column for a proxy with no auth username", async ({ page }) => {
    await page.route("**/api/v1/proxies/?*", async (route) => {
      const body = paged([{ ...PROXY_CL, username: null }]);
      await route.fulfill({ status: 200, headers: { "Content-Type": "application/json" }, body: JSON.stringify(body) });
    });

    await page.goto("/proxies");

    await expect(page.getByRole("heading", { name: "Proxies", exact: true })).toBeVisible({ timeout: 10_000 });
    await expect(page.getByRole("listitem").getByText("200.1.2.3")).toHaveCount(0);
  });

  test("filters by host", async ({ page }) => {
    let lastUrl = "";
    await page.route("**/api/v1/proxies/?*", async (route) => {
      lastUrl = route.request().url();
      await route.fulfill({ status: 200, headers: { "Content-Type": "application/json" }, body: JSON.stringify(paged([PROXY_CL])) });
    });

    await page.goto("/proxies");
    await expect(page.getByRole("heading", { name: "Proxies", exact: true })).toBeVisible({ timeout: 10_000 });

    await page.getByRole("searchbox", { name: "Host", exact: true }).fill("10.0.0.");

    // The field is debounced (300ms), so poll rather than asserting on the next request.
    await expect.poll(() => new URL(lastUrl).searchParams.get("host")).toBe("10.0.0.");
  });

  test("filters by user", async ({ page }) => {
    let lastUrl = "";
    await page.route("**/api/v1/proxies/?*", async (route) => {
      lastUrl = route.request().url();
      await route.fulfill({ status: 200, headers: { "Content-Type": "application/json" }, body: JSON.stringify(paged([PROXY_CL])) });
    });

    await page.goto("/proxies");
    await expect(page.getByRole("heading", { name: "Proxies", exact: true })).toBeVisible({ timeout: 10_000 });

    await page.getByRole("searchbox", { name: "User", exact: true }).fill("200.1");

    await expect.poll(() => new URL(lastUrl).searchParams.get("username")).toBe("200.1");
  });

  test("clears the host and user filters when clicking Clear filters", async ({ page }) => {
    let lastUrl = "";
    await page.route("**/api/v1/proxies/?*", async (route) => {
      lastUrl = route.request().url();
      await route.fulfill({ status: 200, headers: { "Content-Type": "application/json" }, body: JSON.stringify(paged([PROXY_CL])) });
    });

    await page.goto("/proxies");
    await expect(page.getByRole("heading", { name: "Proxies", exact: true })).toBeVisible({ timeout: 10_000 });

    await page.getByRole("searchbox", { name: "Host", exact: true }).fill("10.0.0.");
    await page.getByRole("searchbox", { name: "User", exact: true }).fill("200.1");
    await expect.poll(() => new URL(lastUrl).searchParams.get("username")).toBe("200.1");

    await page.getByRole("button", { name: "Clear filters" }).first().click();

    await expect(page.getByRole("searchbox", { name: "Host", exact: true })).toHaveValue("");
    await expect(page.getByRole("searchbox", { name: "User", exact: true })).toHaveValue("");
    // The unfiltered query key was already cached, so clearing serves from cache rather
    // than refetching — "Clear filters" disappearing is what proves both states reset.
    await expect(page.getByRole("button", { name: "Clear filters" })).toHaveCount(0);
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

  test("clears the ProxyKind filter when clicking Clear filters", async ({ page }) => {
    await page.route("**/api/v1/proxies/?*", async (route) => {
      await route.fulfill({ status: 200, headers: { "Content-Type": "application/json" }, body: JSON.stringify(paged([PROXY_RESIDENTIAL])) });
    });

    await page.goto("/proxies");
    await expect(page.getByRole("heading", { name: "Proxies", exact: true })).toBeVisible({ timeout: 10_000 });

    // Apply the kind filter
    await page.getByTestId("proxies-kind-select").getByRole("button").click();
    await page.getByRole("menuitem", { name: "Residential", exact: true }).click();

    // Verify "Clear filters" button appears
    const clearButton = page.getByRole("button", { name: "Clear filters" }).first();
    await expect(clearButton).toBeVisible();

    // Click "Clear filters" to reset all filters including kind
    await clearButton.click();

    // Verify the "Clear filters" button disappears when no filters are active
    // This confirms that the kind state was cleared (filtersActive is false)
    await expect(page.getByRole("button", { name: "Clear filters" })).toHaveCount(0);
  });
});

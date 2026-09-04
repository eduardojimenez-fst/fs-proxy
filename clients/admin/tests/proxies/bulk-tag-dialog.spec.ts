import { expect, test } from "@playwright/test";
import { seedAuthedSession, TEST_USER } from "../helpers/auth-seed";
import { installAdminShellMocks, ADMIN_PERMS, paged } from "../helpers/shell-mocks";

const PROXY = {
  id: "11111111-1111-1111-1111-111111111111",
  host: "10.0.0.5",
  port: 3128,
  protocol: "Http",
  country: "CL",
  status: "Active",
  providerAccountId: "acc-1",
  providerAccountName: "Manual",
  providerType: "Manual",
  providerGrouping: null,
  tags: [],
  createdAtUtc: "2026-01-01T00:00:00Z",
  lastRenewedAtUtc: null,
};

const PAIS_CATEGORY = { id: "cat-1", name: "pais", values: ["ar", "cl"] };

test.beforeEach(async ({ page }) => {
  await seedAuthedSession(page, { ...TEST_USER, permissions: [...ADMIN_PERMS] });
  await installAdminShellMocks(page);
  await page.route("**/api/v1/proxies/provider-accounts*", async (route) => {
    await route.fulfill({ status: 200, headers: { "Content-Type": "application/json" }, body: JSON.stringify(paged([])) });
  });
  await page.route("**/api/v1/proxies/?*", async (route) => {
    await route.fulfill({ status: 200, headers: { "Content-Type": "application/json" }, body: JSON.stringify(paged([PROXY])) });
  });
  await page.route("**/api/v1/proxies/tag-categories", async (route) => {
    await route.fulfill({ status: 200, headers: { "Content-Type": "application/json" }, body: JSON.stringify([PAIS_CATEGORY]) });
  });
});

test.describe("bulk tag editor", () => {
  test("adds a category-selected tag to every checked proxy", async ({ page }) => {
    let assignBody: unknown;
    await page.route("**/api/v1/proxies/tags/assign", async (route) => {
      assignBody = route.request().postDataJSON();
      await route.fulfill({ status: 200, headers: { "Content-Type": "application/json" }, body: JSON.stringify(1) });
    });

    await page.goto("/proxies");
    await expect(page.getByRole("heading", { name: "Proxies", exact: true })).toBeVisible({ timeout: 10_000 });
    await page.getByRole("checkbox", { name: /Select 10.0.0.5:3128/ }).check();
    await page.getByRole("button", { name: "Manage tags" }).click();
    // The Select is a Radix DropdownMenu-based combobox, not a native <select>.
    await page.getByTestId("bulk-add-category-select").getByRole("button").click();
    await page.getByRole("menuitem", { name: "pais", exact: true }).click();
    await page.getByTestId("bulk-add-value-select").getByRole("button").click();
    await page.getByRole("menuitem", { name: "cl", exact: true }).click();
    await page.getByRole("button", { name: "Add to 1 selected" }).click();

    await expect.poll(() => assignBody).toEqual({ proxyIds: ["11111111-1111-1111-1111-111111111111"], tagName: "pais:cl" });
  });
});

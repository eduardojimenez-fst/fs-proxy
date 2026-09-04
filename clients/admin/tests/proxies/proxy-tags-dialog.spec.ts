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
  tags: ["pais:cl"],
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

test.describe("individual proxy tag editor", () => {
  test("pre-selects the category value matching the proxy's current tags, and submits the composed set", async ({ page }) => {
    let putBody: unknown;
    await page.route("**/api/v1/proxies/11111111-1111-1111-1111-111111111111/tags", async (route) => {
      putBody = route.request().postDataJSON();
      await route.fulfill({ status: 204 });
    });

    await page.goto("/proxies");
    await expect(page.getByRole("heading", { name: "Proxies", exact: true })).toBeVisible({ timeout: 10_000 });
    await page.getByRole("button", { name: "Tags", exact: true }).click();

    // The Select is a Radix DropdownMenu-based combobox, not a native <select> — its trigger
    // button's visible text is the current value, and options open as menuitems.
    const paisSelect = page.getByTestId("tag-category-select-pais");
    await expect(paisSelect.getByRole("button")).toHaveText("cl");
    await paisSelect.getByRole("button").click();
    await page.getByRole("menuitem", { name: "ar", exact: true }).click();
    await page.getByRole("button", { name: "Save" }).click();

    await expect.poll(() => putBody).toEqual({ tagNames: ["pais:ar"] });
  });
});

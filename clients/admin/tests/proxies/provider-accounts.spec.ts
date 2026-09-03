import { expect, test } from "@playwright/test";
import { seedAuthedSession, TEST_USER } from "../helpers/auth-seed";
import { installAdminShellMocks, ADMIN_PERMS, paged } from "../helpers/shell-mocks";

test.beforeEach(async ({ page }) => {
  await seedAuthedSession(page, { ...TEST_USER, permissions: [...ADMIN_PERMS] });
  await installAdminShellMocks(page);
});

test.describe("provider accounts", () => {
  test("creates a new provider account", async ({ page }) => {
    await page.route("**/api/v1/proxies/provider-accounts*", async (route) => {
      if (route.request().method() === "GET") {
        await route.fulfill({ status: 200, headers: { "Content-Type": "application/json" }, body: JSON.stringify(paged([])) });
      } else {
        await route.fulfill({ status: 200, headers: { "Content-Type": "application/json" }, body: JSON.stringify("new-id") });
      }
    });

    await page.goto("/proxies/provider-accounts");
    await expect(page.getByRole("heading", { name: "Provider accounts", exact: true })).toBeVisible({ timeout: 10_000 });

    // The empty-state action button duplicates the header's "New provider
    // account" button while the list is empty — both open the same dialog,
    // so scope to the first (header) instance to avoid a strict-mode clash.
    await page.getByRole("button", { name: "New provider account", exact: true }).first().click();
    await page.getByLabel("Name").fill("WebShare - test");
    await page.getByLabel(/Credentials/).fill('{"apiKey":"key-123"}');
    await page.getByRole("button", { name: "Save", exact: true }).click();

    await expect(page.getByText("Provider account created", { exact: true })).toBeVisible({ timeout: 10_000 });
  });
});

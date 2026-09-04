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

  test("shows the synced proxy count and cascade-deletes on confirm", async ({ page }) => {
    const account = {
      id: "acc-1",
      name: "BrightData - JP",
      providerType: "BrightData",
      isEnabled: true,
      lastSyncedAtUtc: null,
      lastSyncStatus: null,
      consecutiveSyncFailures: 0,
    };
    await page.route("**/api/v1/proxies/provider-accounts*", async (route) => {
      if (route.request().method() === "GET") {
        await route.fulfill({ status: 200, headers: { "Content-Type": "application/json" }, body: JSON.stringify(paged([account])) });
      }
    });
    await page.route("**/api/v1/proxies/?*", async (route) => {
      await route.fulfill({ status: 200, headers: { "Content-Type": "application/json" }, body: JSON.stringify(paged([], { totalCount: 3 })) });
    });
    let deleteUrl: string | null = null;
    await page.route("**/api/v1/proxies/provider-accounts/acc-1*", async (route) => {
      if (route.request().method() === "DELETE") {
        deleteUrl = route.request().url();
        await route.fulfill({ status: 204 });
      } else {
        await route.fallback();
      }
    });

    await page.goto("/proxies/provider-accounts");
    await expect(page.getByRole("heading", { name: "Provider accounts", exact: true })).toBeVisible({ timeout: 10_000 });

    await page.getByRole("button", { name: "Delete BrightData - JP", exact: true }).click();

    await expect(page.getByText(/permanently delete 3 synced proxies/, { exact: false })).toBeVisible({ timeout: 10_000 });

    await page.getByRole("button", { name: "Delete", exact: true }).click();

    await expect(page.getByText("Provider account deleted", { exact: true })).toBeVisible({ timeout: 10_000 });
    await expect.poll(() => deleteUrl).toContain("force=true");
  });
});

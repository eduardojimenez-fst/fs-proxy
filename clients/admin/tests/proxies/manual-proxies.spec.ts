import { expect, test } from "@playwright/test";
import { seedAuthedSession, TEST_USER } from "../helpers/auth-seed";
import { installAdminShellMocks, ADMIN_PERMS, paged } from "../helpers/shell-mocks";

test.beforeEach(async ({ page }) => {
  await seedAuthedSession(page, { ...TEST_USER, permissions: [...ADMIN_PERMS] });
  await installAdminShellMocks(page);
  await page.route("**/api/v1/proxies/provider-accounts*", async (route) => {
    await route.fulfill({
      status: 200,
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify(
        paged([
          {
            id: "manual-acct",
            name: "Manual",
            providerType: "Manual",
            isEnabled: true,
            lastSyncedAtUtc: null,
            lastSyncStatus: null,
            consecutiveSyncFailures: 0,
          },
        ]),
      ),
    });
  });
});

test.describe("manual proxies", () => {
  test("shows the empty state before any manual proxy exists", async ({ page }) => {
    await page.route("**/api/v1/proxies/?*", async (route) => {
      await route.fulfill({ status: 200, headers: { "Content-Type": "application/json" }, body: JSON.stringify(paged([])) });
    });

    await page.goto("/proxies/manual");

    await expect(page.getByRole("heading", { name: "Manual proxies", exact: true })).toBeVisible({ timeout: 10_000 });
    await expect(page.getByText("No manual proxies yet.", { exact: true })).toBeVisible({ timeout: 10_000 });
  });
});

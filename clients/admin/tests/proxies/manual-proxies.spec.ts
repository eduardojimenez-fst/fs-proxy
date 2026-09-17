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

  test("pre-fills the current username when editing, so saving does not wipe it", async ({ page }) => {
    await page.route("**/api/v1/proxies/?*", async (route) => {
      const body = paged([
        {
          id: "33333333-3333-3333-3333-333333333333",
          host: "10.0.0.5",
          port: 3128,
          protocol: "Http",
          status: "Active",
          providerAccountId: "manual-acct",
          providerAccountName: "Manual",
          providerType: "Manual",
          tags: ["pais:cl"],
          createdAtUtc: "2026-01-01T00:00:00Z",
          lastRenewedAtUtc: null,
          geolocation: "CL",
          providerGrouping: null,
          kind: null,
          username: "200.1.2.3",
        },
      ]);
      await route.fulfill({ status: 200, headers: { "Content-Type": "application/json" }, body: JSON.stringify(body) });
    });
    let putBody: Record<string, unknown> = {};
    await page.route("**/api/v1/proxies/manual-proxies/*", async (route) => {
      putBody = route.request().postDataJSON();
      await route.fulfill({ status: 204, body: "" });
    });

    await page.goto("/proxies/manual");
    await expect(page.getByRole("heading", { name: "Manual proxies", exact: true })).toBeVisible({ timeout: 10_000 });

    await page.getByRole("button", { name: "Edit", exact: true }).first().click();
    await expect(page.getByLabel("Username")).toHaveValue("200.1.2.3");

    // Submitting an untouched form must round-trip the username, not blank it.
    await page.getByRole("button", { name: "Save", exact: true }).click();
    await expect.poll(() => putBody.username).toBe("200.1.2.3");
  });
});

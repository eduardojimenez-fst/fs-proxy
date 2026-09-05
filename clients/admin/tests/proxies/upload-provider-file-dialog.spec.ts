import { expect, test } from "@playwright/test";
import { seedAuthedSession, TEST_USER } from "../helpers/auth-seed";
import { installAdminShellMocks, ADMIN_PERMS, paged } from "../helpers/shell-mocks";

const ACCOUNT = {
  id: "acc-1",
  name: "Oxylabs - CL",
  providerType: "Oxylabs",
  isEnabled: true,
  lastSyncedAtUtc: null,
  lastSyncStatus: null,
  consecutiveSyncFailures: 0,
};

test.beforeEach(async ({ page }) => {
  await seedAuthedSession(page, { ...TEST_USER, permissions: [...ADMIN_PERMS] });
  await installAdminShellMocks(page);
  await page.route("**/api/v1/proxies/provider-accounts*", async (route) => {
    if (route.request().method() === "GET") {
      await route.fulfill({ status: 200, headers: { "Content-Type": "application/json" }, body: JSON.stringify(paged([ACCOUNT])) });
    } else {
      await route.fallback();
    }
  });
});

test.describe("upload provider file dialog", () => {
  test("uploads a file with default credentials and shows the result summary", async ({ page }) => {
    let capturedForm: { fileName?: string; defaultUsername?: string } = {};
    await page.route("**/api/v1/proxies/provider-accounts/acc-1/sync-from-file", async (route) => {
      const request = route.request();
      const body = request.postDataBuffer()?.toString("utf-8") ?? "";
      capturedForm = {
        fileName: /filename="([^"]+)"/.exec(body)?.[1],
        defaultUsername: /name="defaultUsername"\r\n\r\n([^\r\n]+)/.exec(body)?.[1],
      };
      await route.fulfill({
        status: 200,
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ created: 10, updated: 0, retired: 0, errors: [] }),
      });
    });

    await page.goto("/proxies/provider-accounts");
    await expect(page.getByRole("heading", { name: "Provider accounts", exact: true })).toBeVisible({ timeout: 10_000 });

    await page.getByRole("button", { name: "Upload file for Oxylabs - CL" }).click();
    await page.setInputFiles('input[type="file"]', {
      name: "oxylabs.csv",
      mimeType: "text/csv",
      buffer: Buffer.from("Host,Port,Protocol,Username,Password,Geolocation,ProxyKind\ndc.oxylabs.io,8007,Http,,,CL,DataCenter"),
    });
    await page.getByLabel("Default username").fill("acct-user");
    await page.getByLabel("Default password").fill("acct-pass");
    await page.getByRole("button", { name: "Upload", exact: true }).click();

    await expect(page.getByText("10 created, 0 updated, 0 retired", { exact: true })).toBeVisible({ timeout: 10_000 });
    expect(capturedForm.fileName).toBe("oxylabs.csv");
    expect(capturedForm.defaultUsername).toBe("acct-user");
  });

  test("shows per-row errors when the response includes them", async ({ page }) => {
    await page.route("**/api/v1/proxies/provider-accounts/acc-1/sync-from-file", async (route) => {
      await route.fulfill({
        status: 200,
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ created: 1, updated: 0, retired: 0, errors: [{ lineNumber: 3, message: "Host is required." }] }),
      });
    });

    await page.goto("/proxies/provider-accounts");
    await expect(page.getByRole("heading", { name: "Provider accounts", exact: true })).toBeVisible({ timeout: 10_000 });
    await page.getByRole("button", { name: "Upload file for Oxylabs - CL" }).click();
    await page.setInputFiles('input[type="file"]', {
      name: "oxylabs.csv",
      mimeType: "text/csv",
      buffer: Buffer.from("Host,Port,Protocol,Username,Password,Geolocation,ProxyKind\n"),
    });
    await page.getByRole("button", { name: "Upload", exact: true }).click();

    await expect(page.getByText("line 3: Host is required.", { exact: true })).toBeVisible({ timeout: 10_000 });
  });
});

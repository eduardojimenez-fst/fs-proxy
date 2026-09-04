import { expect, test } from "@playwright/test";
import { seedAuthedSession, TEST_USER } from "../helpers/auth-seed";
import { installAdminShellMocks, ADMIN_PERMS } from "../helpers/shell-mocks";

const PAIS_CATEGORY = { id: "cat-1", name: "pais", values: ["ar", "cl"] };

test.beforeEach(async ({ page }) => {
  await seedAuthedSession(page, { ...TEST_USER, permissions: [...ADMIN_PERMS] });
  await installAdminShellMocks(page);
});

test.describe("tag categories", () => {
  test("renders a category with its values", async ({ page }) => {
    await page.route("**/api/v1/proxies/tag-categories", async (route) => {
      if (route.request().method() === "GET") {
        await route.fulfill({ status: 200, headers: { "Content-Type": "application/json" }, body: JSON.stringify([PAIS_CATEGORY]) });
      } else {
        await route.continue();
      }
    });

    await page.goto("/proxies/tag-categories");

    await expect(page.getByRole("heading", { name: "Tag Categories", exact: true })).toBeVisible({ timeout: 10_000 });
    await expect(page.getByText("pais", { exact: true })).toBeVisible();
    await expect(page.getByText("ar", { exact: true })).toBeVisible();
    await expect(page.getByText("cl", { exact: true })).toBeVisible();
  });

  test("creates a new category", async ({ page }) => {
    await page.route("**/api/v1/proxies/tag-categories", async (route) => {
      if (route.request().method() === "GET") {
        await route.fulfill({ status: 200, headers: { "Content-Type": "application/json" }, body: JSON.stringify([]) });
      } else if (route.request().method() === "POST") {
        await route.fulfill({ status: 200, headers: { "Content-Type": "application/json" }, body: JSON.stringify("cat-new") });
      } else {
        await route.continue();
      }
    });

    await page.goto("/proxies/tag-categories");
    await expect(page.getByRole("heading", { name: "Tag Categories", exact: true })).toBeVisible({ timeout: 10_000 });
    await page.getByRole("button", { name: "New category" }).first().click();
    await page.getByLabel("Name").fill("funcionalidad");
    await page.getByRole("button", { name: "Save" }).click();

    await expect(page.getByText("Category created", { exact: true })).toBeVisible();
  });
});

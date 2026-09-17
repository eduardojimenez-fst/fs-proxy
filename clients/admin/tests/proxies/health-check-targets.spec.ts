import { expect, test } from "@playwright/test";
import { seedAuthedSession, TEST_USER } from "../helpers/auth-seed";
import { installAdminShellMocks, ADMIN_PERMS } from "../helpers/shell-mocks";

const TARGETS = [
  {
    id: "h1",
    name: "Mercado Publico",
    testUrl: "https://www.mercadopublico.cl",
    expectedStatusCode: 200,
    expectedBodyKeyword: "licitacion",
    timeoutMs: 8000,
  },
  { id: "h2", name: "Generic", testUrl: "https://example.com", expectedStatusCode: null, expectedBodyKeyword: null, timeoutMs: 5000 },
];

const TAGS = [
  { id: "t1", name: "pais:cl", policyProfileId: null, policyProfileName: null, healthCheckTargetId: "h1", healthCheckTargetName: "Mercado Publico" },
  { id: "t2", name: "uso:batch", policyProfileId: null, policyProfileName: null, healthCheckTargetId: null, healthCheckTargetName: null },
];

test.beforeEach(async ({ page }) => {
  await seedAuthedSession(page, { ...TEST_USER, permissions: [...ADMIN_PERMS] });
  await installAdminShellMocks(page);
  await page.route("**/api/v1/proxies/tags", async (route) => {
    await route.fulfill({ status: 200, headers: { "Content-Type": "application/json" }, body: JSON.stringify(TAGS) });
  });
});

test.describe("health check targets", () => {
  test("lists targets with their expectations", async ({ page }) => {
    await page.route("**/api/v1/proxies/health-check-targets", async (route) => {
      await route.fulfill({ status: 200, headers: { "Content-Type": "application/json" }, body: JSON.stringify(TARGETS) });
    });

    await page.goto("/proxies/health-check-targets");

    await expect(page.getByRole("heading", { name: "Health Check Targets", exact: true })).toBeVisible({ timeout: 10_000 });
    await expect(page.getByText('200 · "licitacion" · 8000ms', { exact: true })).toBeVisible();
    // A target with no expected status documents the implicit rule rather than showing blank.
    await expect(page.getByText("2xx–3xx · 5000ms", { exact: true })).toBeVisible();
  });

  test("explains the default-target fallback when nothing is assigned", async ({ page }) => {
    await page.route("**/api/v1/proxies/health-check-targets", async (route) => {
      await route.fulfill({ status: 200, headers: { "Content-Type": "application/json" }, body: JSON.stringify([]) });
    });
    await page.route("**/api/v1/proxies/tags", async (route) => {
      const noneAssigned = TAGS.map((t) => ({ ...t, healthCheckTargetId: null, healthCheckTargetName: null }));
      await route.fulfill({ status: 200, headers: { "Content-Type": "application/json" }, body: JSON.stringify(noneAssigned) });
    });

    await page.goto("/proxies/health-check-targets");

    await expect(page.getByText("No custom health check targets.", { exact: true })).toBeVisible({ timeout: 10_000 });
    await expect(page.getByText(/probed against the configured default URL/)).toBeVisible();
  });

  test("assigns a target to a tag", async ({ page }) => {
    await page.route("**/api/v1/proxies/health-check-targets", async (route) => {
      await route.fulfill({ status: 200, headers: { "Content-Type": "application/json" }, body: JSON.stringify(TARGETS) });
    });
    let assignedUrl = "";
    await page.route("**/api/v1/proxies/tags/*/health-check-target/*", async (route) => {
      assignedUrl = route.request().url();
      await route.fulfill({ status: 204, body: "" });
    });

    await page.goto("/proxies/health-check-targets");
    await expect(page.getByRole("heading", { name: "Health Check Targets", exact: true })).toBeVisible({ timeout: 10_000 });

    await page.getByTestId("target-assign-uso:batch").getByRole("button").click();
    await page.getByRole("menuitem", { name: "Generic", exact: true }).click();

    await expect.poll(() => assignedUrl).toContain("/tags/t2/health-check-target/h2");
  });

  test("sends null rather than an empty string for a blank expected status", async ({ page }) => {
    await page.route("**/api/v1/proxies/health-check-targets", async (route) => {
      if (route.request().method() === "POST") {
        await route.fulfill({ status: 200, headers: { "Content-Type": "application/json" }, body: JSON.stringify("new-id") });
        return;
      }
      await route.fulfill({ status: 200, headers: { "Content-Type": "application/json" }, body: JSON.stringify(TARGETS) });
    });

    await page.goto("/proxies/health-check-targets");
    await expect(page.getByRole("heading", { name: "Health Check Targets", exact: true })).toBeVisible({ timeout: 10_000 });

    await page.getByRole("button", { name: "New target" }).click();
    await page.getByLabel("Name").fill("Only connectivity");
    await page.getByLabel("Test URL").fill("https://example.org/ping");

    const postPromise = page.waitForRequest(
      (r) => r.url().includes("/health-check-targets") && r.method() === "POST",
    );
    await page.getByRole("button", { name: "Save", exact: true }).click();
    const body = (await postPromise).postDataJSON();

    expect(body).toMatchObject({ name: "Only connectivity", testUrl: "https://example.org/ping", timeoutMs: 5000 });
    expect(body.expectedStatusCode).toBeNull();
    expect(body.expectedBodyKeyword).toBeNull();
  });
});

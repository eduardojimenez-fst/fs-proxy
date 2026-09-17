import { expect, test } from "@playwright/test";
import { seedAuthedSession, TEST_USER } from "../helpers/auth-seed";
import { installAdminShellMocks, ADMIN_PERMS } from "../helpers/shell-mocks";

const POLICIES = [
  {
    id: "p1",
    name: "Strict — scraping",
    type: "AutoDisableAndRenew",
    failureThreshold: 3,
    windowMinutes: 30,
    minDistinctReporters: 2,
  },
  { id: "p2", name: "Lenient", type: "AutoDisable", failureThreshold: 10, windowMinutes: 120, minDistinctReporters: 1 },
];

const TAGS = [
  { id: "t1", name: "pais:cl", policyProfileId: "p1", policyProfileName: "Strict — scraping", healthCheckTargetId: null, healthCheckTargetName: null },
  { id: "t2", name: "uso:batch", policyProfileId: null, policyProfileName: null, healthCheckTargetId: null, healthCheckTargetName: null },
];

test.beforeEach(async ({ page }) => {
  await seedAuthedSession(page, { ...TEST_USER, permissions: [...ADMIN_PERMS] });
  await installAdminShellMocks(page);
  await page.route("**/api/v1/proxies/tags", async (route) => {
    await route.fulfill({ status: 200, headers: { "Content-Type": "application/json" }, body: JSON.stringify(TAGS) });
  });
});

test.describe("policies", () => {
  test("lists policies with their thresholds and the tags they affect", async ({ page }) => {
    await page.route("**/api/v1/proxies/policies", async (route) => {
      await route.fulfill({ status: 200, headers: { "Content-Type": "application/json" }, body: JSON.stringify(POLICIES) });
    });

    await page.goto("/proxies/policies");

    await expect(page.getByRole("heading", { name: "Policies", exact: true })).toBeVisible({ timeout: 10_000 });
    await expect(page.getByText("Strict — scraping", { exact: true }).first()).toBeVisible();
    await expect(page.getByText("3 fails / 30m / 2 rep", { exact: true })).toBeVisible();
    // p1 is assigned to pais:cl; p2 is assigned to nothing and must say so.
    await expect(page.getByText("— unused", { exact: true })).toBeVisible();
  });

  test("warns that nothing is enforced when no policy exists", async ({ page }) => {
    await page.route("**/api/v1/proxies/policies", async (route) => {
      await route.fulfill({ status: 200, headers: { "Content-Type": "application/json" }, body: JSON.stringify([]) });
    });

    await page.goto("/proxies/policies");

    await expect(page.getByText("No policy profiles yet.", { exact: true })).toBeVisible({ timeout: 10_000 });
    await expect(page.getByText(/no proxy is ever disabled automatically/)).toBeVisible();
  });

  test("assigns a policy to a tag", async ({ page }) => {
    await page.route("**/api/v1/proxies/policies", async (route) => {
      await route.fulfill({ status: 200, headers: { "Content-Type": "application/json" }, body: JSON.stringify(POLICIES) });
    });
    let assignedUrl = "";
    await page.route("**/api/v1/proxies/tags/*/policy/*", async (route) => {
      assignedUrl = route.request().url();
      await route.fulfill({ status: 204, body: "" });
    });

    await page.goto("/proxies/policies");
    await expect(page.getByRole("heading", { name: "Policies", exact: true })).toBeVisible({ timeout: 10_000 });

    await page.getByTestId("policy-assign-uso:batch").getByRole("button").click();
    await page.getByRole("menuitem", { name: "Lenient", exact: true }).click();

    await expect.poll(() => assignedUrl).toContain("/tags/t2/policy/p2");
  });

  test("unassigns a policy from a tag via the placeholder option", async ({ page }) => {
    await page.route("**/api/v1/proxies/policies", async (route) => {
      await route.fulfill({ status: 200, headers: { "Content-Type": "application/json" }, body: JSON.stringify(POLICIES) });
    });
    let method = "";
    let url = "";
    await page.route("**/api/v1/proxies/tags/*/policy", async (route) => {
      method = route.request().method();
      url = route.request().url();
      await route.fulfill({ status: 204, body: "" });
    });

    await page.goto("/proxies/policies");
    await expect(page.getByRole("heading", { name: "Policies", exact: true })).toBeVisible({ timeout: 10_000 });

    await page.getByTestId("policy-assign-pais:cl").getByRole("button").click();
    await page.getByRole("menuitem", { name: "No policy", exact: true }).click();

    await expect.poll(() => method).toBe("DELETE");
    expect(url).toContain("/tags/t1/policy");
  });

  test("creates a policy and previews what it will do", async ({ page }) => {
    await page.route("**/api/v1/proxies/policies", async (route) => {
      if (route.request().method() === "POST") {
        await route.fulfill({ status: 200, headers: { "Content-Type": "application/json" }, body: JSON.stringify("new-id") });
        return;
      }
      await route.fulfill({ status: 200, headers: { "Content-Type": "application/json" }, body: JSON.stringify(POLICIES) });
    });

    await page.goto("/proxies/policies");
    await expect(page.getByRole("heading", { name: "Policies", exact: true })).toBeVisible({ timeout: 10_000 });

    await page.getByRole("button", { name: "New policy" }).click();
    await expect(page.getByRole("dialog")).toBeVisible();

    await page.getByLabel("Name").fill("Nightly");
    await page.getByLabel("Failures").fill("7");

    // The plain-language preview must track the inputs, since the numbers alone are opaque.
    await expect(page.getByRole("dialog").getByText(/Disable a proxy after/)).toContainText("7");

    const postPromise = page.waitForRequest(
      (r) => r.url().includes("/api/v1/proxies/policies") && r.method() === "POST",
    );
    await page.getByRole("button", { name: "Save", exact: true }).click();
    const post = await postPromise;
    expect(post.postDataJSON()).toMatchObject({ name: "Nightly", failureThreshold: 7 });
  });
});

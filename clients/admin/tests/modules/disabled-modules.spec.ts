import { expect, test, type Locator, type Page } from "@playwright/test";
import { seedAuthedSession, TEST_USER } from "../helpers/auth-seed";
import {
  ADMIN_PERMS,
  installAdminShellMocks,
  withDisabledModules,
} from "../helpers/shell-mocks";

// `disabledModules` in /config.json hides whole modules from this app's UI per
// deployment. Every test below seeds the FULL root permission set, so anything
// that disappears can only have disappeared because of the module gate — never
// because of a missing permission.
//
// The load-bearing case is the first one: the failure mode worth engineering
// against is a config *without* the key quietly hiding a module in production.

const sidebar = (page: Page) => page.getByRole("navigation", { name: "Primary" });

/**
 * Expand a sidebar accordion section.
 *
 * Collapsed sections keep their items in the DOM but mark them `aria-hidden`,
 * so a role query returns 0 either way — asserting a hidden item without
 * expanding first would pass whether or not the module gate works.
 */
async function openSection(scope: Locator, caption: string): Promise<void> {
  const button = scope.getByRole("button", { name: caption });
  await expect(button).toBeVisible();
  if ((await button.getAttribute("aria-expanded")) !== "true") await button.click();
}

test.beforeEach(async ({ page }) => {
  await installAdminShellMocks(page, ADMIN_PERMS);
});

test.describe("disabledModules — default is visible", () => {
  test("with no config override at all, every Operations item shows", async ({ page }) => {
    await seedAuthedSession(page, TEST_USER);
    await page.goto("/");

    const nav = sidebar(page);
    await openSection(nav, "Operations");
    await expect(nav.getByRole("link", { name: "Billing" })).toBeVisible();
    await expect(nav.getByRole("link", { name: "Webhooks" })).toBeVisible();
    await expect(nav.getByRole("link", { name: "Audits" })).toBeVisible();
    await expect(nav.getByRole("link", { name: "Health" })).toBeVisible();
  });

  test("an empty list hides nothing", async ({ page }) => {
    await withDisabledModules(page, []);
    await seedAuthedSession(page, TEST_USER);
    await page.goto("/");

    const nav = sidebar(page);
    await openSection(nav, "Operations");
    await expect(nav.getByRole("link", { name: "Billing" })).toBeVisible();
  });

  test("an unknown key is ignored rather than hiding anything", async ({ page }) => {
    await withDisabledModules(page, ["biling", "not-a-module"]);
    await seedAuthedSession(page, TEST_USER);
    await page.goto("/");

    const nav = sidebar(page);
    await openSection(nav, "Operations");
    await expect(nav.getByRole("link", { name: "Billing" })).toBeVisible();
  });
});

test.describe("disabledModules — hiding", () => {
  test("hides only the named module, leaving its siblings", async ({ page }) => {
    await withDisabledModules(page, ["billing"]);
    await seedAuthedSession(page, TEST_USER);
    await page.goto("/");

    const nav = sidebar(page);
    await openSection(nav, "Operations");
    await expect(nav.getByRole("link", { name: "Billing" })).toHaveCount(0);
    await expect(nav.getByRole("link", { name: "Webhooks" })).toBeVisible();
    await expect(nav.getByRole("link", { name: "Audits" })).toBeVisible();
  });

  test("accepts a comma-separated string (the envsubst/Docker form)", async ({ page }) => {
    // Deliberately includes a space — envsubst-rendered values get hand-edited.
    await withDisabledModules(page, "billing, webhooks");
    await seedAuthedSession(page, TEST_USER);
    await page.goto("/");

    const nav = sidebar(page);
    await openSection(nav, "Operations");
    await expect(nav.getByRole("link", { name: "Billing" })).toHaveCount(0);
    await expect(nav.getByRole("link", { name: "Webhooks" })).toHaveCount(0);
    await expect(nav.getByRole("link", { name: "Audits" })).toBeVisible();
  });

  test("an empty string hides nothing (unset FSH_DISABLED_MODULES)", async ({ page }) => {
    await withDisabledModules(page, "");
    await seedAuthedSession(page, TEST_USER);
    await page.goto("/");

    const nav = sidebar(page);
    await openSection(nav, "Operations");
    await expect(nav.getByRole("link", { name: "Billing" })).toBeVisible();
  });

  // The fs-proxy shape: a single-tenant app with no billing/webhooks/audit story.
  // Two whole sections go; Identity survives because Users/Roles aren't hidable.
  test("emptying a section drops the whole accordion", async ({ page }) => {
    await withDisabledModules(page, [
      "billing",
      "webhooks",
      "auditing",
      "health",
      "multitenancy",
    ]);
    await seedAuthedSession(page, TEST_USER);
    await page.goto("/");

    const nav = sidebar(page);
    await expect(nav.getByRole("button", { name: "Operations" })).toHaveCount(0);
    await expect(nav.getByRole("button", { name: "Tenants" })).toHaveCount(0);
    // Identity keeps Users + Roles, which have no module key.
    await openSection(nav, "Identity");
    await expect(nav.getByRole("link", { name: "Users" })).toBeVisible();
    await expect(nav.getByRole("link", { name: "Roles" })).toBeVisible();
    // Impersonation IS hidable, but wasn't hidden here.
    await expect(nav.getByRole("link", { name: "Impersonation" })).toBeVisible();
    // The overview keeps only the one ungated pivot card.
    await expect(page.locator("main").getByRole("link", { name: /Users/ })).toBeVisible();
    await expect(page.locator("main").getByRole("link", { name: /Tenants/ })).toHaveCount(0);
  });

  test("the mobile drawer hides the same items", async ({ page }) => {
    await withDisabledModules(page, ["billing"]);
    await seedAuthedSession(page, TEST_USER);
    await page.setViewportSize({ width: 480, height: 900 });
    await page.goto("/");

    await page.getByRole("button", { name: "Open navigation menu" }).click();
    const drawer = page.getByRole("dialog", { name: "Primary navigation" });
    await expect(drawer).toBeVisible();
    await openSection(drawer, "Operations");
    await expect(drawer.getByRole("link", { name: "Billing" })).toHaveCount(0);
    await expect(drawer.getByRole("link", { name: "Webhooks" })).toBeVisible();
  });
});

test.describe("disabledModules — routes", () => {
  test("a hidden module's deep link 404s outside the shell", async ({ page }) => {
    await withDisabledModules(page, ["billing"]);
    await seedAuthedSession(page, TEST_USER);
    await page.goto("/billing/invoices");

    // The route is absent, not gated, so it falls through to the catch-all `*`
    // NotFound — which sits OUTSIDE AppShell. No sidebar is the tell that we
    // got a real 404 and not a 404 painted inside the app shell.
    await expect(page.getByText(/not found/i).first()).toBeVisible();
    await expect(page.getByRole("navigation", { name: "Primary" })).toHaveCount(0);
  });

  test("the same deep link works while the module is enabled", async ({ page }) => {
    await withDisabledModules(page, []);
    await seedAuthedSession(page, TEST_USER);
    await page.goto("/billing/invoices");

    await expect(page.getByRole("navigation", { name: "Primary" })).toBeVisible();
  });
});

test.describe("disabledModules — surfaces beyond the nav", () => {
  test("the overview drops the pivot cards that point into a hidden module", async ({ page }) => {
    await withDisabledModules(page, ["billing"]);
    await seedAuthedSession(page, TEST_USER);
    await page.goto("/");

    // Scoped to <main>: the sidebar lives outside it, so this isolates the cards.
    const cards = page.locator("main").getByRole("link");
    await expect(cards.filter({ hasText: "Billing" })).toHaveCount(0);
    await expect(cards.filter({ hasText: "Invoices" })).toHaveCount(0);
    await expect(cards.filter({ hasText: "Tenants" })).toBeVisible();
    await expect(cards.filter({ hasText: "Users" })).toBeVisible();
  });

  test("hiding billing fires no billing requests from the overview", async ({ page }) => {
    const billingCalls: string[] = [];
    page.on("request", (r) => {
      if (/\/api\/v1\/billing\//.test(r.url())) billingCalls.push(r.url());
    });

    await withDisabledModules(page, ["billing"]);
    await seedAuthedSession(page, TEST_USER);
    await page.goto("/");
    await expect(page.locator("main").getByRole("link", { name: /Tenants/ })).toBeVisible();

    expect(billingCalls).toEqual([]);
  });
});

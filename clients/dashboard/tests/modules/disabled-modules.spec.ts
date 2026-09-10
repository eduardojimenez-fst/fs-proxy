import { expect, test, type Locator, type Page } from "@playwright/test";
import { seedAuthedSession, TEST_USER } from "../helpers/auth-seed";
import { mockJsonResponse } from "../helpers/api-mocks";
import { installShellMocks, withDisabledModules } from "../helpers/shell-mocks";

// `disabledModules` in /config.json hides whole modules from this app's UI per
// deployment. Every test grants a broad permission set, so anything that
// disappears can only have disappeared because of the module gate.
//
// The load-bearing case is the first one: the failure mode worth engineering
// against is a config *without* the key quietly hiding a module in production.

const ALL_PERMS = [
  "Permissions.Billing.View",
  "Permissions.AuditTrails.View",
  "Permissions.Sessions.ViewAll",
  "Permissions.Chat.Channels.View",
  "Permissions.Files.Upload",
  "Permissions.Users.Update",
  "Permissions.Roles.Update",
  "Permissions.Groups.Update",
  "Permissions.Catalog.Products.View",
  "Permissions.Catalog.Brands.View",
  "Permissions.Catalog.Categories.View",
  "Permissions.Tickets.View",
  // Trash's nav entry is gated on `anyPerm: ALL_TRASH_PERMISSIONS` (the *Restore*
  // permissions, not the View ones) — without these the permission gate hides it
  // before the module gate is ever consulted.
  "Permissions.Catalog.Products.Restore",
  "Permissions.Tickets.Restore",
  "Permissions.Files.ViewTrash",
];

const sidebar = (page: Page) => page.getByRole("navigation").first();

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
  await installShellMocks(page);
  await mockJsonResponse(page, "**/api/v1/identity/permissions", ALL_PERMS);
});

test.describe("disabledModules — default is visible", () => {
  test("with no config override at all, the module sections show", async ({ page }) => {
    await seedAuthedSession(page, TEST_USER);
    await page.goto("/");

    const nav = sidebar(page);
    await expect(nav.getByRole("button", { name: "Operations" })).toBeVisible();
    await expect(nav.getByRole("button", { name: "Catalog" })).toBeVisible();
    await expect(nav.getByRole("button", { name: "Helpdesk" })).toBeVisible();
    await expect(nav.getByRole("link", { name: "Chat" })).toBeVisible();
  });

  test("an empty list hides nothing", async ({ page }) => {
    await withDisabledModules(page, []);
    await seedAuthedSession(page, TEST_USER);
    await page.goto("/");

    await expect(sidebar(page).getByRole("button", { name: "Catalog" })).toBeVisible();
  });
});

test.describe("disabledModules — hiding", () => {
  test("hides a top-level item and leaves its siblings", async ({ page }) => {
    await withDisabledModules(page, ["chat"]);
    await seedAuthedSession(page, TEST_USER);
    await page.goto("/");

    const nav = sidebar(page);
    await expect(nav.getByRole("link", { name: "Chat" })).toHaveCount(0);
    await expect(nav.getByRole("link", { name: "My Files" })).toBeVisible();
  });

  test("emptying a section drops the whole accordion", async ({ page }) => {
    await withDisabledModules(page, ["catalog"]);
    await seedAuthedSession(page, TEST_USER);
    await page.goto("/");

    const nav = sidebar(page);
    await expect(nav.getByRole("button", { name: "Catalog" })).toHaveCount(0);
    await expect(nav.getByRole("button", { name: "Operations" })).toBeVisible();
  });

  test("hides the billing items but keeps Live activity in Operations", async ({ page }) => {
    await withDisabledModules(page, ["billing"]);
    await seedAuthedSession(page, TEST_USER);
    await page.goto("/");

    const nav = sidebar(page);
    await openSection(nav, "Operations");
    await expect(nav.getByRole("link", { name: "Subscription" })).toHaveCount(0);
    await expect(nav.getByRole("link", { name: "Invoices" })).toHaveCount(0);
    await expect(nav.getByRole("link", { name: "Live activity" })).toBeVisible();
  });

  test("accepts a comma-separated string (the envsubst/Docker form)", async ({ page }) => {
    // Deliberately includes a space — envsubst-rendered values get hand-edited.
    await withDisabledModules(page, "catalog, tickets");
    await seedAuthedSession(page, TEST_USER);
    await page.goto("/");

    const nav = sidebar(page);
    await expect(nav.getByRole("button", { name: "Catalog" })).toHaveCount(0);
    await expect(nav.getByRole("button", { name: "Helpdesk" })).toHaveCount(0);
    await expect(nav.getByRole("button", { name: "Operations" })).toBeVisible();
  });

  test("an empty string hides nothing (unset FSH_DISABLED_MODULES)", async ({ page }) => {
    await withDisabledModules(page, "");
    await seedAuthedSession(page, TEST_USER);
    await page.goto("/");

    await expect(sidebar(page).getByRole("button", { name: "Catalog" })).toBeVisible();
  });
});

test.describe("disabledModules — anyModule (Trash)", () => {
  test("Trash survives while one of its modules is still enabled", async ({ page }) => {
    await withDisabledModules(page, ["catalog"]);
    await seedAuthedSession(page, TEST_USER);
    await page.goto("/");

    const nav = sidebar(page);
    await openSection(nav, "System");
    await expect(nav.getByRole("link", { name: "Trash" })).toBeVisible();
  });

  test("Trash disappears once every module it fronts is hidden", async ({ page }) => {
    await withDisabledModules(page, ["catalog", "tickets", "files"]);
    await seedAuthedSession(page, TEST_USER);
    await page.goto("/");

    const nav = sidebar(page);
    await openSection(nav, "System");
    await expect(nav.getByRole("link", { name: "Trash" })).toHaveCount(0);
    await expect(nav.getByRole("link", { name: "Health" })).toBeVisible();
  });
});

test.describe("disabledModules — routes", () => {
  test("a hidden module's deep link 404s outside the shell", async ({ page }) => {
    await withDisabledModules(page, ["billing"]);
    await seedAuthedSession(page, TEST_USER);
    await page.goto("/subscription");

    // The route is absent, not gated, so it falls through to the catch-all `*`
    // NotFound — which sits OUTSIDE AppShell. No sidebar is the tell that we
    // got a real 404 and not a 404 painted inside the app shell.
    await expect(page.getByText(/not found/i).first()).toBeVisible();
    await expect(page.getByRole("navigation")).toHaveCount(0);
  });

  test("the same deep link works while the module is enabled", async ({ page }) => {
    await withDisabledModules(page, []);
    await seedAuthedSession(page, TEST_USER);
    await page.goto("/subscription");

    await expect(sidebar(page)).toBeVisible();
  });
});

test.describe("disabledModules — command palette", () => {
  test("⌘K stops offering a hidden module, in Navigate and Create alike", async ({ page }) => {
    await withDisabledModules(page, ["catalog"]);
    await seedAuthedSession(page, TEST_USER);
    await page.goto("/");

    await page.keyboard.press("ControlOrMeta+k");
    const dialog = page.getByRole("dialog");
    await expect(dialog).toBeVisible();

    await dialog.getByPlaceholder("Type a command or search…").fill("product");
    // "Browse products" (Navigate) and "New product" (Create) both belong to
    // catalog — the palette duplicates the nav graph, so both must be gone.
    await expect(dialog.getByText(/product/i)).toHaveCount(0);
  });

  test("⌘K still offers the module when it's enabled", async ({ page }) => {
    await withDisabledModules(page, []);
    await seedAuthedSession(page, TEST_USER);
    await page.goto("/");

    await page.keyboard.press("ControlOrMeta+k");
    const dialog = page.getByRole("dialog");
    await expect(dialog).toBeVisible();
    await dialog.getByPlaceholder("Type a command or search…").fill("product");
    await expect(dialog.getByText(/product/i).first()).toBeVisible();
  });
});

test.describe("disabledModules — the overview page", () => {
  test("renders with billing hidden — no crash, no billing calls", async ({ page }) => {
    // Uncaught exceptions only. `console.error` is useless here: installShellMocks
    // aborts the SSE/SignalR transports on purpose, so the console always carries
    // negotiation failures. A crashed render surfaces as a pageerror.
    const errors: string[] = [];
    const billingCalls: string[] = [];
    page.on("pageerror", (e) => errors.push(String(e)));
    page.on("request", (r) => {
      if (/\/api\/v1\/billing\//.test(r.url())) billingCalls.push(r.url());
    });

    await withDisabledModules(page, ["billing"]);
    await seedAuthedSession(page, TEST_USER);
    await page.goto("/");

    // The shell must still mount — this is the assertion that would catch a
    // naive gate on SseProvider/RealtimeProvider (Topbar calls useSseStatus()
    // unconditionally, so gating the provider white-screens the app).
    await expect(sidebar(page)).toBeVisible();
    await expect(page.locator("main").getByRole("link", { name: "Subscription" })).toHaveCount(0);

    expect(billingCalls).toEqual([]);
    expect(errors).toEqual([]);
  });
});

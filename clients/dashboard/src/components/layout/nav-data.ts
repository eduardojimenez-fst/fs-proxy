import {
  Activity,
  CreditCard,
  FolderOpen,
  FolderTree,
  HeartPulse,
  LayoutDashboard,
  MessageCircle,
  Package,
  Receipt,
  ScrollText,
  Settings,
  ShieldCheck,
  Tags,
  Ticket,
  Trash2,
  Users,
  UsersRound,
  Wallet,
  Wifi,
} from "lucide-react";
import { ALL_TRASH_PERMISSIONS } from "@/lib/trash-permissions";
import { areAnyModulesEnabled, isModuleEnabled, type ModuleKey } from "@/lib/modules";

export type NavSpec = {
  to: string;
  label: string;
  icon: React.ComponentType<{ className?: string }>;
  /**
   * Permission required to see this item. Items without a `perm` are visible to
   * every authenticated tenant user; gated items are hidden when the current
   * user (or impersonated user) lacks the permission, so they never land on a
   * page the API will reject with 403.
   */
  perm?: string;
  /**
   * Visible only if the user holds *at least one* of these permissions. Use for
   * an item that fronts several independently-gated sub-views (e.g. Trash, whose
   * tabs each require a different permission) — the entry should show as long as
   * the user can reach any one of them. Combined with `perm` via AND.
   */
  anyPerm?: readonly string[];
  /**
   * UI module this item belongs to. Hidden when the deployment lists the key in
   * `disabledModules` (config.json), independently of permissions — permissions
   * are per-role, this is per-deployment. Items without a `module` are never
   * hidden this way. See lib/modules.ts.
   */
  module?: ModuleKey;
  /**
   * Visible while *at least one* of these modules is enabled. For an entry that
   * fronts several modules at once (Trash, whose tabs belong to different
   * modules). Same OR shape as `anyPerm`; combined with `module` via AND.
   */
  anyModule?: readonly ModuleKey[];
};

export type NavSection = {
  id: string;
  caption: string;
  /** Section-level icon used as a fallback when the sidebar is
   *  collapsed and the section is rendered as a stack of item icons. */
  icon: React.ComponentType<{ className?: string }>;
  items: NavSpec[];
};

// Top-level items live OUTSIDE any section. Overview opens the app;
// Settings is account-scoped and lives at the very bottom.
export const topNavTop: NavSpec[] = [
  { to: "/", label: "Overview", icon: LayoutDashboard },
  // Each gate mirrors the permission the page's primary list endpoint enforces
  // server-side (Chat → channels list, Files → /files/mine). Same convention
  // as trash-permissions.ts: if the endpoint's permission changes, mirror it.
  {
    to: "/chat",
    label: "Chat",
    icon: MessageCircle,
    perm: "Permissions.Chat.Channels.View",
    module: "chat",
  },
  {
    to: "/files",
    label: "My Files",
    icon: FolderOpen,
    perm: "Permissions.Files.Upload",
    module: "files",
  },
];

export const topNavBottom: NavSpec[] = [
  { to: "/settings", label: "Settings", icon: Settings },
];

// Section accordion. Single-select — only one section open at a time.
export const sections: NavSection[] = [
  {
    id: "operations",
    caption: "Operations",
    icon: Activity,
    items: [
      // Live activity is SSE-backed; the stream is auth-only (no permission), so no gate.
      { to: "/activity", label: "Live activity", icon: Activity, module: "activity" },
      {
        to: "/subscription",
        label: "Subscription",
        icon: CreditCard,
        perm: "Permissions.Billing.View",
        module: "billing",
      },
      {
        to: "/wallet",
        label: "WhatsApp wallet",
        icon: Wallet,
        perm: "Permissions.Billing.View",
        module: "billing",
      },
      {
        to: "/invoices",
        label: "Invoices",
        icon: Receipt,
        perm: "Permissions.Billing.View",
        module: "billing",
      },
    ],
  },
  {
    id: "catalog",
    caption: "Catalog",
    icon: Package,
    items: [
      {
        to: "/catalog/products",
        label: "Products",
        icon: Package,
        perm: "Permissions.Catalog.Products.View",
        module: "catalog",
      },
      {
        to: "/catalog/brands",
        label: "Brands",
        icon: Tags,
        perm: "Permissions.Catalog.Brands.View",
        module: "catalog",
      },
      {
        to: "/catalog/categories",
        label: "Categories",
        icon: FolderTree,
        perm: "Permissions.Catalog.Categories.View",
        module: "catalog",
      },
    ],
  },
  {
    id: "helpdesk",
    caption: "Helpdesk",
    icon: Ticket,
    items: [
      {
        to: "/tickets",
        label: "Tickets",
        icon: Ticket,
        perm: "Permissions.Tickets.View",
        module: "tickets",
      },
    ],
  },
  {
    id: "identity",
    caption: "Identity",
    icon: Users,
    items: [
      // Gate the identity-management pages on a manage permission (not View): View Users/Roles/Groups
      // are IsBasic so every member holds them (the chat/user picker relies on Users.View), but only
      // managers should see these admin pages. Basic lacks the *.Update perms, so the items hide for them.
      { to: "/identity/users", label: "Users", icon: Users, perm: "Permissions.Users.Update" },
      { to: "/identity/roles", label: "Roles", icon: ShieldCheck, perm: "Permissions.Roles.Update" },
      { to: "/identity/groups", label: "Groups", icon: UsersRound, perm: "Permissions.Groups.Update" },
    ],
  },
  {
    id: "system",
    caption: "System",
    icon: HeartPulse,
    items: [
      // Health hits the anonymous /health/ready probe — visible to everyone.
      { to: "/system/health", label: "Health", icon: HeartPulse, module: "health" },
      {
        to: "/system/audits",
        label: "Audit trail",
        icon: ScrollText,
        perm: "Permissions.AuditTrails.View",
        module: "auditing",
      },
      { to: "/system/sessions", label: "Sessions", icon: Wifi, perm: "Permissions.Sessions.ViewAll" },
      // Trash fronts five tabs, each gated on a different resource's restore /
      // view-trash permission. Show the entry if the user can reach any tab; the
      // page hides the individual tabs they can't (see trash-permissions.ts).
      {
        to: "/system/trash",
        label: "Trash",
        icon: Trash2,
        anyPerm: ALL_TRASH_PERMISSIONS,
        anyModule: ["catalog", "tickets", "files"],
      },
    ],
  },
];

/** True when the item passes both gates: the deployment's enabled modules
 *  (`module` AND `anyModule`) and the user's permissions (`perm` AND one of
 *  `anyPerm`). Ungated items are always visible. Module checks come first —
 *  a module the deployment turned off is hidden regardless of permissions. */
function isNavItemVisible(item: NavSpec, permissions: readonly string[]): boolean {
  if (!isModuleEnabled(item.module)) return false;
  if (!areAnyModulesEnabled(item.anyModule)) return false;
  if (item.perm && !permissions.includes(item.perm)) return false;
  if (item.anyPerm && !item.anyPerm.some((p) => permissions.includes(p))) return false;
  return true;
}

/** Drop items the user can't access, then drop any section left empty. */
export function visibleSections(permissions: readonly string[]): NavSection[] {
  return sections
    .map((s) => ({ ...s, items: s.items.filter((i) => isNavItemVisible(i, permissions)) }))
    .filter((s) => s.items.length > 0);
}

/** Filter a flat nav list (top/bottom) by permission. */
export function visibleItems(items: NavSpec[], permissions: readonly string[]): NavSpec[] {
  return items.filter((i) => isNavItemVisible(i, permissions));
}

/** Find the section whose items contain the given path (best prefix match). */
export function findSectionForPath(pathname: string): string | null {
  let bestId: string | null = null;
  let bestLen = 0;
  for (const s of sections) {
    for (const item of s.items) {
      if (
        (item.to === "/" && pathname === "/") ||
        (item.to !== "/" && pathname.startsWith(item.to))
      ) {
        if (item.to.length > bestLen) {
          bestLen = item.to.length;
          bestId = s.id;
        }
      }
    }
  }
  return bestId;
}

import {
  Activity,
  Building2,
  Cloud,
  LayoutDashboard,
  Network,
  Receipt,
  ScrollText,
  Server,
  Settings,
  ShieldCheck,
  Tag,
  UserCog,
  UsersRound,
  Webhook,
  type LucideIcon,
} from "lucide-react";
import {
  AuditingPermissions,
  BillingPermissions,
  IdentityPermissions,
  MultitenancyPermissions,
  ProxiesPermissions,
  WebhooksPermissions,
} from "@/lib/permissions";
import { isModuleEnabled, type ModuleKey } from "@/lib/modules";

/** A single nav destination — label, route, icon, optional perm guard. */
export type NavSpec = {
  to: string;
  label: string;
  icon: LucideIcon;
  /** One or more permissions the user must hold to see this item. */
  perms?: readonly string[];
  /**
   * UI module this item belongs to. Hidden when the deployment lists the key in
   * `disabledModules` (config.json), independently of the user's permissions —
   * permissions are per-role, this is per-deployment. Items without a `module`
   * are never hidden this way. See lib/modules.ts.
   */
  module?: ModuleKey;
};

/** A collapsible section that groups related NavSpecs. */
export type NavSection = {
  id: string;
  caption: string;
  icon: LucideIcon;
  items: NavSpec[];
};

// ─── Top-level singletons ────────────────────────────────────────────────────

export const topNavTop: NavSpec[] = [
  { to: "/", label: "Overview", icon: LayoutDashboard },
];

export const topNavBottom: NavSpec[] = [
  { to: "/settings", label: "Settings", icon: Settings },
];

// ─── Section accordions ──────────────────────────────────────────────────────

export const sections: NavSection[] = [
  {
    id: "multitenancy",
    caption: "Tenants",
    icon: Building2,
    items: [
      {
        to: "/tenants",
        label: "Tenants",
        icon: Building2,
        perms: [MultitenancyPermissions.Tenants.View],
        module: "multitenancy",
      },
    ],
  },
  {
    id: "identity",
    caption: "Identity",
    icon: UsersRound,
    items: [
      {
        to: "/users",
        label: "Users",
        icon: UsersRound,
        perms: [IdentityPermissions.Users.View],
      },
      {
        to: "/roles",
        label: "Roles",
        icon: ShieldCheck,
        perms: [IdentityPermissions.Roles.View],
      },
      {
        to: "/impersonation",
        label: "Impersonation",
        icon: UserCog,
        perms: [IdentityPermissions.Impersonation.View],
        module: "impersonation",
      },
    ],
  },
  {
    id: "proxies",
    caption: "Proxies",
    icon: Network,
    items: [
      {
        to: "/proxies",
        label: "Proxies",
        icon: Network,
        perms: [ProxiesPermissions.ProviderAccounts.View],
      },
      {
        to: "/proxies/provider-accounts",
        label: "Provider Accounts",
        icon: Cloud,
        perms: [ProxiesPermissions.ProviderAccounts.View],
      },
      {
        to: "/proxies/manual",
        label: "Manual Proxies",
        icon: Server,
        perms: [ProxiesPermissions.ManualProxies.View],
      },
      {
        to: "/proxies/tag-categories",
        label: "Tag Categories",
        icon: Tag,
        perms: [ProxiesPermissions.Tags.View],
      },
    ],
  },
  {
    id: "operations",
    caption: "Operations",
    icon: Activity,
    items: [
      {
        to: "/billing",
        label: "Billing",
        icon: Receipt,
        perms: [BillingPermissions.View],
        module: "billing",
      },
      {
        to: "/webhooks",
        label: "Webhooks",
        icon: Webhook,
        perms: [WebhooksPermissions.Subscriptions.View],
        module: "webhooks",
      },
      {
        to: "/audits",
        label: "Audits",
        icon: ScrollText,
        perms: [AuditingPermissions.AuditTrails.View],
        module: "auditing",
      },
      {
        to: "/health",
        label: "Health",
        icon: Activity,
        module: "health",
      },
    ],
  },
];

// ─── Helpers ─────────────────────────────────────────────────────────────────

/** Find the section id whose items contain the given path (best prefix match). */
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

/** Returns true when the given NavSpec is the active route. */
export function isNavItemActive(item: NavSpec, pathname: string): boolean {
  if (item.to === "/") return pathname === "/";
  return pathname === item.to || pathname.startsWith(`${item.to}/`);
}

/**
 * Filter nav items by the deployment's enabled modules AND the user's granted
 * permissions. Both sidebar.tsx and mobile-nav.tsx route through here, so the
 * module gate covers desktop and the mobile drawer from this one place; each
 * then drops any section left with no items.
 */
export function filterNavSpec(items: NavSpec[], granted: readonly string[]): NavSpec[] {
  return items.filter((item) => {
    if (!isModuleEnabled(item.module)) return false;
    if (!item.perms || item.perms.length === 0) return true;
    return item.perms.every((p) => granted.includes(p));
  });
}

// ── Legacy flat export (used by sidebar-content & permission gating elsewhere) ──

/** @deprecated Use sections / topNavTop / topNavBottom instead. */
export type NavItem = NavSpec & { matchPrefix?: string };

/** @deprecated Flat list kept only for call-sites still importing NAV_ITEMS. */
export const NAV_ITEMS: NavItem[] = [
  { to: "/", label: "Overview", icon: LayoutDashboard },
  {
    to: "/tenants",
    label: "Tenants",
    icon: Building2,
    matchPrefix: "/tenants",
    perms: [MultitenancyPermissions.Tenants.View],
  },
  {
    to: "/users",
    label: "Users",
    icon: UsersRound,
    matchPrefix: "/users",
    perms: [IdentityPermissions.Users.View],
  },
  {
    to: "/roles",
    label: "Roles",
    icon: ShieldCheck,
    matchPrefix: "/roles",
    perms: [IdentityPermissions.Roles.View],
  },
  {
    to: "/billing",
    label: "Billing",
    icon: Receipt,
    matchPrefix: "/billing",
    perms: [BillingPermissions.View],
  },
  {
    to: "/impersonation",
    label: "Impersonation",
    icon: UserCog,
    matchPrefix: "/impersonation",
    perms: [IdentityPermissions.Impersonation.View],
  },
  {
    to: "/audits",
    label: "Audits",
    icon: ScrollText,
    matchPrefix: "/audits",
    perms: [AuditingPermissions.AuditTrails.View],
  },
  {
    to: "/webhooks",
    label: "Webhooks",
    icon: Webhook,
    matchPrefix: "/webhooks",
    perms: [WebhooksPermissions.Subscriptions.View],
  },
  { to: "/health", label: "Health", icon: Activity, matchPrefix: "/health" },
];

/** @deprecated Use filterNavSpec instead. */
export function filterNavItems(items: NavItem[], grantedPermissions: readonly string[]): NavItem[] {
  return items.filter((item) => {
    if (!item.perms || item.perms.length === 0) return true;
    return item.perms.every((p) => grantedPermissions.includes(p));
  });
}

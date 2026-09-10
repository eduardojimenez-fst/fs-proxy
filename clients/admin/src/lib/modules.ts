import type { RouteObject } from "react-router-dom";
import { env } from "@/env";

/**
 * UI module keys — the unit a deployment can hide.
 *
 * Mostly a mirror of `src/Modules/*` on the backend, plus a few keys
 * (`health`, `activity`) that are UI areas rather than modules. Hiding a key is
 * purely cosmetic: the module stays registered, migrated and permission-guarded
 * server-side, so this is about removing confusion and clutter from an app that
 * doesn't use a given capability — not about security.
 *
 * Kept byte-identical between clients/admin and clients/dashboard (mirrored the
 * same way `lib/permissions.ts` mirrors the server registry) so one
 * FSH_DISABLED_MODULES value can configure both containers. Keys that don't
 * apply to an app are simply unused there.
 *
 * Deliberately absent: `identity`. It underpins login, /settings/*, RouteGuard
 * and the chat user picker (which relies on Users.View), so offering it would
 * invite a config that bricks the app. If only the identity *management* pages
 * ever need hiding, add a narrower `identity-admin` key then.
 *
 * Note `files` gates the "My Files" page and its nav entry, NOT the upload
 * plumbing — the chat composer and components/file/* use @/api/files
 * independently and keep working with `files` disabled.
 */
export const MODULE_KEYS = [
  "activity",
  "auditing",
  "billing",
  "catalog",
  "chat",
  "files",
  "health",
  "impersonation",
  "multitenancy",
  "notifications",
  "tickets",
  "webhooks",
] as const;

export type ModuleKey = (typeof MODULE_KEYS)[number];

/** Type guard used by env.ts to drop unknown keys coming from config.json. */
export function isModuleKey(value: string): value is ModuleKey {
  return (MODULE_KEYS as readonly string[]).includes(value);
}

/**
 * True unless the deployment listed this module in `disabledModules`.
 *
 * An item with no key is always enabled, so `module` is opt-in per nav item /
 * route. Call this at render time (or inside a route element), never at module
 * scope — see the `getRouter` docstring in routes.tsx for why.
 */
export function isModuleEnabled(key?: ModuleKey): boolean {
  if (key === undefined) return true;
  return !env.disabledModules.includes(key);
}

/**
 * True when at least one of the keys is enabled (OR), for a surface that fronts
 * several modules — e.g. Trash, whose tabs each belong to a different module.
 * Mirrors the `anyPerm` convention in the nav data.
 */
export function areAnyModulesEnabled(keys?: readonly ModuleKey[]): boolean {
  if (keys === undefined || keys.length === 0) return true;
  return keys.some((key) => isModuleEnabled(key));
}

/**
 * Spread helper for the route table: yields the routes when the module is
 * enabled, and nothing when it isn't, so a hidden module's paths fall through
 * to the catch-all `*` NotFound route exactly like a URL that never existed.
 *
 *     ...moduleRoutes("billing", [{ path: "billing", element: <Billing /> }]),
 *
 * Because the route is absent rather than gated, the module's lazy chunk is
 * never fetched.
 */
export function moduleRoutes(key: ModuleKey, routes: RouteObject[]): RouteObject[] {
  return isModuleEnabled(key) ? routes : [];
}

/** OR-variant of `moduleRoutes` for routes fronting several modules. */
export function anyModuleRoutes(
  keys: readonly ModuleKey[],
  routes: RouteObject[],
): RouteObject[] {
  return areAnyModulesEnabled(keys) ? routes : [];
}

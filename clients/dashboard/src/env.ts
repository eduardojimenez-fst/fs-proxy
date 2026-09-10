// Runtime config — fetched once at boot from /config.json. See
// clients/admin/src/env.ts for the rationale; the dashboard doesn't
// need dashboardUrl (the handoff is one-way: admin → dashboard).
import { isModuleKey, MODULE_KEYS, type ModuleKey } from "@/lib/modules";

type RuntimeConfig = {
  apiBase: string;
  defaultTenant: string;
  /**
   * Surfaces the "Sign in with a demo account" picker on the login page.
   * A runtime flag (not a build-time VITE_ var) so a single build artifact
   * can be promoted across environments: set "demoMode": true in the
   * staging config.json and false (or omit it) in production.
   */
  demoMode: boolean;
  /** Idle time (ms) before the inactivity warning appears. */
  inactivityIdleMs: number;
  /** Warning-countdown length (ms) before auto sign-out. */
  inactivityWarningMs: number;
  /** Module keys hidden from this app's nav, routes and cross-links. */
  disabledModules: ModuleKey[];
};

// Dashboard defaults: 20 minutes idle, then a 60-second warning.
const DEFAULT_INACTIVITY_IDLE_MS = 20 * 60_000;
const DEFAULT_INACTIVITY_WARNING_MS = 60_000;

/** Accept a positive finite number from config, else fall back. */
function positiveOr(value: unknown, fallback: number): number {
  return typeof value === "number" && Number.isFinite(value) && value > 0 ? value : fallback;
}

/**
 * Module keys the deployment wants hidden from this app's UI.
 *
 * Accepts a JSON array (`["billing","webhooks"]`) or a comma-separated string
 * (`"billing,webhooks"`). Both forms are needed: Terraform writes config.json
 * with `jsonencode`, so it can pass a real array, but the Docker image renders
 * the file with `envsubst`, which can only interpolate a flat string — and the
 * unquoted form would emit invalid JSON when the variable is unset.
 *
 * Absent, `""` and `[]` all mean "hide nothing". That direction matters: the
 * failure mode worth engineering against is a config *without* the key quietly
 * hiding a module in production, so anything unrecognised widens visibility
 * rather than narrowing it. Unknown keys are dropped with a dev-only warning —
 * a typo must not break the boot in production, nor pass unnoticed in dev.
 */
function moduleKeysOr(value: unknown, fallback: ModuleKey[]): ModuleKey[] {
  const raw: unknown[] = Array.isArray(value)
    ? value
    : typeof value === "string"
      ? value.split(",")
      : [];
  if (raw.length === 0) return fallback;

  const keys: ModuleKey[] = [];
  const unknown: string[] = [];
  for (const entry of raw) {
    if (typeof entry !== "string") continue;
    const key = entry.trim().toLowerCase();
    if (key === "") continue;
    if (isModuleKey(key)) {
      if (!keys.includes(key)) keys.push(key);
    } else {
      unknown.push(key);
    }
  }

  if (unknown.length > 0 && import.meta.env.DEV) {
    console.warn(
      "[env] Ignoring unknown disabledModules keys: %s. Valid keys: %s",
      unknown.join(", "),
      MODULE_KEYS.join(", "),
    );
  }
  return keys;
}

let cached: RuntimeConfig | null = null;

export async function loadRuntimeConfig(): Promise<void> {
  if (cached !== null) return;
  const res = await fetch("/config.json", { cache: "no-store" });
  if (!res.ok) {
    throw new Error(`Failed to load /config.json: ${res.status} ${res.statusText}`);
  }
  const cfg = (await res.json()) as Partial<RuntimeConfig>;
  cached = {
    apiBase: (cfg.apiBase ?? "").replace(/\/$/, ""),
    defaultTenant: cfg.defaultTenant ?? "root",
    demoMode: cfg.demoMode ?? false,
    inactivityIdleMs: positiveOr(cfg.inactivityIdleMs, DEFAULT_INACTIVITY_IDLE_MS),
    inactivityWarningMs: positiveOr(cfg.inactivityWarningMs, DEFAULT_INACTIVITY_WARNING_MS),
    disabledModules: moduleKeysOr(cfg.disabledModules, []),
  };
}

function get(): RuntimeConfig {
  if (cached === null) {
    throw new Error(
      "Runtime config not loaded. main.tsx must await loadRuntimeConfig() before mounting React.",
    );
  }
  return cached;
}

export const env = {
  get apiBase(): string { return get().apiBase; },
  get defaultTenant(): string { return get().defaultTenant; },
  get demoMode(): boolean { return get().demoMode; },
  get inactivityIdleMs(): number { return get().inactivityIdleMs; },
  get inactivityWarningMs(): number { return get().inactivityWarningMs; },
  get disabledModules(): ModuleKey[] { return get().disabledModules; },
};

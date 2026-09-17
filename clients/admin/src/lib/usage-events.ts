import type { UsageEventOutcome } from "@/api/proxy-usage-events";

/** Shared by the per-proxy activity dialog and the fleet-wide feed so both read the same. */
export function outcomeBadgeVariant(
  outcome: UsageEventOutcome,
): "success" | "warning" | "danger" | "muted" {
  switch (outcome) {
    case "Success":
      return "success";
    case "Timeout":
      return "warning";
    case "Banned":
    case "Failure":
      return "danger";
    default:
      return "muted";
  }
}

/** Absolute UTC — operators correlate these with provider dashboards and server logs. */
export function formatUtcTimestamp(iso: string): string {
  const d = new Date(iso);
  if (Number.isNaN(d.getTime())) return iso;
  return d.toISOString().replace("T", " ").replace(/\.\d+Z$/, "Z");
}

import { useEffect, useMemo, useState } from "react";
import { keepPreviousData, useQuery } from "@tanstack/react-query";
import { Activity, Bot, HeartPulse, RefreshCw } from "lucide-react";
import { EntityPageHeader, ErrorBand, LoadingRow, Pagination } from "@/components/list";
import { EmptyState } from "@/components/empty-state";
import { Button } from "@/components/ui/button";
import { Badge } from "@/components/ui/badge";
import { Select } from "@/components/ui/select";
import { ApiRequestError } from "@/lib/api-client";
import { cn } from "@/lib/cn";
import { formatUtcTimestamp, outcomeBadgeVariant } from "@/lib/usage-events";
import {
  listProxyUsageEvents,
  type ProxyUsageEventDto,
  type UsageEventOutcome,
  type UsageEventSource,
} from "@/api/proxy-usage-events";

const PAGE_SIZE = 50;

const OUTCOME_OPTIONS: { value: UsageEventOutcome; label: string }[] = [
  { value: "Success", label: "Success" },
  { value: "Failure", label: "Failure" },
  { value: "Timeout", label: "Timeout" },
  { value: "Banned", label: "Banned" },
];

const SOURCE_OPTIONS: { value: UsageEventSource; label: string }[] = [
  { value: "SystemHealthCheck", label: "Health check" },
  { value: "ConsumerFeedback", label: "Consumer feedback" },
];

type RangeKey = "1h" | "24h" | "7d" | "all";

const RANGE_OPTIONS: { value: RangeKey; label: string }[] = [
  { value: "1h", label: "Last hour" },
  { value: "24h", label: "Last 24 hours" },
  { value: "7d", label: "Last 7 days" },
  { value: "all", label: "All time" },
];

const RANGE_HOURS: Record<Exclude<RangeKey, "all">, number> = { "1h": 1, "24h": 24, "7d": 24 * 7 };

// Desktop grid — shared by header + rows.
const COLS = "grid-cols-[170px_1.9fr_110px_1fr_1.6fr]";

function describeError(err: unknown): string {
  if (err instanceof ApiRequestError) return err.problem?.detail ?? err.problem?.title ?? err.message;
  if (err instanceof Error) return err.message;
  return "Something went wrong.";
}

export function ProxyActivityPage() {
  const [pageNumber, setPageNumber] = useState(1);
  const [outcome, setOutcome] = useState<UsageEventOutcome | "">("");
  const [source, setSource] = useState<UsageEventSource | "">("");
  const [failuresOnly, setFailuresOnly] = useState(false);
  const [range, setRange] = useState<RangeKey>("24h");

  useEffect(() => {
    setPageNumber(1);
  }, [outcome, source, failuresOnly, range]);

  // Recomputed only when the range changes — deriving it inline would produce a new value on every
  // render, changing the query key continuously and refetching forever.
  const fromUtc = useMemo(() => {
    if (range === "all") return undefined;
    return new Date(Date.now() - RANGE_HOURS[range] * 3600_000).toISOString();
  }, [range]);

  const eventsQuery = useQuery({
    queryKey: ["proxies", "usage-events", "fleet", { pageNumber, outcome, source, failuresOnly, range }],
    queryFn: () =>
      listProxyUsageEvents({
        pageNumber,
        pageSize: PAGE_SIZE,
        outcome: outcome || undefined,
        source: source || undefined,
        failuresOnly: failuresOnly || undefined,
        fromUtc,
      }),
    placeholderData: keepPreviousData,
  });

  const data = eventsQuery.data;
  const items: ProxyUsageEventDto[] = data?.items ?? [];
  const filtersActive = outcome !== "" || source !== "" || failuresOnly || range !== "24h";

  function clearFilters() {
    setOutcome("");
    setSource("");
    setFailuresOnly(false);
    setRange("24h");
  }

  return (
    <div className="space-y-4 sm:space-y-6">
      <EntityPageHeader
        icon={Activity}
        title="Activity"
        total={data?.totalCount ?? null}
        unit="event"
        description="Every health-check probe and consumer feedback report across the fleet, newest first."
      >
        <Button
          variant="outline"
          size="sm"
          disabled={eventsQuery.isFetching}
          onClick={() => eventsQuery.refetch()}
          className="flex-1 sm:flex-none"
        >
          <RefreshCw className={cn("mr-1.5 h-3.5 w-3.5", eventsQuery.isFetching && "animate-spin")} />
          Refresh
        </Button>
      </EntityPageHeader>

      <div className="flex flex-wrap items-end gap-3">
        <div data-testid="activity-range-select">
          <Select
            label="Range"
            value={range}
            onChange={(v) => setRange((v || "24h") as RangeKey)}
            options={RANGE_OPTIONS}
            minWidth="11rem"
          />
        </div>
        <div data-testid="activity-outcome-select">
          <Select
            label="Outcome"
            value={outcome}
            onChange={(v) => setOutcome(v as UsageEventOutcome | "")}
            options={OUTCOME_OPTIONS}
            placeholder="Any outcome"
            minWidth="9rem"
          />
        </div>
        <div data-testid="activity-source-select">
          <Select
            label="Source"
            value={source}
            onChange={(v) => setSource(v as UsageEventSource | "")}
            options={SOURCE_OPTIONS}
            placeholder="Any source"
            minWidth="11rem"
          />
        </div>
        <Button
          type="button"
          variant={failuresOnly ? "default" : "outline"}
          size="sm"
          aria-pressed={failuresOnly}
          onClick={() => setFailuresOnly((v) => !v)}
        >
          Failures only
        </Button>
        {filtersActive && (
          <Button variant="ghost" size="sm" onClick={clearFilters}>
            Clear filters
          </Button>
        )}
      </div>

      {eventsQuery.isError && <ErrorBand message={describeError(eventsQuery.error)} />}
      {eventsQuery.isLoading && <LoadingRow label="Loading activity" />}

      {!eventsQuery.isLoading && !eventsQuery.isError && items.length === 0 && (
        <EmptyState
          icon={Activity}
          kicker="// no activity"
          title="No events in this range."
          description={
            filtersActive
              ? "Try widening the range or clearing the outcome filter."
              : "Events appear once the active health check runs, or a consumer reports feedback through the API."
          }
          action={filtersActive ? <Button variant="outline" onClick={clearFilters}>Clear filters</Button> : undefined}
        />
      )}

      {items.length > 0 && (
        <div>
          {/* Mobile card list */}
          <div className="space-y-2 md:hidden">
            {items.map((event) => (
              <div
                key={event.id}
                className="rounded-xl border border-[var(--color-border)] bg-[var(--color-card)] p-3.5 shadow-xs"
              >
                <div className="flex items-start justify-between gap-2">
                  <span className="truncate font-mono text-[12.5px] font-medium text-[var(--color-foreground)]">
                    {event.proxyHost}:{event.proxyPort}
                  </span>
                  <Badge
                    variant={outcomeBadgeVariant(event.outcome)}
                    className="shrink-0 font-mono uppercase tracking-[0.14em]"
                  >
                    {event.outcome}
                  </Badge>
                </div>
                {event.proxyUsername && (
                  <p className="mt-0.5 break-all font-mono text-[11px] text-[var(--color-muted-foreground)]">
                    {event.proxyUsername}
                  </p>
                )}
                <p className="mt-1 font-mono text-[11px] text-[var(--color-muted-foreground)]">
                  {formatUtcTimestamp(event.occurredAtUtc)} · <ReporterLabel event={event} />
                </p>
                {event.detail && (
                  <p className="mt-1 break-all font-mono text-[11px] text-[var(--color-foreground)]">{event.detail}</p>
                )}
              </div>
            ))}
          </div>

          {/* Desktop table */}
          <div className="hidden overflow-hidden rounded-xl border border-[var(--color-border)] bg-[var(--color-card)] shadow-xs md:block">
            <div
              className={cn(
                "grid items-center gap-3 border-b border-[var(--color-border)] bg-[var(--color-muted)]/40 px-4 py-2.5",
                COLS,
              )}
            >
              {["When (UTC)", "Proxy · User", "Outcome", "Reported by", "Detail"].map((h) => (
                <span
                  key={h}
                  className="text-[11.5px] font-semibold uppercase tracking-wider text-[var(--color-muted-foreground)]"
                >
                  {h}
                </span>
              ))}
            </div>
            <ol className="divide-y divide-[var(--color-border)]">
              {items.map((event) => {
                const isHealthCheck = event.source === "SystemHealthCheck";
                const SourceIcon = isHealthCheck ? HeartPulse : Bot;
                return (
                  <li key={event.id} className={cn("grid items-center gap-3 px-4 py-2.5", COLS)}>
                    <span className="font-mono text-[11.5px] text-[var(--color-muted-foreground)]">
                      {formatUtcTimestamp(event.occurredAtUtc)}
                    </span>
                    <div className="min-w-0">
                      <span
                        className="block truncate font-mono text-[12.5px] text-[var(--color-foreground)]"
                        title={`${event.proxyHost}:${event.proxyPort}`}
                      >
                        {event.proxyHost}:{event.proxyPort}
                      </span>
                      {event.proxyUsername && (
                        <span
                          className="line-clamp-2 break-all font-mono text-[11px] leading-[1.35] text-[var(--color-muted-foreground)]"
                          title={event.proxyUsername}
                        >
                          {event.proxyUsername}
                        </span>
                      )}
                    </div>
                    <Badge
                      variant={outcomeBadgeVariant(event.outcome)}
                      className="w-fit font-mono uppercase tracking-[0.14em]"
                    >
                      {event.outcome}
                    </Badge>
                    <span className="flex min-w-0 items-center gap-1.5 text-[12px] text-[var(--color-muted-foreground)]">
                      <SourceIcon
                        className={cn("size-3.5 shrink-0", isHealthCheck && "text-[var(--color-info)]")}
                        aria-hidden
                      />
                      <span className="truncate">
                        <ReporterLabel event={event} />
                      </span>
                    </span>
                    <span
                      className="truncate font-mono text-[11.5px] text-[var(--color-muted-foreground)]"
                      title={event.detail ?? undefined}
                    >
                      {event.detail ?? "—"}
                    </span>
                  </li>
                );
              })}
            </ol>
          </div>
        </div>
      )}

      {data && data.totalPages > 1 && (
        <Pagination
          page={data.pageNumber}
          totalPages={data.totalPages}
          totalCount={data.totalCount}
          shown={items.length}
          fetching={eventsQuery.isFetching}
          hasPrev={data.hasPrevious}
          hasNext={data.hasNext}
          onPrev={() => setPageNumber((p) => Math.max(1, p - 1))}
          onNext={() => setPageNumber((p) => p + 1)}
          noun="events"
        />
      )}
    </div>
  );
}

function ReporterLabel({ event }: { event: ProxyUsageEventDto }) {
  if (event.source === "SystemHealthCheck") return <>health check</>;
  // An id with no name means the API client was deleted after reporting.
  return <>{event.reportedByApiClientName ?? (event.reportedByApiClientId ? "deleted client" : "unknown client")}</>;
}

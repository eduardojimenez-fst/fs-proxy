import { useEffect, useState } from "react";
import { keepPreviousData, useQuery } from "@tanstack/react-query";
import { Activity, Bot, HeartPulse, ShieldAlert, ShieldCheck } from "lucide-react";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import {
  Dialog,
  DialogBody,
  DialogContent,
  DialogDescription,
  DialogFooter,
  DialogHeader,
  DialogTitle,
} from "@/components/ui/dialog";
import { ErrorBand, LoadingRow, Pagination } from "@/components/list";
import { Select } from "@/components/ui/select";
import { EmptyState } from "@/components/empty-state";
import { ApiRequestError } from "@/lib/api-client";
import { cn } from "@/lib/cn";
import { formatUtcTimestamp, outcomeBadgeVariant } from "@/lib/usage-events";
import {
  listProxyUsageEvents,
  type ProxyUsageEventDto,
  type UsageEventOutcome,
  type UsageEventSource,
} from "@/api/proxy-usage-events";
import { getProxyPolicyResolution, type ProxyPolicyResolutionDto } from "@/api/proxy-policy-resolution";
import type { ProxyDto } from "@/api/proxies";

const PAGE_SIZE = 20;

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

function describeError(err: unknown): string {
  if (err instanceof ApiRequestError) return err.problem?.detail ?? err.problem?.title ?? err.message;
  if (err instanceof Error) return err.message;
  return "Something went wrong.";
}

export function ProxyUsageEventsDialog({
  open,
  proxy,
  onClose,
}: {
  open: boolean;
  proxy: ProxyDto | null;
  onClose: () => void;
}) {
  const [pageNumber, setPageNumber] = useState(1);
  const [outcome, setOutcome] = useState<UsageEventOutcome | "">("");
  const [source, setSource] = useState<UsageEventSource | "">("");
  const [failuresOnly, setFailuresOnly] = useState(false);

  // Reset filters whenever the dialog targets a different proxy, so a filter left over from the
  // previous one can't make a busy proxy look silent.
  useEffect(() => {
    setPageNumber(1);
    setOutcome("");
    setSource("");
    setFailuresOnly(false);
  }, [proxy?.id]);

  useEffect(() => {
    setPageNumber(1);
  }, [outcome, source, failuresOnly]);

  const eventsQuery = useQuery({
    queryKey: ["proxies", "usage-events", { proxyId: proxy?.id, pageNumber, outcome, source, failuresOnly }],
    queryFn: () =>
      listProxyUsageEvents({
        proxyId: proxy!.id,
        pageNumber,
        pageSize: PAGE_SIZE,
        outcome: outcome || undefined,
        source: source || undefined,
        failuresOnly: failuresOnly || undefined,
      }),
    enabled: open && proxy !== null,
    placeholderData: keepPreviousData,
  });

  // What the auto-disable engine would do with this proxy right now — the "so what" above the raw
  // events. Computed server-side by the same resolver the engine uses.
  const resolutionQuery = useQuery({
    queryKey: ["proxies", "policy-resolution", proxy?.id],
    queryFn: () => getProxyPolicyResolution(proxy!.id),
    enabled: open && proxy !== null,
  });

  const data = eventsQuery.data;
  const items: ProxyUsageEventDto[] = data?.items ?? [];
  const filtersActive = outcome !== "" || source !== "" || failuresOnly;

  return (
    <Dialog open={open} onOpenChange={(o) => !o && onClose()}>
      <DialogContent className="sm:max-w-3xl">
        <DialogHeader>
          <DialogTitle>Activity</DialogTitle>
          <DialogDescription>
            {proxy ? (
              <>
                Health-check probes and consumer feedback for{" "}
                <span className="font-mono">
                  {proxy.host}:{proxy.port}
                </span>
                , newest first. This is the evidence the auto-disable policy acts on.
              </>
            ) : null}
          </DialogDescription>
        </DialogHeader>

        <DialogBody className="space-y-4">
          {resolutionQuery.data && <PolicyResolutionPanel resolution={resolutionQuery.data} />}

          <div className="flex flex-wrap items-end gap-3">
            <Select
              label="Outcome"
              value={outcome}
              onChange={(v) => setOutcome(v as UsageEventOutcome | "")}
              options={OUTCOME_OPTIONS}
              placeholder="Any outcome"
              minWidth="9rem"
            />
            <Select
              label="Source"
              value={source}
              onChange={(v) => setSource(v as UsageEventSource | "")}
              options={SOURCE_OPTIONS}
              placeholder="Any source"
              minWidth="11rem"
            />
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
              <Button
                variant="ghost"
                size="sm"
                onClick={() => {
                  setOutcome("");
                  setSource("");
                  setFailuresOnly(false);
                }}
              >
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
              title={filtersActive ? "No events match these filters." : "No activity recorded yet."}
              description={
                filtersActive
                  ? "Try clearing the outcome or source filter."
                  : "Events appear once the active health check probes this proxy, or a consumer reports feedback for it."
              }
            />
          )}

          {items.length > 0 && (
            <ol className="divide-y divide-[var(--color-border)] rounded-xl border border-[var(--color-border)] bg-[var(--color-card)]">
              {items.map((event) => (
                <UsageEventRow key={event.id} event={event} />
              ))}
            </ol>
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
        </DialogBody>

        <DialogFooter>
          <Button type="button" variant="outline" onClick={onClose}>
            Close
          </Button>
        </DialogFooter>
      </DialogContent>
    </Dialog>
  );
}

/** Explains, above the raw events, which policy governs this proxy and how close it is to tripping. */
function PolicyResolutionPanel({ resolution }: { resolution: ProxyPolicyResolutionDto }) {
  const { policy, failuresInWindow, distinctReportersInWindow } = resolution;

  if (policy === null) {
    return (
      <div className="flex items-start gap-2.5 rounded-lg border border-[var(--color-border)] bg-[var(--color-muted)]/40 px-3 py-2.5">
        <ShieldAlert className="mt-0.5 size-4 shrink-0 text-[var(--color-warning)]" aria-hidden />
        <div className="min-w-0 text-[12.5px]">
          <p className="font-medium text-[var(--color-foreground)]">No policy governs this proxy.</p>
          <p className="text-[var(--color-muted-foreground)]">
            {resolution.tags.length === 0
              ? "It has no tags, and policies are assigned per tag — it will never be auto-disabled."
              : "None of its tags has a policy assigned — it will never be auto-disabled, however many failures it collects."}
          </p>
          <TargetLine resolution={resolution} />
        </div>
      </div>
    );
  }

  const failures = failuresInWindow ?? 0;
  const reporters = distinctReportersInWindow ?? 0;
  const failuresMet = failures >= policy.failureThreshold;
  const reportersMet = reporters >= policy.minDistinctReporters;
  // Manual profiles record but never act, so "would trip" is only meaningful for the other two.
  const wouldTrip = policy.type !== "Manual" && failuresMet && reportersMet;

  return (
    <div className="flex items-start gap-2.5 rounded-lg border border-[var(--color-border)] bg-[var(--color-muted)]/40 px-3 py-2.5">
      {wouldTrip ? (
        <ShieldAlert className="mt-0.5 size-4 shrink-0 text-[var(--color-destructive)]" aria-hidden />
      ) : (
        <ShieldCheck className="mt-0.5 size-4 shrink-0 text-[var(--color-success)]" aria-hidden />
      )}
      <div className="min-w-0 space-y-0.5 text-[12.5px]">
        <p className="text-[var(--color-foreground)]">
          <span className="font-medium">{policy.name}</span>{" "}
          <Badge variant="muted" className="font-mono text-[10px] uppercase tracking-[0.12em]">
            {policy.type}
          </Badge>
          {resolution.policyFromTag && (
            <span className="text-[var(--color-muted-foreground)]">
              {" "}
              via tag <span className="font-mono">{resolution.policyFromTag}</span>
              {resolution.candidates.length > 1 && " (most restrictive of several)"}
            </span>
          )}
        </p>
        <p className="font-mono text-[11.5px] text-[var(--color-muted-foreground)]">
          <span className={failuresMet ? "text-[var(--color-destructive)]" : undefined}>
            {failures}/{policy.failureThreshold} failures
          </span>
          {" · "}
          <span className={reportersMet ? "text-[var(--color-destructive)]" : undefined}>
            {reporters}/{policy.minDistinctReporters} reporters
          </span>
          {" · "}
          {policy.windowMinutes}m window
        </p>
        {policy.type === "Manual" && (
          <p className="text-[var(--color-muted-foreground)]">
            This profile only records — disabling is left to an operator.
          </p>
        )}
        <TargetLine resolution={resolution} />
      </div>
    </div>
  );
}

function TargetLine({ resolution }: { resolution: ProxyPolicyResolutionDto }) {
  return (
    <p className="text-[11.5px] text-[var(--color-muted-foreground)]">
      Probed against{" "}
      {resolution.healthCheckTargets.map((t, i) => (
        <span key={t.id ?? "default"}>
          {i > 0 && ", "}
          <span className="font-mono" title={t.testUrl}>
            {t.name}
          </span>
        </span>
      ))}
      {resolution.usingDefaultHealthCheckTarget && " — a generic connectivity check, not your destination"}
    </p>
  );
}

function UsageEventRow({ event }: { event: ProxyUsageEventDto }) {
  const isHealthCheck = event.source === "SystemHealthCheck";
  const SourceIcon = isHealthCheck ? HeartPulse : Bot;
  return (
    <li className="flex items-start gap-3 px-4 py-3">
      <SourceIcon
        className={cn(
          "mt-0.5 size-4 shrink-0",
          isHealthCheck ? "text-[var(--color-info)]" : "text-[var(--color-muted-foreground)]",
        )}
        aria-hidden
      />
      <div className="min-w-0 flex-1">
        <div className="flex flex-wrap items-center gap-2">
          <Badge variant={outcomeBadgeVariant(event.outcome)} className="font-mono uppercase tracking-[0.14em]">
            {event.outcome}
          </Badge>
          <span className="font-mono text-[11.5px] text-[var(--color-muted-foreground)]">
            {formatUtcTimestamp(event.occurredAtUtc)}
          </span>
          <span className="text-[11.5px] text-[var(--color-muted-foreground)]">
            {isHealthCheck
              ? "health check"
              : // An id with no name means the API client was deleted after reporting.
                (event.reportedByApiClientName ?? (event.reportedByApiClientId ? "deleted client" : "unknown client"))}
          </span>
        </div>
        {event.detail && (
          <p className="mt-1 break-all font-mono text-[11.5px] text-[var(--color-foreground)]">{event.detail}</p>
        )}
      </div>
    </li>
  );
}

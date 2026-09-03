import { useEffect, useMemo, useState } from "react";
import { keepPreviousData, useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { toast } from "sonner";
import { Globe, RefreshCw } from "lucide-react";
import { EntityPageHeader, ErrorBand, LoadingRow, Pagination } from "@/components/list";
import { EmptyState } from "@/components/empty-state";
import { Button } from "@/components/ui/button";
import { Badge } from "@/components/ui/badge";
import { Select } from "@/components/ui/select";
import { ApiRequestError } from "@/lib/api-client";
import { cn } from "@/lib/cn";
import { ProxiesPermissions } from "@/lib/permissions";
import { useAuth } from "@/auth/use-auth";
import {
  disableProxies,
  enableProxies,
  listProxies,
  type ProxyDto,
  type ProxyStatus,
  type SetProxiesStatusInput,
} from "@/api/proxies";
import { listProviderAccounts } from "@/api/provider-accounts";

const PAGE_SIZE = 20;

const STATUS_OPTIONS: { value: ProxyStatus; label: string }[] = [
  { value: "Active", label: "Active" },
  { value: "Disabled", label: "Disabled" },
  { value: "Testing", label: "Testing" },
  { value: "Banned", label: "Banned" },
  { value: "Retired", label: "Retired" },
];

// Desktop grid template — shared by header + rows.
const DESKTOP_COLS = "grid-cols-[24px_1.3fr_100px_1.2fr_1.4fr_120px]";

function describeError(err: unknown): string {
  if (err instanceof ApiRequestError) return err.problem?.detail ?? err.problem?.title ?? err.message;
  if (err instanceof Error) return err.message;
  return "Something went wrong.";
}

function statusBadgeVariant(status: ProxyStatus): React.ComponentProps<typeof Badge>["variant"] {
  switch (status) {
    case "Active":
      return "success";
    case "Testing":
      return "info";
    case "Banned":
      return "danger";
    case "Retired":
      return "warning";
    case "Disabled":
    default:
      return "muted";
  }
}

export function ProxiesListPage() {
  const { user } = useAuth();
  const queryClient = useQueryClient();

  const [pageNumber, setPageNumber] = useState(1);
  const [tagsInput, setTagsInput] = useState("");
  const [tags, setTags] = useState<string[]>([]);
  const [status, setStatus] = useState<ProxyStatus | "">("");
  const [providerAccountId, setProviderAccountId] = useState("");
  const [selected, setSelected] = useState<Set<string>>(new Set());

  // Debounce the free-text tags input → the committed `tags` filter.
  useEffect(() => {
    const t = setTimeout(() => {
      setTags(
        tagsInput
          .split(",")
          .map((s) => s.trim())
          .filter(Boolean),
      );
      setPageNumber(1);
    }, 300);
    return () => clearTimeout(t);
  }, [tagsInput]);

  // Reset to page 1 whenever a dropdown filter changes.
  useEffect(() => {
    setPageNumber(1);
  }, [status, providerAccountId]);

  const canUpdate = user?.permissions.includes(ProxiesPermissions.ManualProxies.Update) ?? false;

  const providerAccountsQuery = useQuery({
    queryKey: ["proxies", "provider-accounts", "all"],
    queryFn: () => listProviderAccounts(1, 100),
    staleTime: 60_000,
  });

  const proxiesQuery = useQuery({
    queryKey: ["proxies", "list", { pageNumber, tags, status, providerAccountId }],
    queryFn: () =>
      listProxies({
        pageNumber,
        pageSize: PAGE_SIZE,
        tags: tags.length > 0 ? tags : undefined,
        status: status || undefined,
        providerAccountId: providerAccountId || undefined,
      }),
    placeholderData: keepPreviousData,
  });

  const enableMutation = useMutation({
    mutationFn: (input: SetProxiesStatusInput) => enableProxies(input),
    onSuccess: (count) => {
      toast.success(count === 1 ? "1 proxy enabled" : `${count} proxies enabled`);
      setSelected(new Set());
      void queryClient.invalidateQueries({ queryKey: ["proxies", "list"] });
    },
    onError: (err) => toast.error("Enable failed", { description: describeError(err) }),
  });

  const disableMutation = useMutation({
    mutationFn: (input: SetProxiesStatusInput) => disableProxies(input),
    onSuccess: (count) => {
      toast.success(count === 1 ? "1 proxy disabled" : `${count} proxies disabled`);
      setSelected(new Set());
      void queryClient.invalidateQueries({ queryKey: ["proxies", "list"] });
    },
    onError: (err) => toast.error("Disable failed", { description: describeError(err) }),
  });

  const data = proxiesQuery.data;
  const items: ProxyDto[] = data?.items ?? [];
  const mutationBusy = enableMutation.isPending || disableMutation.isPending;

  const allOnPageSelected = items.length > 0 && items.every((p) => selected.has(p.id));

  function toggleSelected(id: string) {
    setSelected((prev) => {
      const next = new Set(prev);
      if (next.has(id)) next.delete(id);
      else next.add(id);
      return next;
    });
  }

  function toggleSelectAllOnPage() {
    setSelected((prev) => {
      if (allOnPageSelected) {
        const next = new Set(prev);
        for (const p of items) next.delete(p.id);
        return next;
      }
      const next = new Set(prev);
      for (const p of items) next.add(p.id);
      return next;
    });
  }

  const filtersActive = tags.length > 0 || status !== "" || providerAccountId !== "";

  const clearFilters = () => {
    setTagsInput("");
    setStatus("");
    setProviderAccountId("");
  };

  const providerOptions = useMemo(
    () =>
      (providerAccountsQuery.data?.items ?? []).map((a) => ({
        value: a.id,
        label: a.name,
      })),
    [providerAccountsQuery.data],
  );

  return (
    <div className="space-y-4 sm:space-y-6">
      <EntityPageHeader
        icon={Globe}
        title="Proxies"
        total={data?.totalCount ?? null}
        unit="proxy"
        description="Inventory of proxies across all provider accounts and manual entries."
      >
        <Button
          variant="outline"
          size="sm"
          disabled={proxiesQuery.isFetching}
          onClick={() => proxiesQuery.refetch()}
          className="flex-1 sm:flex-none"
        >
          <RefreshCw className={cn("mr-1.5 h-3.5 w-3.5", proxiesQuery.isFetching && "animate-spin")} />
          Refresh
        </Button>
      </EntityPageHeader>

      {/* Filter row */}
      <div className="flex flex-wrap items-end gap-3">
        <div className="flex flex-col gap-1">
          <label
            htmlFor="proxies-tags"
            className="font-mono text-[0.6875rem] uppercase tracking-[0.18em] text-[var(--color-muted-foreground)]"
          >
            Tags
          </label>
          <input
            id="proxies-tags"
            type="search"
            placeholder="pais:cl, funcionalidad:licitaciones"
            value={tagsInput}
            onChange={(e) => setTagsInput(e.target.value)}
            className="h-9 w-72 max-w-full rounded-md border border-[var(--color-input)] bg-transparent px-3 font-mono text-[12.5px] outline-none transition-colors placeholder:text-[oklch(from_var(--color-muted-foreground)_l_c_h_/_0.7)] focus-visible:border-[var(--color-ring)] focus-visible:ring-[3px] focus-visible:ring-[oklch(from_var(--color-ring)_l_c_h_/_0.5)]"
          />
        </div>

        <Select
          label="Status"
          value={status}
          onChange={(v) => setStatus(v as ProxyStatus | "")}
          options={STATUS_OPTIONS}
          placeholder="Any status"
          minWidth="9rem"
        />

        <Select
          label="Provider account"
          value={providerAccountId}
          onChange={setProviderAccountId}
          options={providerOptions}
          placeholder="Any account"
          minWidth="12rem"
        />

        {filtersActive && (
          <Button variant="ghost" size="sm" onClick={clearFilters}>
            Clear filters
          </Button>
        )}

        {canUpdate && selected.size > 0 && (
          <div className="ml-auto flex gap-2">
            <Button
              size="sm"
              disabled={mutationBusy}
              onClick={() => enableMutation.mutate({ proxyIds: [...selected] })}
            >
              Enable selected ({selected.size})
            </Button>
            <Button
              variant="outline"
              size="sm"
              disabled={mutationBusy}
              onClick={() => disableMutation.mutate({ proxyIds: [...selected] })}
            >
              Disable selected
            </Button>
          </div>
        )}
      </div>

      {proxiesQuery.isError && <ErrorBand message={describeError(proxiesQuery.error)} />}

      {proxiesQuery.isLoading && <LoadingRow label="Loading proxies" />}

      {!proxiesQuery.isLoading && !proxiesQuery.isError && items.length === 0 && (
        <EmptyState
          icon={Globe}
          kicker="// no matches"
          title="No proxies match these filters."
          description={
            filtersActive
              ? "Try clearing the tag, status, or provider account filter."
              : "Connect a provider account or add a manual proxy to populate this list."
          }
          action={
            filtersActive ? (
              <Button variant="outline" onClick={clearFilters}>
                Clear filters
              </Button>
            ) : undefined
          }
        />
      )}

      {!proxiesQuery.isLoading && items.length > 0 && (
        <div>
          {/* Mobile card list */}
          <div className="space-y-2 md:hidden">
            {items.map((proxy) => (
              <ProxyMobileCard
                key={proxy.id}
                proxy={proxy}
                selected={selected.has(proxy.id)}
                canUpdate={canUpdate}
                busy={mutationBusy}
                onToggleSelected={() => toggleSelected(proxy.id)}
                onEnable={() => enableMutation.mutate({ proxyIds: [proxy.id] })}
                onDisable={() => disableMutation.mutate({ proxyIds: [proxy.id] })}
              />
            ))}
          </div>

          {/* Desktop table */}
          <div className="hidden overflow-hidden rounded-xl border border-[var(--color-border)] bg-[var(--color-card)] shadow-xs md:block">
            <div
              className={cn(
                "grid items-center gap-3 border-b border-[var(--color-border)] bg-[var(--color-muted)]/40 px-4 py-2.5",
                DESKTOP_COLS,
              )}
            >
              {canUpdate ? (
                <input
                  type="checkbox"
                  checked={allOnPageSelected}
                  onChange={toggleSelectAllOnPage}
                  aria-label="Select all proxies on this page"
                />
              ) : (
                <span />
              )}
              <span className="text-[11.5px] font-semibold uppercase tracking-wider text-[var(--color-muted-foreground)]">
                Host
              </span>
              <span className="text-[11.5px] font-semibold uppercase tracking-wider text-[var(--color-muted-foreground)]">
                Status
              </span>
              <span className="text-[11.5px] font-semibold uppercase tracking-wider text-[var(--color-muted-foreground)]">
                Provider
              </span>
              <span className="text-[11.5px] font-semibold uppercase tracking-wider text-[var(--color-muted-foreground)]">
                Tags
              </span>
              <span />
            </div>

            <ol className="divide-y divide-[var(--color-border)]">
              {items.map((proxy) => (
                <ProxyDesktopRow
                  key={proxy.id}
                  proxy={proxy}
                  selected={selected.has(proxy.id)}
                  canUpdate={canUpdate}
                  busy={mutationBusy}
                  onToggleSelected={() => toggleSelected(proxy.id)}
                  onEnable={() => enableMutation.mutate({ proxyIds: [proxy.id] })}
                  onDisable={() => disableMutation.mutate({ proxyIds: [proxy.id] })}
                />
              ))}
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
          fetching={proxiesQuery.isFetching}
          hasPrev={data.hasPrevious}
          hasNext={data.hasNext}
          onPrev={() => setPageNumber((p) => Math.max(1, p - 1))}
          onNext={() => setPageNumber((p) => p + 1)}
          noun="proxies"
        />
      )}
    </div>
  );
}

// ─── Desktop row ────────────────────────────────────────────────────────

function ProxyDesktopRow({
  proxy,
  selected,
  canUpdate,
  busy,
  onToggleSelected,
  onEnable,
  onDisable,
}: {
  proxy: ProxyDto;
  selected: boolean;
  canUpdate: boolean;
  busy: boolean;
  onToggleSelected: () => void;
  onEnable: () => void;
  onDisable: () => void;
}) {
  return (
    <li className="list-none">
      <div className={cn("grid items-center gap-3 px-4 py-3", DESKTOP_COLS)}>
        {canUpdate ? (
          <input
            type="checkbox"
            checked={selected}
            onChange={onToggleSelected}
            aria-label={`Select ${proxy.host}:${proxy.port}`}
          />
        ) : (
          <span />
        )}
        <div className="min-w-0">
          <span className="block truncate font-mono text-[13px] font-medium text-[var(--color-foreground)]">
            {proxy.host}:{proxy.port}
          </span>
          <span className="block truncate font-mono text-[11px] text-[var(--color-muted-foreground)]">
            {proxy.protocol}
          </span>
        </div>
        <div>
          <Badge variant={statusBadgeVariant(proxy.status)} className="font-mono uppercase tracking-[0.14em]">
            {proxy.status}
          </Badge>
        </div>
        <div className="min-w-0">
          <span className="block truncate text-[13px] text-[var(--color-foreground)]">
            {proxy.providerAccountName}
          </span>
          <span className="block truncate font-mono text-[11px] text-[var(--color-muted-foreground)]">
            {proxy.providerType}
          </span>
        </div>
        <span className="truncate text-[12px] text-[var(--color-muted-foreground)]">
          {proxy.tags.length > 0 ? proxy.tags.join(", ") : "—"}
        </span>
        <div className="flex items-center justify-end">
          {canUpdate ? (
            proxy.status === "Active" ? (
              <Button variant="outline" size="sm" disabled={busy} onClick={onDisable}>
                Disable
              </Button>
            ) : (
              <Button size="sm" disabled={busy} onClick={onEnable}>
                Enable
              </Button>
            )
          ) : null}
        </div>
      </div>
    </li>
  );
}

// ─── Mobile card ────────────────────────────────────────────────────────

function ProxyMobileCard({
  proxy,
  selected,
  canUpdate,
  busy,
  onToggleSelected,
  onEnable,
  onDisable,
}: {
  proxy: ProxyDto;
  selected: boolean;
  canUpdate: boolean;
  busy: boolean;
  onToggleSelected: () => void;
  onEnable: () => void;
  onDisable: () => void;
}) {
  return (
    <div className="rounded-xl border border-[var(--color-border)] bg-[var(--color-card)] p-4 shadow-xs">
      <div className="flex items-start justify-between gap-3">
        <div className="flex min-w-0 items-start gap-2.5">
          {canUpdate && (
            <input
              type="checkbox"
              checked={selected}
              onChange={onToggleSelected}
              aria-label={`Select ${proxy.host}:${proxy.port}`}
              className="mt-1"
            />
          )}
          <div className="min-w-0">
            <p className="truncate font-mono text-[13px] font-medium text-[var(--color-foreground)]">
              {proxy.host}:{proxy.port}
            </p>
            <p className="mt-0.5 truncate text-[11px] text-[var(--color-muted-foreground)]">
              {proxy.providerAccountName} ({proxy.providerType})
            </p>
          </div>
        </div>
        <Badge variant={statusBadgeVariant(proxy.status)} className="shrink-0 font-mono uppercase tracking-[0.14em]">
          {proxy.status}
        </Badge>
      </div>
      <p className="mt-2 truncate text-[11px] text-[var(--color-muted-foreground)]">
        {proxy.tags.length > 0 ? proxy.tags.join(", ") : "No tags"}
      </p>
      {canUpdate && (
        <div className="mt-3">
          {proxy.status === "Active" ? (
            <Button variant="outline" size="sm" disabled={busy} onClick={onDisable} className="w-full">
              Disable
            </Button>
          ) : (
            <Button size="sm" disabled={busy} onClick={onEnable} className="w-full">
              Enable
            </Button>
          )}
        </div>
      )}
    </div>
  );
}

import { useEffect, useMemo, useState } from "react";
import { keepPreviousData, useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { toast } from "sonner";
import { Globe, RefreshCw, Tag as TagIcon, X } from "lucide-react";
import { EntityPageHeader, ErrorBand, LoadingRow, Pagination } from "@/components/list";
import { EmptyState } from "@/components/empty-state";
import { Button } from "@/components/ui/button";
import { Badge } from "@/components/ui/badge";
import { Input } from "@/components/ui/input";
import { Select } from "@/components/ui/select";
import { ApiRequestError } from "@/lib/api-client";
import { cn } from "@/lib/cn";
import { countryFlag } from "@/lib/country-flag";
import { ProxiesPermissions } from "@/lib/permissions";
import { useAuth } from "@/auth/use-auth";
import { ProxyTagsDialog } from "@/components/proxies/proxy-tags-dialog";
import { BulkTagDialog } from "@/components/proxies/bulk-tag-dialog";
import {
  disableProxies,
  enableProxies,
  listProxies,
  type ProxyDto,
  type ProxyKind,
  type ProxyStatus,
  type SetProxiesStatusInput,
} from "@/api/proxies";
import { listProviderAccounts } from "@/api/provider-accounts";
import { listTagCategories } from "@/api/tag-categories";

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
  const [tags, setTags] = useState<string[]>([]);
  const [filterCategory, setFilterCategory] = useState("");
  const [filterValue, setFilterValue] = useState("");
  const [customTagInput, setCustomTagInput] = useState("");
  const [status, setStatus] = useState<ProxyStatus | "">("");
  const [providerAccountId, setProviderAccountId] = useState("");
  const [geolocationInput, setGeolocationInput] = useState("");
  const [geolocation, setGeolocation] = useState("");
  const [kind, setKind] = useState<ProxyKind | "">("");
  const [selected, setSelected] = useState<Set<string>>(new Set());
  const [tagsDialogProxy, setTagsDialogProxy] = useState<ProxyDto | null>(null);
  const [bulkTagDialogOpen, setBulkTagDialogOpen] = useState(false);

  const tagCategoriesQuery = useQuery({
    queryKey: ["proxies", "tag-categories"],
    queryFn: () => listTagCategories(),
    staleTime: 60_000,
  });
  const tagCategories = tagCategoriesQuery.data ?? [];
  const filterCategoryValues = tagCategories.find((c) => c.name === filterCategory)?.values ?? [];

  function addTagFilter(tag: string) {
    const trimmed = tag.trim();
    if (!trimmed) return;
    setTags((prev) => (prev.includes(trimmed) ? prev : [...prev, trimmed]));
    setPageNumber(1);
  }

  function removeTagFilter(tag: string) {
    setTags((prev) => prev.filter((t) => t !== tag));
    setPageNumber(1);
  }

  useEffect(() => {
    const t = setTimeout(() => {
      setGeolocation(geolocationInput.trim());
      setPageNumber(1);
    }, 300);
    return () => clearTimeout(t);
  }, [geolocationInput]);

  // Reset to page 1 whenever a dropdown filter changes.
  useEffect(() => {
    setPageNumber(1);
  }, [status, providerAccountId, kind]);

  const canUpdate = user?.permissions.includes(ProxiesPermissions.ManualProxies.Update) ?? false;
  const canManageTags = user?.permissions.includes(ProxiesPermissions.Tags.Update) ?? false;
  const canSelect = canUpdate || canManageTags;

  const providerAccountsQuery = useQuery({
    queryKey: ["proxies", "provider-accounts", "all"],
    queryFn: () => listProviderAccounts(1, 100),
    staleTime: 60_000,
  });

  const proxiesQuery = useQuery({
    queryKey: ["proxies", "list", { pageNumber, tags, status, providerAccountId, geolocation, kind }],
    queryFn: () =>
      listProxies({
        pageNumber,
        pageSize: PAGE_SIZE,
        tags: tags.length > 0 ? tags : undefined,
        status: status || undefined,
        providerAccountId: providerAccountId || undefined,
        geolocation: geolocation || undefined,
        kind: kind || undefined,
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

  const filtersActive = tags.length > 0 || status !== "" || providerAccountId !== "" || geolocation !== "" || kind !== "";

  const clearFilters = () => {
    setTags([]);
    setFilterCategory("");
    setFilterValue("");
    setCustomTagInput("");
    setStatus("");
    setProviderAccountId("");
    setGeolocationInput("");
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
          <span className="font-mono text-[0.6875rem] uppercase tracking-[0.18em] text-[var(--color-muted-foreground)]">
            Tags
          </span>
          <div className="flex flex-wrap items-center gap-2">
            <div data-testid="proxies-tag-category-select">
              <Select
                value={filterCategory}
                onChange={(v) => {
                  setFilterCategory(v);
                  setFilterValue("");
                }}
                options={tagCategories.map((c) => ({ value: c.name, label: c.name }))}
                placeholder="Category…"
                minWidth="9rem"
              />
            </div>
            <div data-testid="proxies-tag-value-select">
              <Select
                value={filterValue}
                onChange={setFilterValue}
                options={filterCategoryValues.map((v) => ({
                  value: v,
                  label: filterCategory.toLowerCase() === "country" ? [countryFlag(v), v].filter(Boolean).join(" ") : v,
                }))}
                placeholder="Value…"
                minWidth="9rem"
                disabled={!filterCategory}
              />
            </div>
            <Button
              type="button"
              variant="outline"
              size="sm"
              aria-label="Add category tag filter"
              disabled={!filterCategory || !filterValue}
              onClick={() => {
                addTagFilter(`${filterCategory}:${filterValue}`);
                setFilterValue("");
              }}
            >
              + Add
            </Button>
            <Input
              type="search"
              aria-label="Custom tag filter"
              placeholder="or custom tag"
              value={customTagInput}
              onChange={(e) => setCustomTagInput(e.target.value)}
              onKeyDown={(e) => {
                if (e.key === "Enter") {
                  e.preventDefault();
                  addTagFilter(customTagInput);
                  setCustomTagInput("");
                }
              }}
              className="h-9 w-32"
            />
            <Button
              type="button"
              variant="outline"
              size="sm"
              aria-label="Add custom tag filter"
              disabled={!customTagInput.trim()}
              onClick={() => {
                addTagFilter(customTagInput);
                setCustomTagInput("");
              }}
            >
              + Add
            </Button>
          </div>
        </div>

        <div className="flex flex-col gap-1">
          <label
            htmlFor="proxies-geolocation"
            className="font-mono text-[0.6875rem] uppercase tracking-[0.18em] text-[var(--color-muted-foreground)]"
          >
            Geolocation
          </label>
          <input
            id="proxies-geolocation"
            type="search"
            placeholder="CL"
            value={geolocationInput}
            onChange={(e) => setGeolocationInput(e.target.value)}
            className="h-9 w-24 max-w-full rounded-md border border-[var(--color-input)] bg-transparent px-3 font-mono text-[12.5px] outline-none transition-colors placeholder:text-[oklch(from_var(--color-muted-foreground)_l_c_h_/_0.7)] focus-visible:border-[var(--color-ring)] focus-visible:ring-[3px] focus-visible:ring-[oklch(from_var(--color-ring)_l_c_h_/_0.5)]"
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

        <div data-testid="proxies-kind-select">
          <Select
            label="Kind"
            value={kind}
            onChange={(v) => setKind(v as ProxyKind | "")}
            options={[
              { value: "DataCenter", label: "DataCenter" },
              { value: "Residential", label: "Residential" },
              { value: "Mobile", label: "Mobile" },
              { value: "Dedicated", label: "Dedicated" },
            ]}
            placeholder="Any kind"
            minWidth="9rem"
          />
        </div>

        {filtersActive && (
          <Button variant="ghost" size="sm" onClick={clearFilters}>
            Clear filters
          </Button>
        )}

        {canSelect && selected.size > 0 && (
          <div className="ml-auto flex gap-2">
            {canUpdate && (
              <>
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
              </>
            )}
            {canManageTags && (
              <Button variant="outline" size="sm" onClick={() => setBulkTagDialogOpen(true)}>
                Manage tags
              </Button>
            )}
          </div>
        )}
      </div>

      {tags.length > 0 && (
        <div className="flex flex-wrap gap-1.5">
          {tags.map((tag) => (
            <Badge key={tag} variant="muted" className="gap-1 font-mono text-[11px]">
              {tag}
              <button type="button" aria-label={`Remove tag filter ${tag}`} onClick={() => removeTagFilter(tag)}>
                <X className="h-3 w-3" />
              </button>
            </Badge>
          ))}
        </div>
      )}

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
                canManageTags={canManageTags}
                canSelect={canSelect}
                busy={mutationBusy}
                onToggleSelected={() => toggleSelected(proxy.id)}
                onEnable={() => enableMutation.mutate({ proxyIds: [proxy.id] })}
                onDisable={() => disableMutation.mutate({ proxyIds: [proxy.id] })}
                onEditTags={() => setTagsDialogProxy(proxy)}
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
              {canSelect ? (
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
                  canManageTags={canManageTags}
                  canSelect={canSelect}
                  busy={mutationBusy}
                  onToggleSelected={() => toggleSelected(proxy.id)}
                  onEnable={() => enableMutation.mutate({ proxyIds: [proxy.id] })}
                  onDisable={() => disableMutation.mutate({ proxyIds: [proxy.id] })}
                  onEditTags={() => setTagsDialogProxy(proxy)}
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

      <ProxyTagsDialog open={tagsDialogProxy !== null} proxy={tagsDialogProxy} onClose={() => setTagsDialogProxy(null)} />
      <BulkTagDialog open={bulkTagDialogOpen} proxyIds={[...selected]} onClose={() => setBulkTagDialogOpen(false)} />
    </div>
  );
}

// ─── Desktop row ────────────────────────────────────────────────────────

function ProxyDesktopRow({
  proxy,
  selected,
  canUpdate,
  canManageTags,
  canSelect,
  busy,
  onToggleSelected,
  onEnable,
  onDisable,
  onEditTags,
}: {
  proxy: ProxyDto;
  selected: boolean;
  canUpdate: boolean;
  canManageTags: boolean;
  canSelect: boolean;
  busy: boolean;
  onToggleSelected: () => void;
  onEnable: () => void;
  onDisable: () => void;
  onEditTags: () => void;
}) {
  return (
    <li className="list-none">
      <div className={cn("grid items-center gap-3 px-4 py-3", DESKTOP_COLS)}>
        {canSelect ? (
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
            {proxy.geolocation ? `${proxy.protocol} · ${countryFlag(proxy.geolocation)} ${proxy.geolocation}` : proxy.protocol}
            {proxy.kind ? ` · ${proxy.kind}` : ""}
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
            {proxy.providerGrouping ? `${proxy.providerType} · ${proxy.providerGrouping}` : proxy.providerType}
          </span>
        </div>
        <div className="flex min-w-0 flex-wrap gap-1">
          {proxy.tags.length > 0 ? (
            proxy.tags.map((tag) => (
              <Badge key={tag} variant="muted" className="font-mono text-[10.5px]">
                {tag}
              </Badge>
            ))
          ) : (
            <span className="text-[12px] text-[var(--color-muted-foreground)]">—</span>
          )}
        </div>
        <div className="flex items-center justify-end gap-1">
          {canManageTags ? (
            <Button variant="ghost" size="sm" onClick={onEditTags}>
              <TagIcon className="h-3.5 w-3.5" />
              <span className="sr-only sm:not-sr-only sm:ml-1">Tags</span>
            </Button>
          ) : null}
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
  canManageTags,
  canSelect,
  busy,
  onToggleSelected,
  onEnable,
  onDisable,
  onEditTags,
}: {
  proxy: ProxyDto;
  selected: boolean;
  canUpdate: boolean;
  canManageTags: boolean;
  canSelect: boolean;
  busy: boolean;
  onToggleSelected: () => void;
  onEnable: () => void;
  onDisable: () => void;
  onEditTags: () => void;
}) {
  return (
    <div className="rounded-xl border border-[var(--color-border)] bg-[var(--color-card)] p-4 shadow-xs">
      <div className="flex items-start justify-between gap-3">
        <div className="flex min-w-0 items-start gap-2.5">
          {canSelect && (
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
              {proxy.providerAccountName} (
              {proxy.providerGrouping ? `${proxy.providerType} · ${proxy.providerGrouping}` : proxy.providerType}
              {proxy.geolocation ? `, ${countryFlag(proxy.geolocation)} ${proxy.geolocation}` : ""}
              {proxy.kind ? `, ${proxy.kind}` : ""})
            </p>
          </div>
        </div>
        <Badge variant={statusBadgeVariant(proxy.status)} className="shrink-0 font-mono uppercase tracking-[0.14em]">
          {proxy.status}
        </Badge>
      </div>
      <div className="mt-2 flex flex-wrap gap-1">
        {proxy.tags.length > 0 ? (
          proxy.tags.map((tag) => (
            <Badge key={tag} variant="muted" className="font-mono text-[10.5px]">
              {tag}
            </Badge>
          ))
        ) : (
          <span className="text-[11px] text-[var(--color-muted-foreground)]">No tags</span>
        )}
      </div>
      {(canUpdate || canManageTags) && (
        <div className="mt-3 flex gap-2">
          {canManageTags && (
            <Button variant="outline" size="sm" onClick={onEditTags} className="flex-1">
              <TagIcon className="mr-1 h-3.5 w-3.5" /> Tags
            </Button>
          )}
          {canUpdate &&
            (proxy.status === "Active" ? (
              <Button variant="outline" size="sm" disabled={busy} onClick={onDisable} className="flex-1">
                Disable
              </Button>
            ) : (
              <Button size="sm" disabled={busy} onClick={onEnable} className="flex-1">
                Enable
              </Button>
            ))}
        </div>
      )}
    </div>
  );
}

import { useState } from "react";
import { keepPreviousData, useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { toast } from "sonner";
import { Plus, Server } from "lucide-react";
import { EntityPageHeader, ErrorBand, LoadingRow, Pagination } from "@/components/list";
import { EmptyState } from "@/components/empty-state";
import { Button } from "@/components/ui/button";
import { Badge } from "@/components/ui/badge";
import { ApiRequestError } from "@/lib/api-client";
import { ProxiesPermissions } from "@/lib/permissions";
import { useAuth } from "@/auth/use-auth";
import { listProviderAccounts } from "@/api/provider-accounts";
import { deleteManualProxy } from "@/api/manual-proxies";
import { listProxies, type ProxyDto, type ProxyStatus } from "@/api/proxies";
import { ManualProxyDialog } from "@/components/proxies/manual-proxy-dialog";

const PAGE_SIZE = 20;

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

export function ManualProxiesListPage() {
  const { user } = useAuth();
  const queryClient = useQueryClient();

  const [pageNumber, setPageNumber] = useState(1);
  const [dialogState, setDialogState] = useState<{ open: boolean; proxy?: ProxyDto }>({ open: false });
  const [busyId, setBusyId] = useState<string | null>(null);

  const canCreate = user?.permissions.includes(ProxiesPermissions.ManualProxies.Create) ?? false;
  const canUpdate = user?.permissions.includes(ProxiesPermissions.ManualProxies.Update) ?? false;
  const canDelete = user?.permissions.includes(ProxiesPermissions.ManualProxies.Delete) ?? false;

  // Manual proxies all hang off a single well-known ProviderAccount row (seeded
  // server-side as ManualProviderAccount). The frontend doesn't know its fixed
  // id, so resolve it once by provider type and filter listProxies by it.
  const manualAccountQuery = useQuery({
    queryKey: ["proxies", "provider-accounts", "manual-account"],
    queryFn: async () => (await listProviderAccounts(1, 100)).items.find((a) => a.providerType === "Manual"),
    staleTime: 60_000,
  });

  const manualAccountId = manualAccountQuery.data?.id;

  const proxiesQuery = useQuery({
    queryKey: ["proxies", "list", "manual", manualAccountId, pageNumber],
    queryFn: () => listProxies({ providerAccountId: manualAccountId!, pageNumber, pageSize: PAGE_SIZE }),
    enabled: Boolean(manualAccountId),
    placeholderData: keepPreviousData,
  });

  const deleteMutation = useMutation({
    mutationFn: (id: string) => deleteManualProxy(id),
    onMutate: (id) => setBusyId(id),
    onSuccess: () => {
      toast.success("Manual proxy deleted");
      void queryClient.invalidateQueries({ queryKey: ["proxies", "list"] });
    },
    onError: (err) => toast.error("Delete failed", { description: describeError(err) }),
    onSettled: () => setBusyId(null),
  });

  const data = proxiesQuery.data;
  const items: ProxyDto[] = data?.items ?? [];
  const isLoading = manualAccountQuery.isLoading || proxiesQuery.isLoading;
  const isError = manualAccountQuery.isError || proxiesQuery.isError;

  return (
    <div className="space-y-8">
      <EntityPageHeader
        icon={Server}
        title="Manual proxies"
        total={data?.totalCount ?? null}
        unit="proxy"
        description="Self-hosted proxies with no provider API to sync from."
      >
        {canCreate && (
          <Button onClick={() => setDialogState({ open: true })} className="flex-1 sm:flex-none">
            <Plus className="mr-1 h-4 w-4" /> New manual proxy
          </Button>
        )}
      </EntityPageHeader>

      {isError && <ErrorBand message={describeError(manualAccountQuery.error ?? proxiesQuery.error)} />}

      {isLoading && <LoadingRow label="Loading manual proxies" />}

      {!isLoading && !isError && items.length === 0 && (
        <EmptyState
          icon={Server}
          kicker="// no manual proxies"
          title="No manual proxies yet."
          description="Add a self-hosted proxy to get started."
          action={
            canCreate ? (
              <Button onClick={() => setDialogState({ open: true })}>
                <Plus className="mr-1 h-4 w-4" /> New manual proxy
              </Button>
            ) : undefined
          }
        />
      )}

      {items.length > 0 && (
        <ol className="divide-y divide-[var(--color-border)] border-y border-[var(--color-border)]">
          {items.map((proxy) => (
            <Row
              key={proxy.id}
              proxy={proxy}
              busy={busyId === proxy.id}
              canUpdate={canUpdate}
              canDelete={canDelete}
              onEdit={() => setDialogState({ open: true, proxy })}
              onDelete={() => {
                if (window.confirm(`Delete manual proxy "${proxy.host}:${proxy.port}"?`)) {
                  deleteMutation.mutate(proxy.id);
                }
              }}
            />
          ))}
        </ol>
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

      <ManualProxyDialog
        open={dialogState.open}
        proxy={dialogState.proxy}
        onClose={() => setDialogState({ open: false })}
      />
    </div>
  );
}

// ─── Row ────────────────────────────────────────────────────────────────

function Row({
  proxy,
  busy,
  canUpdate,
  canDelete,
  onEdit,
  onDelete,
}: {
  proxy: ProxyDto;
  busy: boolean;
  canUpdate: boolean;
  canDelete: boolean;
  onEdit: () => void;
  onDelete: () => void;
}) {
  return (
    <li>
      <div className="grid grid-cols-[1fr_auto_auto] items-center gap-3 px-1 py-3.5 sm:grid-cols-[1fr_auto_1.4fr_auto_auto]">
        <div className="min-w-0">
          <div className="truncate font-mono text-[13px] font-medium">
            {proxy.host}:{proxy.port}
          </div>
          <div className="mt-0.5 font-mono text-[11px] uppercase tracking-[0.14em] text-[var(--color-muted-foreground)]">
            {proxy.protocol}
          </div>
        </div>

        <Badge variant={statusBadgeVariant(proxy.status)} className="font-mono uppercase tracking-[0.14em]">
          {proxy.status}
        </Badge>

        <div className="hidden truncate text-[12px] text-[var(--color-muted-foreground)] sm:block">
          {proxy.tags.length > 0 ? proxy.tags.join(", ") : "No tags"}
        </div>

        {canUpdate ? (
          <Button variant="ghost" size="sm" onClick={onEdit} disabled={busy}>
            Edit
          </Button>
        ) : (
          <span className="hidden sm:block" />
        )}

        {canDelete ? (
          <Button
            variant="ghost"
            size="sm"
            onClick={onDelete}
            disabled={busy}
            aria-label={`Delete ${proxy.host}:${proxy.port}`}
            className="text-[var(--color-destructive)] hover:bg-[oklch(from_var(--color-destructive)_l_c_h_/_0.08)]"
          >
            Delete
          </Button>
        ) : (
          <span className="hidden sm:block" />
        )}
      </div>
    </li>
  );
}

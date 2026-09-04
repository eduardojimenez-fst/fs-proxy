import { useState } from "react";
import { keepPreviousData, useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { toast } from "sonner";
import { Plus, RefreshCw, Send, Server, Trash2 } from "lucide-react";
import { EntityPageHeader, ErrorBand, LoadingRow, Pagination } from "@/components/list";
import { EmptyState } from "@/components/empty-state";
import { Button } from "@/components/ui/button";
import { Badge } from "@/components/ui/badge";
import { ConfirmDialog } from "@/components/ui/confirm-dialog";
import { ApiRequestError } from "@/lib/api-client";
import { cn } from "@/lib/cn";
import { ProxiesPermissions } from "@/lib/permissions";
import { useAuth } from "@/auth/use-auth";
import { listProxies } from "@/api/proxies";
import {
  deleteProviderAccount,
  listProviderAccounts,
  syncProviderAccountNow,
  type ProviderAccountDto,
} from "@/api/provider-accounts";
import { ProviderAccountDialog } from "@/components/proxies/provider-account-dialog";

const PAGE_SIZE = 20;

function describeError(err: unknown): string {
  if (err instanceof ApiRequestError) return err.problem?.detail ?? err.problem?.title ?? err.message;
  if (err instanceof Error) return err.message;
  return "Something went wrong.";
}

export function ProviderAccountsListPage() {
  const { user } = useAuth();
  const queryClient = useQueryClient();

  const [pageNumber, setPageNumber] = useState(1);
  const [dialogState, setDialogState] = useState<{ open: boolean; account?: ProviderAccountDto }>({ open: false });
  const [busyId, setBusyId] = useState<string | null>(null);
  const [deleteState, setDeleteState] = useState<{
    open: boolean;
    account?: ProviderAccountDto;
    proxyCount: number | null;
  }>({ open: false, proxyCount: null });

  const canCreate = user?.permissions.includes(ProxiesPermissions.ProviderAccounts.Create) ?? false;
  const canUpdate = user?.permissions.includes(ProxiesPermissions.ProviderAccounts.Update) ?? false;
  const canDelete = user?.permissions.includes(ProxiesPermissions.ProviderAccounts.Delete) ?? false;

  const accountsQuery = useQuery({
    queryKey: ["proxies", "provider-accounts", pageNumber],
    queryFn: () => listProviderAccounts(pageNumber, PAGE_SIZE),
    placeholderData: keepPreviousData,
  });

  const syncMutation = useMutation({
    mutationFn: (id: string) => syncProviderAccountNow(id),
    onMutate: (id) => setBusyId(id),
    onSuccess: (touched) => {
      toast.success(touched === 1 ? "Sync complete — 1 proxy touched" : `Sync complete — ${touched} proxies touched`);
      void queryClient.invalidateQueries({ queryKey: ["proxies", "provider-accounts"] });
      void queryClient.invalidateQueries({ queryKey: ["proxies", "list"] });
    },
    onError: (err) => toast.error("Sync failed", { description: describeError(err) }),
    onSettled: () => setBusyId(null),
  });

  const deleteMutation = useMutation({
    mutationFn: (input: { id: string; force: boolean }) => deleteProviderAccount(input.id, { force: input.force }),
    onMutate: (input) => setBusyId(input.id),
    onSuccess: () => {
      toast.success("Provider account deleted");
      void queryClient.invalidateQueries({ queryKey: ["proxies", "provider-accounts"] });
      void queryClient.invalidateQueries({ queryKey: ["proxies", "list"] });
      setDeleteState({ open: false, proxyCount: null });
    },
    onError: (err) => toast.error("Delete failed", { description: describeError(err) }),
    onSettled: () => setBusyId(null),
  });

  async function startDelete(account: ProviderAccountDto) {
    setDeleteState({ open: true, account, proxyCount: null });
    try {
      const result = await listProxies({ providerAccountId: account.id, pageSize: 1 });
      setDeleteState((prev) => (prev.account?.id === account.id ? { ...prev, proxyCount: result.totalCount } : prev));
    } catch {
      // Best-effort — if the count fails to load, the dialog falls back to a generic warning
      // (see deleteDescription below) rather than blocking the delete entirely.
      setDeleteState((prev) => (prev.account?.id === account.id ? { ...prev, proxyCount: -1 } : prev));
    }
  }

  const deleteDescription =
    deleteState.proxyCount === null
      ? "Checking for synced proxies…"
      : deleteState.proxyCount === -1
        ? `Delete provider account "${deleteState.account?.name}"? Could not check for synced proxies — if any exist, they will also be permanently deleted.`
        : deleteState.proxyCount === 0
          ? `Delete provider account "${deleteState.account?.name}"? It has no synced proxies.`
          : `Delete provider account "${deleteState.account?.name}"? This will permanently delete ${deleteState.proxyCount} synced ${deleteState.proxyCount === 1 ? "proxy" : "proxies"} and their tag assignments. This cannot be undone.`;

  const data = accountsQuery.data;
  const items: ProviderAccountDto[] = data?.items ?? [];

  return (
    <div className="space-y-8">
      <EntityPageHeader
        icon={Server}
        title="Provider accounts"
        total={data?.totalCount ?? null}
        unit="account"
        description="WebShare, Oxylabs, and BrightData accounts synced into the proxy pool."
      >
        <Button
          variant="outline"
          size="sm"
          disabled={accountsQuery.isFetching}
          onClick={() => accountsQuery.refetch()}
          className="flex-1 sm:flex-none"
        >
          <RefreshCw className={cn("mr-1.5 h-3.5 w-3.5", accountsQuery.isFetching && "animate-spin")} />
          Refresh
        </Button>
        {canCreate && (
          <Button onClick={() => setDialogState({ open: true })} className="flex-1 sm:flex-none">
            <Plus className="mr-1 h-4 w-4" /> New provider account
          </Button>
        )}
      </EntityPageHeader>

      {accountsQuery.isError && <ErrorBand message={describeError(accountsQuery.error)} />}

      {accountsQuery.isLoading && <LoadingRow label="Loading provider accounts" />}

      {!accountsQuery.isLoading && !accountsQuery.isError && items.length === 0 && (
        <EmptyState
          icon={Server}
          kicker="// no accounts"
          title="No provider accounts yet."
          description="Add a WebShare, Oxylabs, or BrightData account to start syncing proxies into the pool."
          action={
            canCreate ? (
              <Button onClick={() => setDialogState({ open: true })}>
                <Plus className="mr-1 h-4 w-4" /> New provider account
              </Button>
            ) : undefined
          }
        />
      )}

      {items.length > 0 && (
        <ol className="divide-y divide-[var(--color-border)] border-y border-[var(--color-border)]">
          {items.map((account) => (
            <Row
              key={account.id}
              account={account}
              busy={busyId === account.id}
              canUpdate={canUpdate}
              canDelete={canDelete}
              onSync={() => syncMutation.mutate(account.id)}
              onEdit={() => setDialogState({ open: true, account })}
              onDelete={() => void startDelete(account)}
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
          fetching={accountsQuery.isFetching}
          hasPrev={data.hasPrevious}
          hasNext={data.hasNext}
          onPrev={() => setPageNumber((p) => Math.max(1, p - 1))}
          onNext={() => setPageNumber((p) => p + 1)}
          noun="accounts"
        />
      )}

      <ProviderAccountDialog
        open={dialogState.open}
        account={dialogState.account}
        onClose={() => setDialogState({ open: false })}
      />

      <ConfirmDialog
        open={deleteState.open}
        onOpenChange={(open) => !open && setDeleteState({ open: false, proxyCount: null })}
        title="Delete provider account"
        description={deleteDescription}
        confirmLabel="Delete"
        destructive
        pending={deleteState.proxyCount === null || (deleteState.account !== undefined && busyId === deleteState.account.id)}
        onConfirm={() => {
          if (deleteState.account && deleteState.proxyCount !== null) {
            deleteMutation.mutate({ id: deleteState.account.id, force: deleteState.proxyCount !== 0 });
          }
        }}
      />
    </div>
  );
}

// ─── Row ────────────────────────────────────────────────────────────────

function Row({
  account,
  busy,
  canUpdate,
  canDelete,
  onSync,
  onEdit,
  onDelete,
}: {
  account: ProviderAccountDto;
  busy: boolean;
  canUpdate: boolean;
  canDelete: boolean;
  onSync: () => void;
  onEdit: () => void;
  onDelete: () => void;
}) {
  return (
    <li>
      <div className="grid grid-cols-[1fr_auto_auto] items-center gap-3 px-1 py-3.5 sm:grid-cols-[1.4fr_auto_1.4fr_auto_auto_auto]">
        <div className="min-w-0">
          <div className="truncate font-mono text-[13px] font-medium">{account.name}</div>
          <div className="mt-0.5 font-mono text-[11px] uppercase tracking-[0.14em] text-[var(--color-muted-foreground)]">
            {account.providerType}
          </div>
        </div>

        <Badge variant={account.isEnabled ? "success" : "muted"} className="font-mono uppercase tracking-[0.14em]">
          {account.isEnabled ? "Enabled" : "Disabled"}
        </Badge>

        <div className="hidden truncate text-[12px] text-[var(--color-muted-foreground)] sm:block">
          {account.lastSyncedAtUtc ? `Last sync: ${new Date(account.lastSyncedAtUtc).toLocaleString()}` : "Never synced"}
          {account.consecutiveSyncFailures > 0 ? ` · ${account.consecutiveSyncFailures} consecutive failures` : ""}
        </div>

        {canUpdate ? (
          <Button variant="outline" size="sm" onClick={onSync} disabled={busy}>
            <Send className="mr-1 h-3.5 w-3.5" /> Sync now
          </Button>
        ) : (
          <span className="hidden sm:block" />
        )}

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
            aria-label={`Delete ${account.name}`}
            className="text-[var(--color-destructive)] hover:bg-[oklch(from_var(--color-destructive)_l_c_h_/_0.08)]"
          >
            <Trash2 className="h-3.5 w-3.5" />
          </Button>
        ) : (
          <span className="hidden sm:block" />
        )}
      </div>
    </li>
  );
}

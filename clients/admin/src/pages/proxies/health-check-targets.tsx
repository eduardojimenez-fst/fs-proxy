import { useState } from "react";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { toast } from "sonner";
import { HeartPulse, Pencil, Plus, Trash2 } from "lucide-react";
import { EntityPageHeader, ErrorBand, LoadingRow } from "@/components/list";
import { EmptyState } from "@/components/empty-state";
import { Button } from "@/components/ui/button";
import { Badge } from "@/components/ui/badge";
import { Select } from "@/components/ui/select";
import { ApiRequestError } from "@/lib/api-client";
import { ProxiesPermissions } from "@/lib/permissions";
import { useAuth } from "@/auth/use-auth";
import { HealthCheckTargetDialog } from "@/components/proxies/health-check-target-dialog";
import {
  assignHealthCheckTargetToTag,
  deleteHealthCheckTarget,
  listHealthCheckTargets,
  unassignHealthCheckTargetFromTag,
  type HealthCheckTargetDto,
} from "@/api/health-check-targets";
import { listProxyTags } from "@/api/proxy-tags";

function describeError(err: unknown): string {
  if (err instanceof ApiRequestError) return err.problem?.detail ?? err.problem?.title ?? err.message;
  if (err instanceof Error) return err.message;
  return "Something went wrong.";
}

export function HealthCheckTargetsPage() {
  const { user } = useAuth();
  const queryClient = useQueryClient();
  const [dialogState, setDialogState] = useState<{ open: boolean; target?: HealthCheckTargetDto }>({ open: false });

  const canCreate = user?.permissions.includes(ProxiesPermissions.HealthCheckTargets.Create) ?? false;
  const canUpdate = user?.permissions.includes(ProxiesPermissions.HealthCheckTargets.Update) ?? false;
  const canDelete = user?.permissions.includes(ProxiesPermissions.HealthCheckTargets.Delete) ?? false;

  const targetsQuery = useQuery({
    queryKey: ["proxies", "health-check-targets"],
    queryFn: () => listHealthCheckTargets(),
  });
  const tagsQuery = useQuery({ queryKey: ["proxies", "tags"], queryFn: () => listProxyTags() });

  const targets = targetsQuery.data ?? [];
  const tags = tagsQuery.data ?? [];

  function invalidateAll() {
    void queryClient.invalidateQueries({ queryKey: ["proxies", "health-check-targets"] });
    void queryClient.invalidateQueries({ queryKey: ["proxies", "tags"] });
    void queryClient.invalidateQueries({ queryKey: ["proxies", "policy-resolution"] });
  }

  const deleteMutation = useMutation({
    mutationFn: (id: string) => deleteHealthCheckTarget(id),
    onSuccess: () => {
      toast.success("Target deleted");
      invalidateAll();
    },
    onError: (err) => toast.error("Delete failed", { description: describeError(err) }),
  });

  const assignMutation = useMutation({
    mutationFn: (input: { tagId: string; targetId: string }) =>
      input.targetId === ""
        ? unassignHealthCheckTargetFromTag(input.tagId)
        : assignHealthCheckTargetToTag(input.tagId, input.targetId),
    onSuccess: (_data, input) => {
      toast.success(input.targetId === "" ? "Target unassigned" : "Target assigned");
      invalidateAll();
    },
    onError: (err) => toast.error("Assignment failed", { description: describeError(err) }),
  });

  const allOnDefault = tags.length > 0 && tags.every((t) => t.healthCheckTargetId === null);

  return (
    <div className="space-y-8">
      <EntityPageHeader
        icon={HeartPulse}
        title="Health Check Targets"
        total={targets.length}
        unit="target"
        description="Where the periodic probe sends its GET through each proxy. Assigned per tag; proxies with no assigned target fall back to the configured default."
      >
        {canCreate && (
          <Button onClick={() => setDialogState({ open: true })}>
            <Plus className="mr-1 h-4 w-4" /> New target
          </Button>
        )}
      </EntityPageHeader>

      {targetsQuery.isError && <ErrorBand message={describeError(targetsQuery.error)} />}
      {targetsQuery.isLoading && <LoadingRow label="Loading health check targets" />}

      {allOnDefault && (
        <ErrorBand message="No tag has a target assigned, so every proxy is probed against the configured default URL — a generic connectivity check, not your real destination." />
      )}

      {!targetsQuery.isLoading && !targetsQuery.isError && targets.length === 0 && (
        <EmptyState
          icon={HeartPulse}
          kicker="// default only"
          title="No custom health check targets."
          description="Every proxy is probed against the default URL from configuration. Add a target pointing at the site you actually scrape to catch proxies that are up but blocked."
          action={
            canCreate ? <Button onClick={() => setDialogState({ open: true })}>Create the first target</Button> : undefined
          }
        />
      )}

      {targets.length > 0 && (
        <div className="overflow-hidden rounded-xl border border-[var(--color-border)] bg-[var(--color-card)] shadow-xs">
          <div className="grid grid-cols-[1.2fr_1.8fr_1fr_1.2fr_100px] items-center gap-3 border-b border-[var(--color-border)] bg-[var(--color-muted)]/40 px-4 py-2.5">
            {["Name", "Test URL", "Expectation", "Assigned tags", ""].map((h, i) => (
              <span
                key={h || i}
                className="text-[11.5px] font-semibold uppercase tracking-wider text-[var(--color-muted-foreground)]"
              >
                {h}
              </span>
            ))}
          </div>
          <ol className="divide-y divide-[var(--color-border)]">
            {targets.map((target) => {
              const assigned = tags.filter((t) => t.healthCheckTargetId === target.id);
              return (
                <li key={target.id} className="grid grid-cols-[1.2fr_1.8fr_1fr_1.2fr_100px] items-center gap-3 px-4 py-3">
                  <span className="truncate text-[13px] font-medium text-[var(--color-foreground)]">{target.name}</span>
                  <span className="truncate font-mono text-[11.5px] text-[var(--color-muted-foreground)]" title={target.testUrl}>
                    {target.testUrl}
                  </span>
                  <span className="font-mono text-[11.5px] text-[var(--color-muted-foreground)]">
                    {target.expectedStatusCode ?? "2xx–3xx"}
                    {target.expectedBodyKeyword ? ` · "${target.expectedBodyKeyword}"` : ""} · {target.timeoutMs}ms
                  </span>
                  <div className="flex min-w-0 flex-wrap gap-1">
                    {assigned.length > 0 ? (
                      assigned.map((t) => (
                        <Badge key={t.id} variant="muted" className="font-mono text-[10.5px]">
                          {t.name}
                        </Badge>
                      ))
                    ) : (
                      <span className="text-[12px] text-[var(--color-muted-foreground)]" title="No proxy is probed with this target">
                        — unused
                      </span>
                    )}
                  </div>
                  <div className="flex items-center justify-end gap-1">
                    {canUpdate && (
                      <Button
                        variant="ghost"
                        size="sm"
                        aria-label={`Edit ${target.name}`}
                        onClick={() => setDialogState({ open: true, target })}
                      >
                        <Pencil className="h-3.5 w-3.5" />
                      </Button>
                    )}
                    {canDelete && (
                      <Button
                        variant="ghost"
                        size="sm"
                        aria-label={`Delete ${target.name}`}
                        disabled={deleteMutation.isPending}
                        onClick={() => deleteMutation.mutate(target.id)}
                      >
                        <Trash2 className="h-3.5 w-3.5" />
                      </Button>
                    )}
                  </div>
                </li>
              );
            })}
          </ol>
        </div>
      )}

      <section className="space-y-3">
        <div>
          <h2 className="text-[15px] font-semibold text-[var(--color-foreground)]">Tag assignments</h2>
          <p className="text-[12.5px] text-[var(--color-muted-foreground)]">
            A proxy is probed against every target its tags assign — one usage event per target. With none assigned it
            falls back to the default URL from configuration.
          </p>
        </div>

        {tagsQuery.isLoading && <LoadingRow label="Loading tags" />}

        {tags.length > 0 && (
          <div className="overflow-hidden rounded-xl border border-[var(--color-border)] bg-[var(--color-card)] shadow-xs">
            <ol className="divide-y divide-[var(--color-border)]">
              {tags.map((tag) => (
                <li key={tag.id} className="flex items-center justify-between gap-3 px-4 py-2.5">
                  <span className="truncate font-mono text-[12.5px] text-[var(--color-foreground)]">{tag.name}</span>
                  <div data-testid={`target-assign-${tag.name}`}>
                    <Select
                      value={tag.healthCheckTargetId ?? ""}
                      onChange={(v) => assignMutation.mutate({ tagId: tag.id, targetId: v })}
                      options={targets.map((t) => ({ value: t.id, label: t.name }))}
                      placeholder="Default target"
                      minWidth="14rem"
                      disabled={!canUpdate || assignMutation.isPending}
                    />
                  </div>
                </li>
              ))}
            </ol>
          </div>
        )}
      </section>

      <HealthCheckTargetDialog
        open={dialogState.open}
        target={dialogState.target}
        onClose={() => setDialogState({ open: false })}
      />
    </div>
  );
}

import { useState } from "react";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { toast } from "sonner";
import { Pencil, Plus, ShieldCheck, Trash2 } from "lucide-react";
import { EntityPageHeader, ErrorBand, LoadingRow } from "@/components/list";
import { EmptyState } from "@/components/empty-state";
import { Button } from "@/components/ui/button";
import { Badge } from "@/components/ui/badge";
import { Select } from "@/components/ui/select";
import { ApiRequestError } from "@/lib/api-client";
import { ProxiesPermissions } from "@/lib/permissions";
import { useAuth } from "@/auth/use-auth";
import { PolicyProfileDialog } from "@/components/proxies/policy-profile-dialog";
import {
  assignPolicyToTag,
  deletePolicyProfile,
  listPolicyProfiles,
  unassignPolicyFromTag,
  type PolicyProfileDto,
  type PolicyProfileType,
} from "@/api/policies";
import { listProxyTags } from "@/api/proxy-tags";

function describeError(err: unknown): string {
  if (err instanceof ApiRequestError) return err.problem?.detail ?? err.problem?.title ?? err.message;
  if (err instanceof Error) return err.message;
  return "Something went wrong.";
}

const TYPE_LABEL: Record<PolicyProfileType, string> = {
  Manual: "Manual",
  AutoDisable: "Auto-disable",
  AutoDisableAndRenew: "Auto-disable & renew",
};

function typeVariant(type: PolicyProfileType): React.ComponentProps<typeof Badge>["variant"] {
  switch (type) {
    case "AutoDisableAndRenew":
      return "danger";
    case "AutoDisable":
      return "warning";
    case "Manual":
    default:
      return "muted";
  }
}

export function PoliciesPage() {
  const { user } = useAuth();
  const queryClient = useQueryClient();
  const [dialogState, setDialogState] = useState<{ open: boolean; profile?: PolicyProfileDto }>({ open: false });

  const canCreate = user?.permissions.includes(ProxiesPermissions.Policies.Create) ?? false;
  const canUpdate = user?.permissions.includes(ProxiesPermissions.Policies.Update) ?? false;
  const canDelete = user?.permissions.includes(ProxiesPermissions.Policies.Delete) ?? false;

  const policiesQuery = useQuery({ queryKey: ["proxies", "policies"], queryFn: () => listPolicyProfiles() });
  const tagsQuery = useQuery({ queryKey: ["proxies", "tags"], queryFn: () => listProxyTags() });

  const policies = policiesQuery.data ?? [];
  const tags = tagsQuery.data ?? [];

  function invalidateAll() {
    void queryClient.invalidateQueries({ queryKey: ["proxies", "policies"] });
    void queryClient.invalidateQueries({ queryKey: ["proxies", "tags"] });
    // Changing an assignment changes which policy governs every proxy carrying that tag.
    void queryClient.invalidateQueries({ queryKey: ["proxies", "policy-resolution"] });
  }

  const deleteMutation = useMutation({
    mutationFn: (id: string) => deletePolicyProfile(id),
    onSuccess: () => {
      toast.success("Policy deleted");
      invalidateAll();
    },
    onError: (err) => toast.error("Delete failed", { description: describeError(err) }),
  });

  // Assignment rides through mutate(arg): the selects are rendered per row, so closing over
  // component state here would apply the wrong tag when two changes land in quick succession.
  const assignMutation = useMutation({
    mutationFn: (input: { tagId: string; policyProfileId: string }) =>
      input.policyProfileId === ""
        ? unassignPolicyFromTag(input.tagId)
        : assignPolicyToTag(input.tagId, input.policyProfileId),
    onSuccess: (_data, input) => {
      toast.success(input.policyProfileId === "" ? "Policy unassigned" : "Policy assigned");
      invalidateAll();
    },
    onError: (err) => toast.error("Assignment failed", { description: describeError(err) }),
  });

  const untaggedNotice = tags.length > 0 && tags.every((t) => t.policyProfileId === null);

  return (
    <div className="space-y-8">
      <EntityPageHeader
        icon={ShieldCheck}
        title="Policies"
        total={policies.length}
        unit="profile"
        description="Rules that auto-disable a proxy after repeated failures. A policy only acts on a proxy through the tags it is assigned to."
      >
        {canCreate && (
          <Button onClick={() => setDialogState({ open: true })}>
            <Plus className="mr-1 h-4 w-4" /> New policy
          </Button>
        )}
      </EntityPageHeader>

      {policiesQuery.isError && <ErrorBand message={describeError(policiesQuery.error)} />}
      {policiesQuery.isLoading && <LoadingRow label="Loading policies" />}

      {!policiesQuery.isLoading && !policiesQuery.isError && policies.length === 0 && (
        <EmptyState
          icon={ShieldCheck}
          kicker="// nothing enforced"
          title="No policy profiles yet."
          description="Without a policy, failures are recorded but no proxy is ever disabled automatically — however many failures it collects."
          action={
            canCreate ? <Button onClick={() => setDialogState({ open: true })}>Create the first policy</Button> : undefined
          }
        />
      )}

      {policies.length > 0 && (
        <div className="overflow-hidden rounded-xl border border-[var(--color-border)] bg-[var(--color-card)] shadow-xs">
          <div className="grid grid-cols-[1.3fr_190px_1fr_1.2fr_100px] items-center gap-3 border-b border-[var(--color-border)] bg-[var(--color-muted)]/40 px-4 py-2.5">
            {["Name", "Type", "Trips after", "Assigned tags", ""].map((h, i) => (
              <span
                key={h || i}
                className="text-[11.5px] font-semibold uppercase tracking-wider text-[var(--color-muted-foreground)]"
              >
                {h}
              </span>
            ))}
          </div>
          <ol className="divide-y divide-[var(--color-border)]">
            {policies.map((policy) => {
              const assigned = tags.filter((t) => t.policyProfileId === policy.id);
              return (
                <li key={policy.id} className="grid grid-cols-[1.3fr_190px_1fr_1.2fr_100px] items-center gap-3 px-4 py-3">
                  <span className="truncate text-[13px] font-medium text-[var(--color-foreground)]">{policy.name}</span>
                  <Badge variant={typeVariant(policy.type)} className="w-fit font-mono text-[10.5px] uppercase tracking-[0.12em]">
                    {TYPE_LABEL[policy.type]}
                  </Badge>
                  <span className="font-mono text-[11.5px] text-[var(--color-muted-foreground)]">
                    {policy.failureThreshold} fails / {policy.windowMinutes}m / {policy.minDistinctReporters} rep
                  </span>
                  <div className="flex min-w-0 flex-wrap gap-1">
                    {assigned.length > 0 ? (
                      assigned.map((t) => (
                        <Badge key={t.id} variant="muted" className="font-mono text-[10.5px]">
                          {t.name}
                        </Badge>
                      ))
                    ) : (
                      <span className="text-[12px] text-[var(--color-muted-foreground)]" title="This policy affects nothing">
                        — unused
                      </span>
                    )}
                  </div>
                  <div className="flex items-center justify-end gap-1">
                    {canUpdate && (
                      <Button
                        variant="ghost"
                        size="sm"
                        aria-label={`Edit ${policy.name}`}
                        onClick={() => setDialogState({ open: true, profile: policy })}
                      >
                        <Pencil className="h-3.5 w-3.5" />
                      </Button>
                    )}
                    {canDelete && (
                      <Button
                        variant="ghost"
                        size="sm"
                        aria-label={`Delete ${policy.name}`}
                        disabled={deleteMutation.isPending}
                        onClick={() => deleteMutation.mutate(policy.id)}
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

      {/* Assignment — the only thing that makes a policy affect real proxies. */}
      <section className="space-y-3">
        <div>
          <h2 className="text-[15px] font-semibold text-[var(--color-foreground)]">Tag assignments</h2>
          <p className="text-[12.5px] text-[var(--color-muted-foreground)]">
            A proxy inherits a policy from its tags. When several of its tags carry a policy, the most restrictive one
            wins (auto-disable &amp; renew &gt; auto-disable &gt; manual). A proxy with no tags is never auto-disabled.
          </p>
        </div>

        {untaggedNotice && (
          <ErrorBand message="No tag has a policy assigned, so nothing is auto-disabled yet. Assign one below to activate the engine." />
        )}

        {tagsQuery.isLoading && <LoadingRow label="Loading tags" />}

        {tags.length > 0 && (
          <div className="overflow-hidden rounded-xl border border-[var(--color-border)] bg-[var(--color-card)] shadow-xs">
            <ol className="divide-y divide-[var(--color-border)]">
              {tags.map((tag) => (
                <li key={tag.id} className="flex items-center justify-between gap-3 px-4 py-2.5">
                  <span className="truncate font-mono text-[12.5px] text-[var(--color-foreground)]">{tag.name}</span>
                  <div data-testid={`policy-assign-${tag.name}`}>
                    <Select
                      value={tag.policyProfileId ?? ""}
                      onChange={(v) => assignMutation.mutate({ tagId: tag.id, policyProfileId: v })}
                      options={policies.map((p) => ({ value: p.id, label: p.name }))}
                      placeholder="No policy"
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

      <PolicyProfileDialog
        open={dialogState.open}
        profile={dialogState.profile}
        onClose={() => setDialogState({ open: false })}
      />
    </div>
  );
}

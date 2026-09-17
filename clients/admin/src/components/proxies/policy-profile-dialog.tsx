import { useEffect } from "react";
import { useForm } from "react-hook-form";
import { zodResolver } from "@hookform/resolvers/zod";
import { z } from "zod";
import { useMutation, useQueryClient } from "@tanstack/react-query";
import { toast } from "sonner";
import { Loader2 } from "lucide-react";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import { Dialog, DialogBody, DialogContent, DialogFooter, DialogHeader, DialogTitle } from "@/components/ui/dialog";
import { Field } from "@/components/list";
import { ApiRequestError } from "@/lib/api-client";
import {
  createPolicyProfile,
  updatePolicyProfile,
  type PolicyProfileDto,
  type PolicyProfileType,
} from "@/api/policies";

const TYPES: { value: PolicyProfileType; label: string; hint: string }[] = [
  { value: "Manual", label: "Manual", hint: "Records failures but never disables — an operator decides." },
  { value: "AutoDisable", label: "Auto-disable", hint: "Disables the proxy once the threshold is met." },
  {
    value: "AutoDisableAndRenew",
    label: "Auto-disable & renew",
    hint: "Disables it and asks the provider for a replacement IP.",
  },
];

const schema = z.object({
  name: z.string().trim().min(2, "At least 2 characters.").max(128),
  type: z.enum(["Manual", "AutoDisable", "AutoDisableAndRenew"]),
  failureThreshold: z.coerce.number().int().min(1, "At least 1.").max(1000),
  windowMinutes: z.coerce.number().int().min(1, "At least 1.").max(10080),
  minDistinctReporters: z.coerce.number().int().min(1, "At least 1.").max(100),
});

type FormValues = z.infer<typeof schema>;

const DEFAULTS: FormValues = {
  name: "",
  type: "AutoDisable",
  failureThreshold: 3,
  windowMinutes: 30,
  minDistinctReporters: 1,
};

export function PolicyProfileDialog({
  open,
  onClose,
  profile,
}: {
  open: boolean;
  onClose: () => void;
  profile?: PolicyProfileDto;
}) {
  const queryClient = useQueryClient();
  const isEdit = Boolean(profile);

  const {
    register,
    handleSubmit,
    reset,
    watch,
    formState: { errors, isSubmitting },
  } = useForm<FormValues>({ resolver: zodResolver(schema), defaultValues: DEFAULTS });

  useEffect(() => {
    reset(
      profile
        ? {
            name: profile.name,
            type: profile.type,
            failureThreshold: profile.failureThreshold,
            windowMinutes: profile.windowMinutes,
            minDistinctReporters: profile.minDistinctReporters,
          }
        : DEFAULTS,
    );
  }, [profile, reset]);

  const selectedType = watch("type");
  const threshold = watch("failureThreshold");
  const windowMinutes = watch("windowMinutes");
  const reporters = watch("minDistinctReporters");

  const mutation = useMutation({
    // Values ride through mutate(arg) — nothing closed over at submit time.
    mutationFn: async (values: FormValues) => {
      if (isEdit) {
        await updatePolicyProfile(profile!.id, values);
      } else {
        await createPolicyProfile(values);
      }
    },
    onSuccess: () => {
      toast.success(isEdit ? "Policy updated" : "Policy created");
      void queryClient.invalidateQueries({ queryKey: ["proxies", "policies"] });
      // A changed threshold/window changes every proxy's resolution explanation.
      void queryClient.invalidateQueries({ queryKey: ["proxies", "policy-resolution"] });
      onClose();
    },
    onError: (err) => {
      const detail =
        err instanceof ApiRequestError ? (err.problem?.detail ?? err.problem?.title ?? err.message) : (err as Error).message;
      toast.error(isEdit ? "Update failed" : "Create failed", { description: detail });
    },
  });

  const submitting = isSubmitting || mutation.isPending;

  return (
    <Dialog open={open} onOpenChange={(o) => !o && onClose()}>
      <DialogContent>
        <DialogHeader>
          <DialogTitle>{isEdit ? "Edit policy profile" : "New policy profile"}</DialogTitle>
        </DialogHeader>
        <form onSubmit={handleSubmit((values) => mutation.mutate(values))}>
          <DialogBody className="space-y-4">
            <Field id="pp-name" label="Name" required error={errors.name?.message}>
              <Input id="pp-name" autoComplete="off" placeholder="Strict — scraping" {...register("name")} />
            </Field>

            <Field
              id="pp-type"
              label="Type"
              required
              hint={TYPES.find((t) => t.value === selectedType)?.hint}
              error={errors.type?.message}
            >
              <select
                id="pp-type"
                className="h-9 w-full rounded-md border border-[var(--color-input)] bg-transparent px-3 text-[13px] outline-none focus-visible:border-[var(--color-ring)] focus-visible:ring-[3px] focus-visible:ring-[oklch(from_var(--color-ring)_l_c_h_/_0.5)]"
                {...register("type")}
              >
                {TYPES.map((t) => (
                  <option key={t.value} value={t.value}>
                    {t.label}
                  </option>
                ))}
              </select>
            </Field>

            <div className="grid grid-cols-3 gap-3">
              <Field id="pp-threshold" label="Failures" required error={errors.failureThreshold?.message}>
                <Input id="pp-threshold" type="number" min={1} {...register("failureThreshold")} />
              </Field>
              <Field id="pp-window" label="Window (min)" required error={errors.windowMinutes?.message}>
                <Input id="pp-window" type="number" min={1} {...register("windowMinutes")} />
              </Field>
              <Field id="pp-reporters" label="Reporters" required error={errors.minDistinctReporters?.message}>
                <Input id="pp-reporters" type="number" min={1} {...register("minDistinctReporters")} />
              </Field>
            </div>

            <p className="rounded-lg border border-[var(--color-border)] bg-[var(--color-muted)]/40 px-3 py-2 text-[12px] text-[var(--color-muted-foreground)]">
              Disable a proxy after <strong>{threshold || "?"}</strong> failed events within{" "}
              <strong>{windowMinutes || "?"}</strong> minutes, reported by at least{" "}
              <strong>{reporters || "?"}</strong> distinct source{Number(reporters) === 1 ? "" : "s"}. All health checks
              count as a single source.
            </p>
          </DialogBody>
          <DialogFooter>
            <Button type="button" variant="outline" onClick={onClose} disabled={submitting}>
              Cancel
            </Button>
            <Button type="submit" disabled={submitting} className="min-w-[8.5rem]">
              {submitting ? (
                <>
                  <Loader2 className="size-4 animate-spin" aria-hidden />
                  <span>Saving…</span>
                </>
              ) : (
                "Save"
              )}
            </Button>
          </DialogFooter>
        </form>
      </DialogContent>
    </Dialog>
  );
}

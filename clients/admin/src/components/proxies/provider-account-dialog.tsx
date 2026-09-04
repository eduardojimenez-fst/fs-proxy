import { useEffect, useState } from "react";
import { Controller, useForm } from "react-hook-form";
import { zodResolver } from "@hookform/resolvers/zod";
import { z } from "zod";
import { useMutation, useQueryClient } from "@tanstack/react-query";
import { toast } from "sonner";
import { Eye, EyeOff, Loader2 } from "lucide-react";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import { Select } from "@/components/ui/select";
import { Switch } from "@/components/ui/switch";
import {
  Dialog,
  DialogBody,
  DialogContent,
  DialogDescription,
  DialogFooter,
  DialogHeader,
  DialogTitle,
} from "@/components/ui/dialog";
import { Field } from "@/components/list";
import { ApiRequestError } from "@/lib/api-client";
import {
  createProviderAccount,
  updateProviderAccount,
  type ProviderAccountDto,
  type ProxyProviderType,
} from "@/api/provider-accounts";

// Only the syncable provider types are offered here — "Manual" accounts
// aren't created through this dialog (that's the manual-proxies flow).
const PROVIDER_OPTIONS: { value: ProxyProviderType; label: string }[] = [
  { value: "WebShare", label: "WebShare" },
  { value: "Oxylabs", label: "Oxylabs" },
  { value: "BrightData", label: "BrightData" },
];

const baseSchema = z.object({
  name: z.string().trim().min(2, "At least 2 characters.").max(128),
  providerType: z.enum(["WebShare", "Oxylabs", "BrightData"]),
  plaintextCredentials: z.string().trim(),
  isEnabled: z.boolean(),
});

type FormValues = z.infer<typeof baseSchema>;

/**
 * Credentials are required on create (there's nothing to fall back to) but
 * optional on edit — a blank value means "keep the current credentials".
 * Built per-mode rather than baked into `baseSchema` so both cases validate
 * correctly with the same field.
 */
function buildSchema(isEdit: boolean) {
  return baseSchema.superRefine((values, ctx) => {
    if (!isEdit && values.plaintextCredentials.length === 0) {
      ctx.addIssue({ code: z.ZodIssueCode.custom, path: ["plaintextCredentials"], message: "Required." });
    }
  });
}

function credentialsPlaceholder(providerType: ProxyProviderType): string {
  switch (providerType) {
    case "BrightData":
      return '{"apiToken":"...","zone":"...","customerId":"...","gatewayPort":44445}';
    case "Oxylabs":
      return '{"username":"...","password":"..."}';
    case "WebShare":
    default:
      return '{"apiKey":"..."}';
  }
}

export function ProviderAccountDialog({
  open,
  onClose,
  account,
}: {
  open: boolean;
  onClose: () => void;
  account?: ProviderAccountDto;
}) {
  const queryClient = useQueryClient();
  const isEdit = Boolean(account);
  const [showCredentials, setShowCredentials] = useState(false);

  const {
    register,
    handleSubmit,
    control,
    reset,
    watch,
    formState: { errors, isSubmitting },
  } = useForm<FormValues>({
    resolver: zodResolver(buildSchema(isEdit)),
    defaultValues: {
      name: "",
      providerType: "WebShare",
      plaintextCredentials: "",
      isEnabled: true,
    },
  });

  // Reset the form whenever the target account changes (including back to
  // "create" when the dialog is reopened without an account).
  useEffect(() => {
    if (account) {
      reset({
        name: account.name,
        // "Manual" accounts are never edited through this dialog's provider
        // picker (it's hidden on edit anyway), but keep the type narrow.
        providerType: account.providerType === "Manual" ? "WebShare" : account.providerType,
        plaintextCredentials: "",
        isEnabled: account.isEnabled,
      });
    } else {
      reset({ name: "", providerType: "WebShare", plaintextCredentials: "", isEnabled: true });
    }
    setShowCredentials(false);
  }, [account, reset]);

  const mutation = useMutation({
    // Pass values via mutate(arg) — no closed-over state captured at submit time.
    mutationFn: async (values: FormValues) => {
      if (isEdit) {
        await updateProviderAccount({
          id: account!.id,
          name: values.name,
          plaintextCredentials: values.plaintextCredentials.trim() ? values.plaintextCredentials : undefined,
          isEnabled: values.isEnabled,
        });
      } else {
        await createProviderAccount({
          name: values.name,
          providerType: values.providerType,
          plaintextCredentials: values.plaintextCredentials,
        });
      }
    },
    onSuccess: () => {
      toast.success(isEdit ? "Provider account updated" : "Provider account created");
      void queryClient.invalidateQueries({ queryKey: ["proxies", "provider-accounts"] });
      onClose();
    },
    onError: (err) => {
      const detail =
        err instanceof ApiRequestError ? (err.problem?.detail ?? err.problem?.title ?? err.message) : (err as Error).message;
      toast.error(isEdit ? "Update failed" : "Create failed", { description: detail });
    },
  });

  const submitting = isSubmitting || mutation.isPending;
  const watchedProviderType = watch("providerType");
  const effectiveProviderType = isEdit ? (account!.providerType === "Manual" ? "WebShare" : account!.providerType) : watchedProviderType;

  return (
    <Dialog open={open} onOpenChange={(o) => !o && onClose()}>
      <DialogContent>
        <DialogHeader>
          <DialogTitle>{isEdit ? "Edit provider account" : "New provider account"}</DialogTitle>
          <DialogDescription>
            {isEdit
              ? "Update the name, enabled state, or rotate its credentials."
              : "Connect a WebShare, Oxylabs, or BrightData account to sync proxies from."}
          </DialogDescription>
        </DialogHeader>

        <form onSubmit={handleSubmit((values) => mutation.mutate(values))}>
          <DialogBody className="space-y-4">
            <Field id="pa-name" label="Name" required error={errors.name?.message}>
              <Input id="pa-name" autoComplete="off" placeholder="WebShare — main" {...register("name")} />
            </Field>

            {!isEdit && (
              <Field id="pa-provider" label="Provider" required error={errors.providerType?.message}>
                <Controller
                  control={control}
                  name="providerType"
                  render={({ field }) => (
                    <Select
                      value={field.value}
                      onChange={field.onChange}
                      options={PROVIDER_OPTIONS}
                      className="w-full"
                      minWidth="100%"
                    />
                  )}
                />
              </Field>
            )}

            {/*
              Credentials never get logged: no console.log of form values
              anywhere in this component, and the field defaults to a masked
              password input (toggle to reveal) since this is a secrets field.
            */}
            <Field
              id="pa-credentials"
              label={isEdit ? "Replace credentials (leave blank to keep current)" : "Credentials (JSON)"}
              required={!isEdit}
              hint="Sent once over the wire and encrypted at rest by the server."
              error={errors.plaintextCredentials?.message}
            >
              <div className="relative">
                <Input
                  id="pa-credentials"
                  type={showCredentials ? "text" : "password"}
                  autoComplete="off"
                  placeholder={credentialsPlaceholder(effectiveProviderType)}
                  className="pr-10 font-mono"
                  {...register("plaintextCredentials")}
                />
                <button
                  type="button"
                  onClick={() => setShowCredentials((s) => !s)}
                  aria-label={showCredentials ? "Hide credentials" : "Show credentials"}
                  className="absolute inset-y-0 right-1.5 grid w-7 place-items-center rounded-md text-[var(--color-muted-foreground)] outline-none transition-colors hover:bg-[var(--color-accent)] hover:text-[var(--color-foreground)] focus-visible:ring-2 focus-visible:ring-[oklch(from_var(--color-ring)_l_c_h_/_0.5)]"
                >
                  {showCredentials ? <EyeOff className="size-3.5" aria-hidden /> : <Eye className="size-3.5" aria-hidden />}
                </button>
              </div>
            </Field>

            {isEdit && (
              <Field id="pa-enabled" label="Enabled">
                <Controller
                  control={control}
                  name="isEnabled"
                  render={({ field }) => (
                    <Switch id="pa-enabled" checked={field.value} onCheckedChange={field.onChange} />
                  )}
                />
              </Field>
            )}
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

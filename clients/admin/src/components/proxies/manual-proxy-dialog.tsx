import { useEffect } from "react";
import { useForm } from "react-hook-form";
import { zodResolver } from "@hookform/resolvers/zod";
import { z } from "zod";
import { useMutation, useQueryClient } from "@tanstack/react-query";
import { toast } from "sonner";
import { Loader2 } from "lucide-react";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
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
import { createManualProxy, updateManualProxy } from "@/api/manual-proxies";
import type { ProxyDto } from "@/api/proxies";

// Username/password are both genuinely optional here — CreateManualProxyCommandValidator
// has no NotEmpty rule on either (unlike provider-account credentials), so a manual
// proxy can be registered with no auth at all.
const schema = z.object({
  host: z.string().trim().min(1, "Required.").max(255),
  port: z.coerce.number().int().min(1, "Required.").max(65535),
  username: z.string().trim().optional(),
  plaintextPassword: z.string().trim().optional(),
  tagsInput: z.string().trim(),
});

type FormValues = z.infer<typeof schema>;

export function ManualProxyDialog({
  open,
  onClose,
  proxy,
}: {
  open: boolean;
  onClose: () => void;
  proxy?: ProxyDto;
}) {
  const queryClient = useQueryClient();
  const isEdit = Boolean(proxy);

  const {
    register,
    handleSubmit,
    reset,
    formState: { errors, isSubmitting },
  } = useForm<FormValues>({
    resolver: zodResolver(schema),
    defaultValues: { host: "", port: 3128, username: "", plaintextPassword: "", tagsInput: "" },
  });

  // Reset the form whenever the target proxy changes (including back to "create"
  // when the dialog is reopened without a proxy). Username/password are never
  // pre-filled on edit — the server never returns the plaintext password back,
  // and a blank password on submit means "keep the current one" (see mutationFn
  // below), so there's nothing correct to pre-fill anyway.
  useEffect(() => {
    reset(
      proxy
        ? { host: proxy.host, port: proxy.port, username: "", plaintextPassword: "", tagsInput: proxy.tags.join(", ") }
        : { host: "", port: 3128, username: "", plaintextPassword: "", tagsInput: "" },
    );
  }, [proxy, reset]);

  const mutation = useMutation({
    // Pass values via mutate(arg) — no closed-over state captured at submit time.
    mutationFn: async (values: FormValues): Promise<void> => {
      const tagNames = values.tagsInput
        .split(",")
        .map((t) => t.trim())
        .filter(Boolean);
      const shared = {
        host: values.host,
        port: values.port,
        protocol: "Http" as const,
        username: values.username ? values.username : undefined,
        // Blank means "keep the current password" server-side —
        // UpdateManualProxyCommandHandler falls back to the existing
        // ProtectedPassword whenever PlaintextPassword is null/whitespace.
        plaintextPassword: values.plaintextPassword ? values.plaintextPassword : undefined,
        tagNames,
      };
      if (isEdit) {
        await updateManualProxy({ id: proxy!.id, ...shared });
      } else {
        await createManualProxy(shared);
      }
    },
    onSuccess: () => {
      toast.success(isEdit ? "Manual proxy updated" : "Manual proxy created");
      void queryClient.invalidateQueries({ queryKey: ["proxies", "list"] });
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
          <DialogTitle>{isEdit ? "Edit manual proxy" : "New manual proxy"}</DialogTitle>
          <DialogDescription>
            {isEdit
              ? "Update its connection details, credentials, or tags."
              : "Register a self-hosted proxy with no provider API to sync from."}
          </DialogDescription>
        </DialogHeader>

        <form onSubmit={handleSubmit((values) => mutation.mutate(values))}>
          <DialogBody className="space-y-4">
            <div className="grid grid-cols-[1fr_120px] gap-3">
              <Field id="mp-host" label="Host" required error={errors.host?.message}>
                <Input id="mp-host" autoComplete="off" placeholder="10.0.0.5" {...register("host")} />
              </Field>
              <Field id="mp-port" label="Port" required error={errors.port?.message}>
                <Input id="mp-port" type="number" {...register("port")} />
              </Field>
            </div>

            <Field id="mp-username" label="Username" error={errors.username?.message}>
              <Input id="mp-username" autoComplete="off" {...register("username")} />
            </Field>

            <Field
              id="mp-password"
              label={isEdit ? "Replace password (leave blank to keep current)" : "Password"}
              error={errors.plaintextPassword?.message}
            >
              <Input id="mp-password" type="password" autoComplete="new-password" {...register("plaintextPassword")} />
            </Field>

            <Field id="mp-tags" label="Tags" hint="Comma-separated." error={errors.tagsInput?.message}>
              <Input
                id="mp-tags"
                autoComplete="off"
                placeholder="pais:cl, funcionalidad:licitaciones"
                {...register("tagsInput")}
              />
            </Field>
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

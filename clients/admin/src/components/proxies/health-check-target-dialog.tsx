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
  createHealthCheckTarget,
  updateHealthCheckTarget,
  type HealthCheckTargetDto,
} from "@/api/health-check-targets";

// Both expectations are genuinely optional server-side: no expected status means "any 2xx–3xx",
// no keyword means the body is never read.
const schema = z.object({
  name: z.string().trim().min(2, "At least 2 characters.").max(128),
  testUrl: z.string().trim().url("Must be an absolute URL."),
  expectedStatusCode: z
    .union([z.literal(""), z.coerce.number().int().min(100).max(599)])
    .optional(),
  expectedBodyKeyword: z.string().trim().max(256).optional(),
  timeoutMs: z.coerce.number().int().min(500, "At least 500ms.").max(30000, "At most 30000ms."),
});

type FormValues = z.infer<typeof schema>;

const DEFAULTS: FormValues = {
  name: "",
  testUrl: "",
  expectedStatusCode: "",
  expectedBodyKeyword: "",
  timeoutMs: 5000,
};

export function HealthCheckTargetDialog({
  open,
  onClose,
  target,
}: {
  open: boolean;
  onClose: () => void;
  target?: HealthCheckTargetDto;
}) {
  const queryClient = useQueryClient();
  const isEdit = Boolean(target);

  const {
    register,
    handleSubmit,
    reset,
    formState: { errors, isSubmitting },
  } = useForm<FormValues>({ resolver: zodResolver(schema), defaultValues: DEFAULTS });

  useEffect(() => {
    reset(
      target
        ? {
            name: target.name,
            testUrl: target.testUrl,
            expectedStatusCode: target.expectedStatusCode ?? "",
            expectedBodyKeyword: target.expectedBodyKeyword ?? "",
            timeoutMs: target.timeoutMs,
          }
        : DEFAULTS,
    );
  }, [target, reset]);

  const mutation = useMutation({
    mutationFn: async (values: FormValues) => {
      const body = {
        name: values.name,
        testUrl: values.testUrl,
        // "" means "no expectation" — send null, not 0 or an empty string.
        expectedStatusCode: values.expectedStatusCode === "" || values.expectedStatusCode === undefined
          ? null
          : Number(values.expectedStatusCode),
        expectedBodyKeyword: values.expectedBodyKeyword ? values.expectedBodyKeyword : null,
        timeoutMs: values.timeoutMs,
      };
      if (isEdit) {
        await updateHealthCheckTarget(target!.id, body);
      } else {
        await createHealthCheckTarget(body);
      }
    },
    onSuccess: () => {
      toast.success(isEdit ? "Target updated" : "Target created");
      void queryClient.invalidateQueries({ queryKey: ["proxies", "health-check-targets"] });
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
          <DialogTitle>{isEdit ? "Edit health-check target" : "New health-check target"}</DialogTitle>
        </DialogHeader>
        <form onSubmit={handleSubmit((values) => mutation.mutate(values))}>
          <DialogBody className="space-y-4">
            <Field id="hct-name" label="Name" required error={errors.name?.message}>
              <Input id="hct-name" autoComplete="off" placeholder="Mercado Público" {...register("name")} />
            </Field>

            <Field
              id="hct-url"
              label="Test URL"
              required
              hint="Probed with a GET through the proxy."
              error={errors.testUrl?.message}
            >
              <Input id="hct-url" autoComplete="off" placeholder="https://www.mercadopublico.cl" {...register("testUrl")} />
            </Field>

            <div className="grid grid-cols-2 gap-3">
              <Field
                id="hct-status"
                label="Expected status"
                hint="Blank = any 2xx–3xx."
                error={errors.expectedStatusCode?.message}
              >
                <Input id="hct-status" type="number" placeholder="200" {...register("expectedStatusCode")} />
              </Field>
              <Field id="hct-timeout" label="Timeout (ms)" required error={errors.timeoutMs?.message}>
                <Input id="hct-timeout" type="number" min={500} max={30000} {...register("timeoutMs")} />
              </Field>
            </div>

            <Field
              id="hct-keyword"
              label="Expected body keyword"
              hint="Blank = the body is never read. Catches captive portals and block pages that still answer 200."
              error={errors.expectedBodyKeyword?.message}
            >
              <Input id="hct-keyword" autoComplete="off" placeholder="licitacion" {...register("expectedBodyKeyword")} />
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

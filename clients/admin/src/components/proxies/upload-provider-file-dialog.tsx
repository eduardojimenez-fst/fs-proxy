import { useState } from "react";
import { useMutation, useQueryClient } from "@tanstack/react-query";
import { toast } from "sonner";
import { Loader2 } from "lucide-react";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import { Select } from "@/components/ui/select";
import { Dialog, DialogBody, DialogContent, DialogFooter, DialogHeader, DialogTitle } from "@/components/ui/dialog";
import { Field } from "@/components/list";
import { ApiRequestError } from "@/lib/api-client";
import { syncProviderAccountFromFile, type FileImportResult, type ProviderAccountDto } from "@/api/provider-accounts";
import type { ProxyKind } from "@/api/proxies";

const PROXY_KIND_OPTIONS: { value: ProxyKind; label: string }[] = [
  { value: "DataCenter", label: "DataCenter" },
  { value: "Residential", label: "Residential" },
  { value: "Mobile", label: "Mobile" },
  { value: "Dedicated", label: "Dedicated" },
];

function describeError(err: unknown): string {
  if (err instanceof ApiRequestError) return err.problem?.detail ?? err.problem?.title ?? err.message;
  if (err instanceof Error) return err.message;
  return "Something went wrong.";
}

export function UploadProviderFileDialog({
  open,
  account,
  onClose,
}: {
  open: boolean;
  account: ProviderAccountDto | null;
  onClose: () => void;
}) {
  const queryClient = useQueryClient();
  const [file, setFile] = useState<File | null>(null);
  const [defaultUsername, setDefaultUsername] = useState("");
  const [defaultPassword, setDefaultPassword] = useState("");
  const [defaultGeolocation, setDefaultGeolocation] = useState("");
  const [defaultProxyKind, setDefaultProxyKind] = useState<ProxyKind | "">("");
  const [result, setResult] = useState<FileImportResult | null>(null);

  const mutation = useMutation({
    mutationFn: () =>
      syncProviderAccountFromFile(account!.id, {
        file: file!,
        defaultUsername: defaultUsername || undefined,
        defaultPassword: defaultPassword || undefined,
        defaultGeolocation: defaultGeolocation || undefined,
        defaultProxyKind: defaultProxyKind || undefined,
      }),
    onSuccess: (r) => {
      setResult(r);
      void queryClient.invalidateQueries({ queryKey: ["proxies", "provider-accounts"] });
      void queryClient.invalidateQueries({ queryKey: ["proxies", "list"] });
    },
    onError: (err) => toast.error("Upload failed", { description: describeError(err) }),
  });

  function handleClose() {
    setFile(null);
    setDefaultUsername("");
    setDefaultPassword("");
    setDefaultGeolocation("");
    setDefaultProxyKind("");
    setResult(null);
    onClose();
  }

  if (!account) return null;

  return (
    <Dialog open={open} onOpenChange={(o) => !o && handleClose()}>
      <DialogContent>
        <DialogHeader>
          <DialogTitle>Upload proxy list for {account.name}</DialogTitle>
        </DialogHeader>
        <DialogBody className="space-y-4">
          <Field id="upload-file" label="CSV file" required>
            <input
              id="upload-file"
              type="file"
              accept=".csv,text/csv"
              onChange={(e) => setFile(e.target.files?.[0] ?? null)}
            />
          </Field>
          <Field id="upload-default-username" label="Default username" hint="Used for any row that leaves Username blank.">
            <Input
              id="upload-default-username"
              aria-label="Default username"
              value={defaultUsername}
              onChange={(e) => setDefaultUsername(e.target.value)}
            />
          </Field>
          <Field id="upload-default-password" label="Default password">
            <Input
              id="upload-default-password"
              aria-label="Default password"
              type="password"
              value={defaultPassword}
              onChange={(e) => setDefaultPassword(e.target.value)}
            />
          </Field>
          <Field id="upload-default-geolocation" label="Default geolocation">
            <Input
              id="upload-default-geolocation"
              value={defaultGeolocation}
              onChange={(e) => setDefaultGeolocation(e.target.value)}
              placeholder="CL"
            />
          </Field>
          <Field id="upload-default-kind" label="Default proxy kind">
            <Select
              value={defaultProxyKind}
              onChange={(v) => setDefaultProxyKind(v as ProxyKind | "")}
              options={PROXY_KIND_OPTIONS}
              placeholder="— none —"
              className="w-full"
              minWidth="100%"
            />
          </Field>

          {result && (
            <div className="rounded-lg border border-[var(--color-border)] p-3 text-[13px]">
              <p>
                {result.created} created, {result.updated} updated, {result.retired} retired
              </p>
              {result.errors.length > 0 && (
                <ul className="mt-2 space-y-1 text-[var(--color-destructive)]">
                  {result.errors.map((e) => (
                    <li key={e.lineNumber}>
                      line {e.lineNumber}: {e.message}
                    </li>
                  ))}
                </ul>
              )}
            </div>
          )}
        </DialogBody>
        <DialogFooter>
          <Button type="button" variant="outline" onClick={handleClose}>
            Close
          </Button>
          <Button
            type="button"
            onClick={() => mutation.mutate()}
            disabled={!file || mutation.isPending}
            className="min-w-[8.5rem]"
          >
            {mutation.isPending ? (
              <>
                <Loader2 className="size-4 animate-spin" aria-hidden />
                <span>Uploading…</span>
              </>
            ) : (
              "Upload"
            )}
          </Button>
        </DialogFooter>
      </DialogContent>
    </Dialog>
  );
}

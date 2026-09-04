import { useState } from "react";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { toast } from "sonner";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import { Select } from "@/components/ui/select";
import { Dialog, DialogBody, DialogContent, DialogFooter, DialogHeader, DialogTitle } from "@/components/ui/dialog";
import { Field } from "@/components/list";
import { ApiRequestError } from "@/lib/api-client";
import { listTagCategories } from "@/api/tag-categories";
import { assignProxyTag, unassignProxyTag } from "@/api/proxies";

function describeError(err: unknown): string {
  if (err instanceof ApiRequestError) return err.problem?.detail ?? err.problem?.title ?? err.message;
  if (err instanceof Error) return err.message;
  return "Something went wrong.";
}

export function BulkTagDialog({
  open,
  proxyIds,
  onClose,
}: {
  open: boolean;
  proxyIds: string[];
  onClose: () => void;
}) {
  const queryClient = useQueryClient();

  const [addCategory, setAddCategory] = useState("");
  const [addValue, setAddValue] = useState("");
  const [addCustom, setAddCustom] = useState("");
  const [removeCategory, setRemoveCategory] = useState("");
  const [removeValue, setRemoveValue] = useState("");
  const [removeCustom, setRemoveCustom] = useState("");

  const categoriesQuery = useQuery({
    queryKey: ["proxies", "tag-categories"],
    queryFn: () => listTagCategories(),
    enabled: open,
  });
  const categories = categoriesQuery.data ?? [];

  function resolveTagName(category: string, value: string, custom: string): string | null {
    if (custom.trim()) return custom.trim();
    if (category && value) return `${category}:${value}`;
    return null;
  }

  const assignMutation = useMutation({
    mutationFn: (tagName: string) => assignProxyTag(proxyIds, tagName),
    onSuccess: (count) => {
      toast.success(count === 1 ? "Tag added to 1 proxy" : `Tag added to ${count} proxies`);
      void queryClient.invalidateQueries({ queryKey: ["proxies", "list"] });
      setAddCategory("");
      setAddValue("");
      setAddCustom("");
    },
    onError: (err) => toast.error("Add failed", { description: describeError(err) }),
  });

  const unassignMutation = useMutation({
    mutationFn: (tagName: string) => unassignProxyTag(proxyIds, tagName),
    onSuccess: (count) => {
      toast.success(count === 1 ? "Tag removed from 1 proxy" : `Tag removed from ${count} proxies`);
      void queryClient.invalidateQueries({ queryKey: ["proxies", "list"] });
      setRemoveCategory("");
      setRemoveValue("");
      setRemoveCustom("");
    },
    onError: (err) => toast.error("Remove failed", { description: describeError(err) }),
  });

  const addCategoryValues = categories.find((c) => c.name === addCategory)?.values ?? [];
  const removeCategoryValues = categories.find((c) => c.name === removeCategory)?.values ?? [];

  return (
    <Dialog open={open} onOpenChange={(o) => !o && onClose()}>
      <DialogContent>
        <DialogHeader>
          <DialogTitle>Manage tags for {proxyIds.length} selected</DialogTitle>
        </DialogHeader>
        <DialogBody className="space-y-6">
          <div className="space-y-2">
            <div className="text-[11.5px] font-semibold uppercase tracking-wider text-[var(--color-muted-foreground)]">
              Add tag
            </div>
            <Field id="bulk-add-category" label="Category (add)">
              <div data-testid="bulk-add-category-select">
                <Select
                  value={addCategory}
                  onChange={(v) => {
                    setAddCategory(v);
                    setAddValue("");
                  }}
                  options={categories.map((c) => ({ value: c.name, label: c.name }))}
                  placeholder="— choose —"
                  className="w-full"
                  minWidth="100%"
                />
              </div>
            </Field>
            <Field id="bulk-add-value" label="Value (add)">
              <div data-testid="bulk-add-value-select">
                <Select
                  value={addValue}
                  onChange={setAddValue}
                  options={addCategoryValues.map((v) => ({ value: v, label: v }))}
                  placeholder="— choose —"
                  className="w-full"
                  minWidth="100%"
                />
              </div>
            </Field>
            <Field id="bulk-add-custom" label="Or custom tag">
              <Input id="bulk-add-custom" value={addCustom} onChange={(e) => setAddCustom(e.target.value)} />
            </Field>
            <Button
              type="button"
              size="sm"
              disabled={assignMutation.isPending || resolveTagName(addCategory, addValue, addCustom) === null}
              onClick={() => {
                const tagName = resolveTagName(addCategory, addValue, addCustom);
                if (tagName) assignMutation.mutate(tagName);
              }}
            >
              Add to {proxyIds.length} selected
            </Button>
          </div>

          <div className="space-y-2 border-t border-[var(--color-border)] pt-4">
            <div className="text-[11.5px] font-semibold uppercase tracking-wider text-[var(--color-muted-foreground)]">
              Remove tag
            </div>
            <Field id="bulk-remove-category" label="Category (remove)">
              <div data-testid="bulk-remove-category-select">
                <Select
                  value={removeCategory}
                  onChange={(v) => {
                    setRemoveCategory(v);
                    setRemoveValue("");
                  }}
                  options={categories.map((c) => ({ value: c.name, label: c.name }))}
                  placeholder="— choose —"
                  className="w-full"
                  minWidth="100%"
                />
              </div>
            </Field>
            <Field id="bulk-remove-value" label="Value (remove)">
              <div data-testid="bulk-remove-value-select">
                <Select
                  value={removeValue}
                  onChange={setRemoveValue}
                  options={removeCategoryValues.map((v) => ({ value: v, label: v }))}
                  placeholder="— choose —"
                  className="w-full"
                  minWidth="100%"
                />
              </div>
            </Field>
            <Field id="bulk-remove-custom" label="Or custom tag">
              <Input id="bulk-remove-custom" value={removeCustom} onChange={(e) => setRemoveCustom(e.target.value)} />
            </Field>
            <Button
              type="button"
              size="sm"
              variant="outline"
              disabled={unassignMutation.isPending || resolveTagName(removeCategory, removeValue, removeCustom) === null}
              onClick={() => {
                const tagName = resolveTagName(removeCategory, removeValue, removeCustom);
                if (tagName) unassignMutation.mutate(tagName);
              }}
            >
              Remove from {proxyIds.length} selected
            </Button>
          </div>
        </DialogBody>
        <DialogFooter>
          <Button type="button" variant="outline" onClick={onClose}>
            Close
          </Button>
        </DialogFooter>
      </DialogContent>
    </Dialog>
  );
}

import { useEffect, useState } from "react";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { toast } from "sonner";
import { Loader2 } from "lucide-react";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import { Select } from "@/components/ui/select";
import { Dialog, DialogBody, DialogContent, DialogFooter, DialogHeader, DialogTitle } from "@/components/ui/dialog";
import { Field } from "@/components/list";
import { ApiRequestError } from "@/lib/api-client";
import { countryFlag } from "@/lib/country-flag";
import { listTagCategories } from "@/api/tag-categories";
import { setProxyTags, type ProxyDto } from "@/api/proxies";

function describeError(err: unknown): string {
  if (err instanceof ApiRequestError) return err.problem?.detail ?? err.problem?.title ?? err.message;
  if (err instanceof Error) return err.message;
  return "Something went wrong.";
}

export function ProxyTagsDialog({ open, proxy, onClose }: { open: boolean; proxy: ProxyDto | null; onClose: () => void }) {
  const queryClient = useQueryClient();
  const [selectedByCategory, setSelectedByCategory] = useState<Record<string, string>>({});
  const [customTagsInput, setCustomTagsInput] = useState("");

  const categoriesQuery = useQuery({
    queryKey: ["proxies", "tag-categories"],
    queryFn: () => listTagCategories(),
    enabled: open,
  });
  const categories = categoriesQuery.data ?? [];

  // Split the proxy's current tags into "matches a category:value" vs. everything else, so the
  // selects pre-select what's already assigned and the free-text field only shows the rest.
  //
  // Depend on `categoriesQuery.data` (referentially stable once fetched) rather than the
  // `categories` fallback below — `categoriesQuery.data ?? []` produces a new array on every
  // render while the query is still loading, which as a dependency here would re-fire this
  // effect every render and loop forever ("Maximum update depth exceeded").
  useEffect(() => {
    const cats = categoriesQuery.data ?? [];
    if (!proxy || cats.length === 0) {
      setSelectedByCategory({});
      setCustomTagsInput(proxy?.tags.join(", ") ?? "");
      return;
    }
    const matched: Record<string, string> = {};
    const consumed = new Set<string>();
    for (const category of cats) {
      for (const value of category.values) {
        // Catalog casing is preserved for display, but the persisted Tag is always lowercase
        // (composed strings are normalized server-side), so match case-insensitively here.
        const composed = `${category.name}:${value}`.toLowerCase();
        const match = proxy.tags.find((t) => t.toLowerCase() === composed);
        if (match) {
          matched[category.name] = value;
          consumed.add(match);
          break;
        }
      }
    }
    setSelectedByCategory(matched);
    setCustomTagsInput(proxy.tags.filter((t) => !consumed.has(t)).join(", "));
  }, [proxy, categoriesQuery.data]);

  const mutation = useMutation({
    mutationFn: (tagNames: string[]) => setProxyTags(proxy!.id, tagNames),
    onSuccess: () => {
      toast.success("Tags updated");
      void queryClient.invalidateQueries({ queryKey: ["proxies", "list"] });
      onClose();
    },
    onError: (err) => toast.error("Update failed", { description: describeError(err) }),
  });

  if (!proxy) return null;

  function handleSubmit() {
    const composed = categories
      .map((c) => (selectedByCategory[c.name] ? `${c.name}:${selectedByCategory[c.name]}` : null))
      .filter((t): t is string => t !== null);
    const custom = customTagsInput
      .split(",")
      .map((t) => t.trim())
      .filter(Boolean);
    mutation.mutate([...composed, ...custom]);
  }

  return (
    <Dialog open={open} onOpenChange={(o) => !o && onClose()}>
      <DialogContent>
        <DialogHeader>
          <DialogTitle>
            Tags for {proxy.host}:{proxy.port}
          </DialogTitle>
        </DialogHeader>
        <DialogBody className="space-y-4">
          {categories.map((category) => (
            <Field key={category.id} id={`tag-cat-${category.id}`} label={category.name}>
              {/* data-testid gives Playwright an unambiguous handle — the Select component
                  (Radix DropdownMenu-based, not a native <select>) doesn't accept id/aria-label. */}
              <div data-testid={`tag-category-select-${category.name}`}>
                <Select
                  value={selectedByCategory[category.name] ?? ""}
                  onChange={(v) => setSelectedByCategory((prev) => ({ ...prev, [category.name]: v }))}
                  options={category.values.map((v) => ({
                    value: v,
                    label: category.name.toLowerCase() === "country" ? [countryFlag(v), v].filter(Boolean).join(" ") : v,
                  }))}
                  placeholder="— none —"
                  className="w-full"
                  minWidth="100%"
                />
              </div>
            </Field>
          ))}
          <Field id="tag-custom" label="Other tags" hint="Comma-separated — anything not covered by a category above.">
            <Input id="tag-custom" value={customTagsInput} onChange={(e) => setCustomTagsInput(e.target.value)} />
          </Field>
        </DialogBody>
        <DialogFooter>
          <Button type="button" variant="outline" onClick={onClose} disabled={mutation.isPending}>
            Cancel
          </Button>
          <Button type="button" onClick={handleSubmit} disabled={mutation.isPending} className="min-w-[8.5rem]">
            {mutation.isPending ? (
              <>
                <Loader2 className="size-4 animate-spin" aria-hidden />
                <span>Saving…</span>
              </>
            ) : (
              "Save"
            )}
          </Button>
        </DialogFooter>
      </DialogContent>
    </Dialog>
  );
}

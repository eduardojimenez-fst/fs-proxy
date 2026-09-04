import { useState } from "react";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { toast } from "sonner";
import { Plus, Tag as TagIcon, Trash2, X } from "lucide-react";
import { EntityPageHeader, ErrorBand, LoadingRow } from "@/components/list";
import { EmptyState } from "@/components/empty-state";
import { Button } from "@/components/ui/button";
import { Badge } from "@/components/ui/badge";
import { Input } from "@/components/ui/input";
import { ApiRequestError } from "@/lib/api-client";
import { countryFlag } from "@/lib/country-flag";
import { ProxiesPermissions } from "@/lib/permissions";
import { useAuth } from "@/auth/use-auth";
import {
  addTagCategoryValue,
  deleteTagCategory,
  listTagCategories,
  removeTagCategoryValue,
  type TagCategoryDto,
} from "@/api/tag-categories";
import { TagCategoryDialog } from "@/components/proxies/tag-category-dialog";

function describeError(err: unknown): string {
  if (err instanceof ApiRequestError) return err.problem?.detail ?? err.problem?.title ?? err.message;
  if (err instanceof Error) return err.message;
  return "Something went wrong.";
}

export function TagCategoriesPage() {
  const { user } = useAuth();
  const queryClient = useQueryClient();
  const [dialogState, setDialogState] = useState<{ open: boolean; category?: TagCategoryDto }>({ open: false });
  const [newValueInputs, setNewValueInputs] = useState<Record<string, string>>({});

  const canCreate = user?.permissions.includes(ProxiesPermissions.Tags.Create) ?? false;
  const canUpdate = user?.permissions.includes(ProxiesPermissions.Tags.Update) ?? false;
  const canDelete = user?.permissions.includes(ProxiesPermissions.Tags.Delete) ?? false;

  const categoriesQuery = useQuery({
    queryKey: ["proxies", "tag-categories"],
    queryFn: () => listTagCategories(),
  });

  const invalidate = () => queryClient.invalidateQueries({ queryKey: ["proxies", "tag-categories"] });

  const deleteCategoryMutation = useMutation({
    mutationFn: (id: string) => deleteTagCategory(id),
    onSuccess: () => {
      toast.success("Category deleted");
      invalidate();
    },
    onError: (err) => toast.error("Delete failed", { description: describeError(err) }),
  });

  const addValueMutation = useMutation({
    mutationFn: (input: { categoryId: string; value: string }) => addTagCategoryValue(input.categoryId, input.value),
    onSuccess: (_data, input) => {
      setNewValueInputs((prev) => ({ ...prev, [input.categoryId]: "" }));
      invalidate();
    },
    onError: (err) => toast.error("Add value failed", { description: describeError(err) }),
  });

  const removeValueMutation = useMutation({
    mutationFn: (input: { categoryId: string; value: string }) => removeTagCategoryValue(input.categoryId, input.value),
    onSuccess: () => invalidate(),
    onError: (err) => toast.error("Remove value failed", { description: describeError(err) }),
  });

  const categories = categoriesQuery.data ?? [];

  return (
    <div className="space-y-8">
      <EntityPageHeader
        icon={TagIcon}
        title="Tag Categories"
        total={categories.length}
        unit="category"
        description="Predefined dimensions (e.g. pais, funcionalidad) and their values, used to speed up tagging proxies."
      >
        {canCreate && (
          <Button onClick={() => setDialogState({ open: true })}>
            <Plus className="mr-1 h-4 w-4" /> New category
          </Button>
        )}
      </EntityPageHeader>

      {categoriesQuery.isError && <ErrorBand message={describeError(categoriesQuery.error)} />}
      {categoriesQuery.isLoading && <LoadingRow label="Loading tag categories" />}

      {!categoriesQuery.isLoading && !categoriesQuery.isError && categories.length === 0 && (
        <EmptyState
          icon={TagIcon}
          kicker="// no categories"
          title="No tag categories yet."
          description='Create one (e.g. "pais") and add values to speed up tagging proxies from a select instead of typing.'
          action={
            canCreate ? (
              <Button onClick={() => setDialogState({ open: true })}>
                <Plus className="mr-1 h-4 w-4" /> New category
              </Button>
            ) : undefined
          }
        />
      )}

      {categories.length > 0 && (
        <ol className="space-y-4">
          {categories.map((category) => (
            <li key={category.id} className="rounded-xl border border-[var(--color-border)] p-4">
              <div className="flex items-center justify-between gap-3">
                <div className="font-mono text-[13px] font-medium">{category.name}</div>
                <div className="flex gap-2">
                  {canUpdate && (
                    <Button variant="ghost" size="sm" onClick={() => setDialogState({ open: true, category })}>
                      Rename
                    </Button>
                  )}
                  {canDelete && (
                    <Button
                      variant="ghost"
                      size="sm"
                      onClick={() => {
                        if (window.confirm(`Delete category "${category.name}"? Already-assigned tags are unaffected.`)) {
                          deleteCategoryMutation.mutate(category.id);
                        }
                      }}
                      className="text-[var(--color-destructive)] hover:bg-[oklch(from_var(--color-destructive)_l_c_h_/_0.08)]"
                    >
                      <Trash2 className="h-3.5 w-3.5" />
                    </Button>
                  )}
                </div>
              </div>

              <div className="mt-3 flex flex-wrap items-center gap-2">
                {category.values.map((value) => (
                  <Badge key={value} variant="muted" className="gap-1 font-mono">
                    {category.name.toLowerCase() === "country" ? `${countryFlag(value)} ${value}` : value}
                    {canUpdate && (
                      <button
                        type="button"
                        aria-label={`Remove value ${value}`}
                        onClick={() => removeValueMutation.mutate({ categoryId: category.id, value })}
                      >
                        <X className="h-3 w-3" />
                      </button>
                    )}
                  </Badge>
                ))}
                {canUpdate && (
                  <form
                    className="flex items-center gap-1"
                    onSubmit={(e) => {
                      e.preventDefault();
                      const value = (newValueInputs[category.id] ?? "").trim();
                      if (value) addValueMutation.mutate({ categoryId: category.id, value });
                    }}
                  >
                    <Input
                      aria-label={`New value for ${category.name}`}
                      placeholder="cl"
                      value={newValueInputs[category.id] ?? ""}
                      onChange={(e) => setNewValueInputs((prev) => ({ ...prev, [category.id]: e.target.value }))}
                      className="h-7 w-24 text-[12px]"
                    />
                    <Button type="submit" size="sm" variant="outline">
                      <Plus className="h-3 w-3" />
                    </Button>
                  </form>
                )}
              </div>
            </li>
          ))}
        </ol>
      )}

      <TagCategoryDialog
        open={dialogState.open}
        category={dialogState.category}
        onClose={() => setDialogState({ open: false })}
      />
    </div>
  );
}

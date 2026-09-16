import { batch, createEffect, createMemo, createSignal, onCleanup } from "solid-js";
import type { ThemeSlot } from "../bridge";
import { beginThemePreview, currentThemeId } from "./controller";
import {
  type ExtensionChoice,
  installTheme,
  type SearchResults,
  selectTheme,
  setThemePickerOpen,
  type ThemeChoice,
  type ThemePreview,
  type ThemeSearchOrder,
  themeRequest,
} from "./picker-state";

export function createThemePicker() {
  const savedId = currentThemeId();
  const preview = beginThemePreview();
  const lifetime = new AbortController();
  const [catalog, setCatalog] = createSignal<ThemeChoice[]>([]);
  const [query, setQuery] = createSignal("");
  const [sortBy, setSortBy] = createSignal<ThemeSearchOrder>("downloadCount");
  const [registry, setRegistry] = createSignal(false);
  const [extensions, setExtensions] = createSignal<ExtensionChoice[]>([]);
  const [variants, setVariants] = createSignal<ThemePreview[] | null>(null);
  const [total, setTotal] = createSignal(0);
  const [selected, setSelected] = createSignal(0);
  const [error, setError] = createSignal("");
  const [searchLoading, setSearchLoading] = createSignal(false);
  const [packageLoading, setPackageLoading] = createSignal(false);
  const loading = () => searchLoading() || packageLoading();
  const [saving, setSaving] = createSignal(false);
  let previewGeneration = 0;
  let searchGeneration = 0;
  let registryQuery = "";
  let searchAbort = new AbortController();
  const choices = createMemo(() =>
    (variants()?.map((v) => v.choice) ?? catalog()).filter((t) =>
      `${t.label} ${t.namespace ?? ""}`.toLowerCase().includes(query().toLowerCase()),
    ),
  );
  const isSearch = () => registry() && variants() === null;
  const count = () => (isSearch() ? extensions().length : choices().length);
  const fail = (e: unknown) => {
    if (!lifetime.signal.aborted) setError(String(e));
  };
  const close = () => {
    if (!saving()) setThemePickerOpen(false);
  };

  onCleanup(() => {
    lifetime.abort();
    searchAbort.abort();
    previewGeneration++;
    preview.dispose();
  });
  void themeRequest<ThemeChoice[]>("list", {}, lifetime.signal).then(setCatalog).catch(fail);

  async function highlight(index: number) {
    setSelected(index);
    const generation = ++previewGeneration;
    if (isSearch()) {
      setPackageLoading(false);
      return;
    }
    const choice = choices()[index];
    if (choice === undefined) return;
    setError("");
    try {
      const slot =
        variants()?.find((v) => v.choice.id === choice.id)?.slot ??
        (await themeRequest<ThemeSlot>("preview", { id: choice.id }, lifetime.signal));
      if (generation === previewGeneration && !lifetime.signal.aborted) preview.show(slot);
    } catch (e) {
      if (generation === previewGeneration) fail(e);
    }
  }

  async function search(offset: number, generation: number) {
    setSearchLoading(true);
    setError("");
    try {
      const result = await themeRequest<SearchResults>(
        "search",
        { query: query(), offset, sortBy: sortBy() },
        searchAbort.signal,
      );
      if (generation !== searchGeneration || lifetime.signal.aborted) return;
      setExtensions(offset === 0 ? result.extensions : [...extensions(), ...result.extensions]);
      setTotal(result.totalSize);
    } catch (e) {
      if (generation === searchGeneration) fail(e);
    } finally {
      if (generation === searchGeneration) setSearchLoading(false);
    }
  }

  createEffect(() => {
    const remote = isSearch();
    query();
    sortBy();
    choices();
    const initial =
      !registry() && query() === ""
        ? Math.max(
            0,
            choices().findIndex((choice) => choice.id === savedId),
          )
        : 0;
    setSelected(initial);
    previewGeneration++;
    searchAbort.abort();
    searchAbort = new AbortController();
    const generation = ++searchGeneration;
    setSearchLoading(false);
    setPackageLoading(false);
    if (remote) {
      setExtensions([]);
      setTotal(0);
      const timer = setTimeout(() => void search(0, generation), 250);
      onCleanup(() => clearTimeout(timer));
    } else {
      void highlight(initial);
    }
  });

  async function accept() {
    if (loading() || saving()) return;
    setError("");
    if (isSearch()) {
      const extension = extensions()[selected()];
      if (extension === undefined) return;
      registryQuery = query();
      setPackageLoading(true);
      const generation = ++previewGeneration;
      try {
        const result = await themeRequest<ThemePreview[]>(
          "previewExtension",
          extension,
          lifetime.signal,
        );
        if (generation === previewGeneration && !lifetime.signal.aborted) {
          batch(() => {
            setQuery("");
            setVariants(result);
          });
        }
      } catch (e) {
        if (generation === previewGeneration) fail(e);
      } finally {
        if (generation === previewGeneration && !lifetime.signal.aborted) setPackageLoading(false);
      }
      return;
    }
    const choice = choices()[selected()];
    if (choice === undefined) return;
    setSaving(true);
    try {
      if (variants() !== null) await installTheme(choice);
      await selectTheme(choice.id, lifetime.signal);
      setThemePickerOpen(false);
    } catch (e) {
      fail(e);
    } finally {
      setSaving(false);
    }
  }

  function switchSource(remote: boolean) {
    if (saving()) return;
    setVariants(null);
    setRegistry(remote);
    setQuery("");
    setError("");
  }

  return {
    savedId,
    query,
    setQuery,
    sortBy,
    setSortBy,
    registry,
    extensions,
    variants,
    backToResults: () =>
      batch(() => {
        setQuery(registryQuery);
        setVariants(null);
      }),
    total,
    selected,
    error,
    loading,
    saving,
    choices,
    isSearch,
    count,
    close,
    highlight,
    accept,
    switchSource,
    loadMore: () => search(extensions().length, searchGeneration),
  };
}

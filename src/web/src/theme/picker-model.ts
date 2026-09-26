import { createEffect, createMemo, createSignal, on, onCleanup } from "solid-js";
import type { ThemeSlot } from "../bridge";
import { beginThemePreview, currentThemeId, currentThemeType } from "./controller";
import {
  type ExtensionChoice,
  installTheme,
  type SearchResults,
  selectTheme,
  setThemePickerOpen,
  type ThemeChoice,
  type ThemePreview,
  type ThemeSearchOrder,
  themePickerStartsInRegistry,
  themeRequest,
} from "./picker-state";

export type ThemeFilter = "light" | "dark" | "all";

export function createThemePicker() {
  const savedId = currentThemeId();
  const [mode, setMode] = createSignal<ThemeFilter>(currentThemeType());
  const preview = beginThemePreview();
  const lifetime = new AbortController();
  const [catalog, setCatalog] = createSignal<ThemeChoice[]>([]);
  const [query, setQuery] = createSignal("");
  const [sortBy, setSortBy] = createSignal<ThemeSearchOrder>("downloadCount");
  const [registry, setRegistry] = createSignal(themePickerStartsInRegistry());
  const [extensions, setExtensions] = createSignal<ExtensionChoice[]>([]);
  const [total, setTotal] = createSignal(0);
  const [selected, setSelected] = createSignal(0);
  const [error, setError] = createSignal("");
  const [searchLoading, setSearchLoading] = createSignal(false);
  const [saving, setSaving] = createSignal(false);
  const [activeExtension, setActiveExtension] = createSignal<ExtensionChoice | null>(null);
  const [variants, setVariants] = createSignal<ThemePreview[]>([]);
  const [variantQuery, setVariantQuery] = createSignal("");
  const [variantSelected, setVariantSelected] = createSignal(0);
  const [packageLoading, setPackageLoading] = createSignal(false);
  const [packageError, setPackageError] = createSignal("");
  let previewGeneration = 0;
  let searchAbort = new AbortController();
  let packageAbort = new AbortController();
  const choices = createMemo(() =>
    catalog().filter(
      (t) =>
        (mode() === "all" || t.type === mode()) &&
        `${t.label} ${t.namespace ?? ""}`.toLowerCase().includes(query().toLowerCase()),
    ),
  );
  const variantChoices = createMemo(() =>
    variants().filter(
      (v) =>
        (mode() === "all" || v.choice.type === mode()) &&
        v.choice.label.toLowerCase().includes(variantQuery().toLowerCase()),
    ),
  );
  const count = () => (registry() ? extensions().length : choices().length);
  const fail = (e: unknown) => {
    if (!lifetime.signal.aborted) setError(String(e));
  };
  const close = () => {
    if (!saving()) setThemePickerOpen(false);
  };

  function closeVariants() {
    packageAbort.abort();
    setActiveExtension(null);
    setVariants([]);
    setVariantQuery("");
    setPackageLoading(false);
    setPackageError("");
    previewGeneration++;
    preview.clear();
  }

  onCleanup(() => {
    lifetime.abort();
    searchAbort.abort();
    packageAbort.abort();
    previewGeneration++;
    preview.dispose();
  });
  void themeRequest<ThemeChoice[]>("list", {}, lifetime.signal).then(setCatalog).catch(fail);

  async function highlight(index: number) {
    if (saving()) return;
    setSelected(index);
    if (registry()) return;
    const generation = ++previewGeneration;
    const choice = choices()[index];
    if (choice === undefined) return;
    setError("");
    try {
      const slot = await themeRequest<ThemeSlot>("preview", { id: choice.id }, lifetime.signal);
      if (generation === previewGeneration && !lifetime.signal.aborted) preview.show(slot);
    } catch (e) {
      if (generation === previewGeneration) fail(e);
    }
  }

  function highlightVariant(index: number) {
    if (saving()) return;
    setVariantSelected(index);
    const variant = variantChoices()[index];
    if (variant !== undefined) preview.show(variant.slot);
  }

  async function search(offset: number, signal: AbortSignal) {
    setSearchLoading(true);
    setError("");
    try {
      const result = await themeRequest<SearchResults>(
        "search",
        { query: query(), offset, sortBy: sortBy() },
        signal,
      );
      if (signal.aborted || lifetime.signal.aborted) return;
      setExtensions(offset === 0 ? result.extensions : [...extensions(), ...result.extensions]);
      setTotal(result.totalSize);
    } catch (e) {
      if (!signal.aborted) fail(e);
    } finally {
      if (!signal.aborted) setSearchLoading(false);
    }
  }

  createEffect(
    on([registry, query, sortBy], ([remote]) => {
      closeVariants();
      searchAbort.abort();
      searchAbort = new AbortController();
      setSearchLoading(false);
      setSelected(0);
      setError("");
      if (!remote) return;
      setExtensions([]);
      setTotal(0);
      const signal = searchAbort.signal;
      const timer = setTimeout(() => void search(0, signal), 250);
      onCleanup(() => clearTimeout(timer));
    }),
  );

  createEffect(
    on([registry, choices], ([remote, items]) => {
      if (remote) return;
      const initial =
        query() === ""
          ? Math.max(
              0,
              items.findIndex((c) => c.id === savedId),
            )
          : 0;
      void highlight(initial);
    }),
  );
  createEffect(on(variantChoices, () => highlightVariant(0)));

  async function openExtension(index: number) {
    if (saving()) return false;
    const extension = extensions()[index];
    if (extension === undefined) return false;
    closeVariants();
    setSelected(index);
    setActiveExtension(extension);
    setPackageLoading(true);
    packageAbort = new AbortController();
    const signal = packageAbort.signal;
    try {
      const result = await themeRequest<ThemePreview[]>("previewExtension", extension, signal);
      if (!signal.aborted && !lifetime.signal.aborted) {
        setVariants(result);
        return true;
      }
    } catch (e) {
      if (!signal.aborted && !lifetime.signal.aborted) setPackageError(String(e));
    } finally {
      if (!signal.aborted) setPackageLoading(false);
    }
    return false;
  }

  async function apply(choice: ThemeChoice, install: boolean) {
    if (saving()) return;
    setSaving(true);
    setError("");
    try {
      if (install) await installTheme(choice);
      await selectTheme(choice.id, lifetime.signal);
      setThemePickerOpen(false);
    } catch (e) {
      fail(e);
    } finally {
      setSaving(false);
    }
  }

  async function accept() {
    const choice = choices()[selected()];
    if (choice !== undefined) await apply(choice, false);
  }
  async function acceptVariant() {
    const variant = variantChoices()[variantSelected()];
    if (variant !== undefined) await apply(variant.choice, true);
  }

  function switchSource(remote: boolean) {
    if (saving()) return;
    setRegistry(remote);
    setQuery("");
  }

  return {
    savedId,
    mode,
    setMode,
    query,
    setQuery,
    sortBy,
    setSortBy,
    registry,
    extensions,
    total,
    selected,
    error,
    searchLoading,
    saving,
    choices,
    count,
    close,
    highlight,
    accept,
    switchSource,
    activeExtension,
    variants,
    variantQuery,
    setVariantQuery,
    variantSelected,
    variantChoices,
    packageLoading,
    packageError,
    highlightVariant,
    acceptVariant,
    openExtension,
    closeVariants,
    loadMore: () => search(extensions().length, searchAbort.signal),
  };
}

export type ThemePickerModel = ReturnType<typeof createThemePicker>;

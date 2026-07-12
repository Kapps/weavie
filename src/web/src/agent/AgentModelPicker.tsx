import { createEffect, createMemo, createSignal, For, type JSX, onCleanup, Show } from "solid-js";
import type { AgentModelChoice } from "../bridge";
import {
  agentControlState,
  closeControlPicker,
  MODEL_AXIS,
  openControlAxis,
  selectModel,
  selectModelEffort,
  toggleModelFast,
} from "./agent-controls-store";

// The merged model → effort / Fast picker: a cascading flyout with models on the left and the focused model's
// efforts + Fast toggle on the right. Opens when the composer's reserved MODEL_AXIS picker is active, mirroring the
// overlay pattern of AgentControlPicker (it owns Enter/Escape while open via the agentControlPickerOpen context).
type SubEntry =
  | { kind: "effort"; id: string; label: string; checked: boolean }
  | { kind: "fast"; on: boolean };

export function AgentModelPicker(props: { backendId: string; slot: string | null }): JSX.Element {
  const models = createMemo<AgentModelChoice[]>(() =>
    openControlAxis() === MODEL_AXIS && props.slot !== null
      ? agentControlState(props.slot).modelControl.models
      : [],
  );
  const isOpen = (): boolean => models().length > 0;

  const [focus, setFocus] = createSignal(0);
  const [pane, setPane] = createSignal<"models" | "sub">("models");
  const [sub, setSub] = createSignal(0);

  const focusedModel = (): AgentModelChoice | undefined => models()[focus()];
  const subEntries = createMemo<SubEntry[]>(() => {
    const model = focusedModel();
    if (model === undefined) {
      return [];
    }
    const efforts: SubEntry[] = model.efforts.map((effort) => ({
      kind: "effort",
      id: effort.id,
      label: effort.label,
      checked: effort.id === model.effort,
    }));
    return model.fastTier === "" ? efforts : [...efforts, { kind: "fast", on: model.fastOn }];
  });

  // Seed focus/pane only on the closed→open transition: a host re-push (e.g. the re-push a Fast toggle triggers)
  // rebuilds models() with a fresh reference, which would otherwise re-run this and snap navigation back mid-use.
  let wasOpen = false;
  createEffect(() => {
    const open = isOpen();
    if (open && !wasOpen) {
      const index = models().findIndex((model) => model.current);
      setFocus(index >= 0 ? index : 0);
      setPane("models");
    }
    wasOpen = open;
  });

  const chooseModel = (model: AgentModelChoice): void => {
    if (props.slot !== null) {
      selectModel(props.backendId, props.slot, model);
    }
    closeControlPicker();
  };

  const chooseEffort = (model: AgentModelChoice, effortId: string): void => {
    if (props.slot !== null) {
      selectModelEffort(props.backendId, props.slot, model, effortId);
    }
    closeControlPicker();
  };

  // Fast keeps the menu open so its flipped state is visible; the host re-pushes control state, refreshing it live.
  const toggleFast = (model: AgentModelChoice): void => {
    if (props.slot !== null) {
      toggleModelFast(props.backendId, props.slot, model);
    }
  };

  const enterSub = (): void => {
    if (subEntries().length === 0) {
      return;
    }
    const current = subEntries().findIndex((entry) => entry.kind === "effort" && entry.checked);
    setSub(current >= 0 ? current : 0);
    setPane("sub");
  };

  const activateSub = (): void => {
    const model = focusedModel();
    const entry = subEntries()[sub()];
    if (model === undefined || entry === undefined) {
      return;
    }
    if (entry.kind === "effort") {
      chooseEffort(model, entry.id);
    } else {
      toggleFast(model);
    }
  };

  const onKeyDown = (event: KeyboardEvent): void => {
    if (!isOpen()) {
      return;
    }
    const stop = (): void => {
      event.preventDefault();
      event.stopPropagation();
    };
    if (event.key === "Escape") {
      stop();
      closeControlPicker();
    } else if (pane() === "models") {
      if (event.key === "ArrowDown") {
        stop();
        setFocus((index) => (index + 1) % models().length);
      } else if (event.key === "ArrowUp") {
        stop();
        setFocus((index) => (index <= 0 ? models().length - 1 : index - 1));
      } else if (event.key === "ArrowRight") {
        stop();
        enterSub();
      } else if (event.key === "Enter" || event.key === "Tab") {
        stop();
        const model = focusedModel();
        if (model !== undefined) {
          chooseModel(model);
        }
      }
    } else if (event.key === "ArrowDown") {
      stop();
      setSub((index) => (index + 1) % subEntries().length);
    } else if (event.key === "ArrowUp") {
      stop();
      setSub((index) => (index <= 0 ? subEntries().length - 1 : index - 1));
    } else if (event.key === "ArrowLeft") {
      stop();
      setPane("models");
    } else if (event.key === "Enter" || event.key === "Tab") {
      stop();
      activateSub();
    }
  };

  // Only listen while open, in capture phase so a pick beats the composer's own history/keydown handling.
  createEffect(() => {
    if (!isOpen()) {
      return;
    }
    window.addEventListener("keydown", onKeyDown, { capture: true });
    onCleanup(() => window.removeEventListener("keydown", onKeyDown, { capture: true }));
  });

  return (
    <Show when={isOpen()}>
      <div class="agent-model-picker" role="group" aria-label="Model, effort, and Fast Mode">
        <div class="agent-model-picker-models" role="listbox" aria-label="Model">
          <For each={models()}>
            {(model, index) => (
              <div
                class="agent-model-row"
                role="option"
                tabindex={-1}
                aria-selected={model.current}
                classList={{ active: index() === focus(), current: model.current }}
                onMouseEnter={() => {
                  setFocus(index());
                  setPane("models");
                }}
                onPointerDown={(event) => {
                  event.preventDefault();
                  chooseModel(model);
                }}
              >
                <span class="agent-model-name">{model.label}</span>
                <Show when={model.fastOn}>
                  <span class="agent-model-fast" title="Fast Mode on">
                    ⚡
                  </span>
                </Show>
                <span class="agent-model-caret" aria-hidden="true">
                  ▸
                </span>
              </div>
            )}
          </For>
        </div>
        <div class="agent-model-picker-sub" role="listbox" aria-label="Effort and Fast Mode">
          <For each={subEntries()}>
            {(entry, index) =>
              entry.kind === "effort" ? (
                <div
                  class="agent-model-sub-item"
                  role="option"
                  tabindex={-1}
                  aria-selected={entry.checked}
                  classList={{ active: pane() === "sub" && index() === sub() }}
                  onMouseEnter={() => {
                    setPane("sub");
                    setSub(index());
                  }}
                  onPointerDown={(event) => {
                    event.preventDefault();
                    const model = focusedModel();
                    if (model !== undefined) {
                      chooseEffort(model, entry.id);
                    }
                  }}
                >
                  <span class="agent-model-check">{entry.checked ? "✓" : ""}</span>
                  <span>{entry.label}</span>
                </div>
              ) : (
                <div
                  class="agent-model-sub-item agent-model-fast-item"
                  role="option"
                  tabindex={-1}
                  aria-selected={entry.on}
                  classList={{ active: pane() === "sub" && index() === sub(), on: entry.on }}
                  onMouseEnter={() => {
                    setPane("sub");
                    setSub(index());
                  }}
                  onPointerDown={(event) => {
                    event.preventDefault();
                    const model = focusedModel();
                    if (model !== undefined) {
                      toggleFast(model);
                    }
                  }}
                >
                  <span class="agent-model-check">⚡</span>
                  <span>Fast</span>
                </div>
              )
            }
          </For>
        </div>
      </div>
    </Show>
  );
}

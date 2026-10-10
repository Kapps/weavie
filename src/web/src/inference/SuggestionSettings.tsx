import { AlertTriangle, ChevronDown, RotateCw } from "lucide-solid";
import {
  Switch as Branch,
  createSignal,
  createUniqueId,
  For,
  type JSX,
  Match,
  Show,
} from "solid-js";
import { Portal } from "solid-js/web";
import type { AgentControlAxis, InferenceControlsState } from "../bridge";
import { ControlMenu } from "../chrome/ControlMenu";
import { writeSetting } from "../host-settings";
import { refreshInferenceControls } from "./inference-controls";
import "./suggestions.css";

/** Routes a settings action's failure to the surface's error line. */
export type Attempt = (action: () => Promise<void>) => void;

// The pickers an agent can lack; the agent picker itself is always first.
const PROFILE = [
  { key: "inference.model", caption: "Model" },
  { key: "inference.effort", caption: "Effort" },
  { key: "inference.fastMode", caption: "Fast" },
];

/** Whether suggestions run, and what they run on: the body of setup's suggestions step and the settings dialog. */
export function SuggestionSettings(props: {
  state: InferenceControlsState;
  attempt: Attempt;
}): JSX.Element {
  const off = () => !props.state.enabled;
  const toggle = (key: string, input: HTMLInputElement, host: () => boolean) =>
    props.attempt(async () => {
      try {
        await writeSetting(key, input.checked);
      } catch (error) {
        // The host refused (e.g. an env override), so no push will sync the switch back to the host's value.
        input.checked = host();
        throw error;
      }
    });
  return (
    <div class="suggestion-settings">
      <SettingRow
        title="Allow suggestions"
        detail="Your agent helps with small things, like naming a branch. Never shown in your chat."
        disabled={false}
        control={(id) => (
          <input
            id={id}
            type="checkbox"
            role="switch"
            class="sg-switch"
            aria-checked={props.state.enabled}
            checked={props.state.enabled}
            onChange={(event) =>
              toggle("inference.enabled", event.currentTarget, () => props.state.enabled)
            }
          />
        )}
      />
      <SettingRow
        title="Suggest automatically"
        detail="Suggest without being asked. Uses a little of your agent usage now and then."
        disabled={off()}
        control={(id) => (
          <input
            id={id}
            type="checkbox"
            role="switch"
            class="sg-switch"
            aria-checked={props.state.automatic}
            checked={props.state.automatic}
            disabled={off()}
            onChange={(event) =>
              toggle("inference.allowAutomatic", event.currentTarget, () => props.state.automatic)
            }
          />
        )}
      />
      <ProfileBar state={props.state} disabled={off()} attempt={props.attempt} />
    </div>
  );
}

// One labelled setting; the row's title and detail name its control.
function SettingRow(props: {
  title: string;
  detail: string;
  disabled: boolean;
  control: (id: string) => JSX.Element;
}): JSX.Element {
  const id = createUniqueId();
  return (
    <div class="sg-row" classList={{ "sg-disabled": props.disabled }}>
      <label class="sg-text" for={id}>
        <strong>{props.title}</strong>
        <small>{props.detail}</small>
      </label>
      {props.control(id)}
    </div>
  );
}

// The agent, model, effort, and Fast Mode suggestions run on, styled after the agent status line's pickers.
function ProfileBar(props: {
  state: InferenceControlsState;
  disabled: boolean;
  attempt: Attempt;
}): JSX.Element {
  const [open, setOpen] = createSignal<{ key: string; anchor: DOMRect } | null>(null);
  const axis = (key: string) => props.state.axes.find((candidate) => candidate.id === key);
  const probing = () => props.state.status === "probing";
  // Until the agent first answers, its pickers hold their place as placeholders instead of popping in.
  const slots = () =>
    PROFILE.filter(
      (slot) => axis(slot.key) !== undefined || (probing() && slot.key !== "inference.fastMode"),
    );
  const toggle = (key: string, anchor: DOMRect) =>
    setOpen((current) => (current?.key === key ? null : { key, anchor }));
  const pick = (key: string, value: string) => {
    setOpen(null);
    props.attempt(() => writeSetting(key, value));
  };
  return (
    <section class="sg-profile" classList={{ "sg-disabled": props.disabled }}>
      <div class="sg-profile-head">
        <strong>Runs on</strong>
        <small>Separate from your chat, so it never changes the model you chat with.</small>
      </div>
      <fieldset class="sg-bar" aria-label="Suggestion agent and model">
        <Segment
          caption="Agent"
          axis={axis("inference.defaultProvider")}
          busy={false}
          disabled={props.disabled}
          open={open()?.key === "inference.defaultProvider"}
          onToggle={(anchor) => toggle("inference.defaultProvider", anchor)}
        />
        <For each={slots()}>
          {(slot) => (
            <Segment
              caption={slot.caption}
              axis={axis(slot.key)}
              busy={probing()}
              disabled={props.disabled}
              open={open()?.key === slot.key}
              onToggle={(anchor) => toggle(slot.key, anchor)}
            />
          )}
        </For>
      </fieldset>
      <Branch>
        <Match when={props.state.status === "failed" && props.state.error}>
          {(error) => (
            <p class="sg-status sg-bad" role="alert">
              <AlertTriangle size="1em" aria-hidden="true" />
              <span>Couldn't get the options: {error()}</span>
              <button
                type="button"
                class="sg-retry"
                onClick={() => props.attempt(refreshInferenceControls)}
              >
                <RotateCw size="1em" aria-hidden="true" />
                Try again
              </button>
            </p>
          )}
        </Match>
        <Match when={props.state.warning}>
          {(warning) => (
            <p class="sg-status sg-warn" role="status">
              <AlertTriangle size="1em" aria-hidden="true" />
              <span>{warning()} Suggestions will fail until you pick another.</span>
            </p>
          )}
        </Match>
      </Branch>
      <Show when={open()} keyed>
        {(menu) => (
          <Show when={axis(menu.key)}>
            {(current) => (
              <Portal>
                <ControlMenu
                  axis={current()}
                  class="sg-menu"
                  style={placement(menu.anchor)}
                  inside=".sg-menu, .sg-segment"
                  onPick={(value) => pick(menu.key, value)}
                  onClose={() => setOpen(null)}
                />
              </Portal>
            )}
          </Show>
        )}
      </Show>
    </section>
  );
}

function Segment(props: {
  caption: string;
  axis: AgentControlAxis | undefined;
  busy: boolean;
  disabled: boolean;
  open: boolean;
  onToggle: (anchor: DOMRect) => void;
}): JSX.Element {
  const isDefault = () => props.axis?.value === "" || props.axis?.value === "inherit";
  return (
    <button
      type="button"
      class="sg-segment"
      classList={{ "sg-busy": props.busy, "sg-placeholder": props.axis === undefined }}
      aria-haspopup="listbox"
      aria-expanded={props.open}
      aria-busy={props.busy || props.axis === undefined}
      disabled={props.disabled || props.axis === undefined}
      title={
        props.axis === undefined
          ? `${props.caption}: asking the agent…`
          : `${props.caption}: ${props.axis.valueLabel}${isDefault() ? " (default)" : ""}`
      }
      onClick={(event) => props.onToggle(event.currentTarget.getBoundingClientRect())}
      onKeyDown={(event) => {
        if (event.key === "ArrowDown" && !props.open) {
          event.preventDefault();
          props.onToggle(event.currentTarget.getBoundingClientRect());
        }
      }}
    >
      <span class="sg-caption">{props.caption}</span>
      <span class="sg-value">
        <span class="sg-value-text">{props.axis?.valueLabel ?? " "}</span>
        <ChevronDown size="0.95em" aria-hidden="true" />
      </span>
    </button>
  );
}

// Below the segment, or above it when the viewport has no room underneath.
function placement(anchor: DOMRect): JSX.CSSProperties {
  const below = window.innerHeight - anchor.bottom;
  const above = below < 180 && anchor.top > below;
  const room = (above ? anchor.top : below) - 12;
  return {
    position: "fixed",
    left: `${anchor.left}px`,
    "min-width": `${Math.max(anchor.width, 200)}px`,
    "max-height": `${Math.min(room, window.innerHeight * 0.45)}px`,
    ...(above
      ? { bottom: `${window.innerHeight - anchor.top + 4}px` }
      : { top: `${anchor.bottom + 4}px` }),
  };
}

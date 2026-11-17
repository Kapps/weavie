import { Copy, Minus, Square, X } from "lucide-solid";
import { For, type JSX, Match, Switch } from "solid-js";
import { windowControlLabel } from "./window-control-label";
import { localWindowFeature } from "./window-state";

// One end's page-drawn window buttons, in the order the host's chrome lists them.
export function WindowControls(props: {
  actions: WindowControlAction[];
  maximized: boolean;
}): JSX.Element {
  return (
    <For each={props.actions}>
      {(action) => (
        <button
          type="button"
          class="tb-ctl"
          classList={{ "tb-close": action === "close" }}
          aria-label={windowControlLabel(action, props.maximized)}
          title={windowControlLabel(action, props.maximized)}
          onClick={() => localWindowFeature()?.publish("control", { action })}
        >
          <Switch>
            <Match when={action === "minimize"}>
              <Minus />
            </Match>
            <Match when={action === "maximize-toggle" && props.maximized}>
              <Copy />
            </Match>
            <Match when={action === "maximize-toggle"}>
              <Square />
            </Match>
            <Match when={action === "close"}>
              <X />
            </Match>
          </Switch>
        </button>
      )}
    </For>
  );
}

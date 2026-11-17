import { Copy, Minus, Square, X } from "lucide-solid";
import { For, type JSX, Match, Switch } from "solid-js";
import { publishWindowControl } from "./caption-press";

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
          aria-label={label(action, props.maximized)}
          onClick={() => publishWindowControl(action)}
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

function label(action: WindowControlAction, maximized: boolean): string {
  if (action === "minimize") return "Minimize";
  if (action === "close") return "Close";
  return maximized ? "Restore" : "Maximize";
}

import { ChevronRight } from "lucide-solid";
import { For, type JSX, Show, createSignal, onCleanup } from "solid-js";

export type MenuAction = "open-folder" | "open-recent" | "close-window" | "exit";

function leaf(path: string): string {
  const parts = path.split(/[\\/]/).filter((p) => p.length > 0);
  return parts.length > 0 ? (parts[parts.length - 1] as string) : path;
}

// The title bar's web-rendered menu bar (File + View): File items post `menu-action` to the host, View
// toggles the file browser. One menu open at a time; hovering another label while open switches to it.
export function Menu(props: {
  recents: string[];
  onMenuAction: (action: MenuAction, path?: string) => void;
  onToggleFiles: () => void;
}): JSX.Element {
  const [openMenu, setOpenMenu] = createSignal<"file" | "view" | null>(null);

  const close = (): void => {
    setOpenMenu(null);
  };
  const toggle = (menu: "file" | "view"): void => {
    setOpenMenu((m) => (m === menu ? null : menu));
  };
  const hover = (menu: "file" | "view"): void => {
    if (openMenu() !== null) {
      setOpenMenu(menu);
    }
  };

  // Dismiss on any outside pointer-down or Escape while a menu is open.
  const onPointerDown = (e: PointerEvent): void => {
    if (!(e.target as HTMLElement).closest(".tb-menu")) {
      close();
    }
  };
  const onKeyDown = (e: KeyboardEvent): void => {
    if (e.key === "Escape") {
      close();
    }
  };
  window.addEventListener("pointerdown", onPointerDown);
  window.addEventListener("keydown", onKeyDown);
  onCleanup(() => {
    window.removeEventListener("pointerdown", onPointerDown);
    window.removeEventListener("keydown", onKeyDown);
  });

  const fileAction = (action: MenuAction, path?: string): void => {
    close();
    props.onMenuAction(action, path);
  };
  const viewAction = (fn: () => void): void => {
    close();
    fn();
  };

  return (
    <div class="tb-menu">
      <div class="tb-menu-item">
        <button
          type="button"
          class="tb-menu-label"
          classList={{ open: openMenu() === "file" }}
          onClick={() => toggle("file")}
          onMouseEnter={() => hover("file")}
        >
          File
        </button>
        <Show when={openMenu() === "file"}>
          <div class="tb-dropdown">
            <button type="button" class="tb-dropitem" onClick={() => fileAction("open-folder")}>
              <span>Open Folder…</span>
            </button>
            <div
              class="tb-dropitem has-submenu"
              classList={{ disabled: props.recents.length === 0 }}
            >
              <span>Open Recent</span>
              <span class="tb-submenu-arrow">
                <ChevronRight />
              </span>
              <Show when={props.recents.length > 0}>
                <div class="tb-submenu">
                  <For each={props.recents}>
                    {(path) => (
                      <button
                        type="button"
                        class="tb-dropitem"
                        title={path}
                        onClick={() => fileAction("open-recent", path)}
                      >
                        <span class="tb-recent-leaf">{leaf(path)}</span>
                        <span class="tb-recent-path">{path}</span>
                      </button>
                    )}
                  </For>
                </div>
              </Show>
            </div>
            <div class="tb-sep" />
            <button type="button" class="tb-dropitem" onClick={() => fileAction("close-window")}>
              <span>Close Window</span>
            </button>
            <button type="button" class="tb-dropitem" onClick={() => fileAction("exit")}>
              <span>Exit</span>
            </button>
          </div>
        </Show>
      </div>

      <div class="tb-menu-item">
        <button
          type="button"
          class="tb-menu-label"
          classList={{ open: openMenu() === "view" }}
          onClick={() => toggle("view")}
          onMouseEnter={() => hover("view")}
        >
          View
        </button>
        <Show when={openMenu() === "view"}>
          <div class="tb-dropdown">
            <button
              type="button"
              class="tb-dropitem"
              onClick={() => viewAction(props.onToggleFiles)}
            >
              <span>Toggle Files</span>
            </button>
          </div>
        </Show>
      </div>
    </div>
  );
}

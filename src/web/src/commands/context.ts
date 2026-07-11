// Web-side context keys for `when` guards on keybindings + palette visibility. The evaluator is an AND of
// clauses, each `!key`, `key`, or `key == 'val'` / `key != 'val'`. See docs/specs/commands.md.

type ContextValue = string | boolean | null;

/** A partial context that takes precedence over the live keys (e.g. the palette evaluating against the pane
 * focused when it opened, before focus moved to the omnibar input). */
export type ContextOverrides = Record<string, ContextValue>;

const context: Record<string, ContextValue> = {};

/** Sets a context key (no-op if unchanged). */
export function setContext(key: string, value: ContextValue): void {
  context[key] = value;
}

/** The focus-derived `when` keys for a focused element — which pane (by `[data-kind]`) holds focus. Shared by
 * the live focusin tracker and the palette's prior-focus snapshot so both classify focus identically. */
export function paneFocusContext(el: Element | null): ContextOverrides {
  const pane = el?.closest("[data-kind]");
  const kind = pane?.getAttribute("data-kind") ?? null;
  const surface = pane?.getAttribute("data-surface") ?? null;
  return {
    focusedPane: kind,
    editorFocused: surface === "editor" || (surface === null && kind === "editor"),
    terminalFocused:
      surface === "terminal" || (surface === null && (kind?.startsWith("terminal:") ?? false)),
    agentFocused: surface === "structured-agent",
    agentComposerFocused:
      surface === "structured-agent" && el?.closest("[data-agent-composer]") !== null,
  };
}

/**
 * Evaluates a `when` expression against the current context; an empty/absent expression is always true.
 * `overrides` win over the live keys — the palette passes the focus context captured when it opened, so a
 * focus-gated command (e.g. terminalFocused) stays visible even though opening the palette moved focus to the
 * omnibar input.
 */
export function evaluateWhen(expr: string | undefined, overrides?: ContextOverrides): boolean {
  if (expr === undefined || expr.trim() === "") {
    return true;
  }
  return expr.split("&&").every((clause) => evalClause(clause.trim(), overrides));
}

function read(key: string, overrides?: ContextOverrides): ContextValue | undefined {
  return overrides !== undefined && key in overrides ? overrides[key] : context[key];
}

function evalClause(clause: string, overrides?: ContextOverrides): boolean {
  if (clause.startsWith("!")) {
    return !truthy(read(clause.slice(1).trim(), overrides));
  }
  const eq = clause.match(/^([\w.]+)\s*(==|!=)\s*(.+)$/);
  if (eq !== null) {
    const key = eq[1] ?? "";
    const value = unquote((eq[3] ?? "").trim());
    const equal = String(read(key, overrides) ?? "") === value;
    return eq[2] === "==" ? equal : !equal;
  }
  return truthy(read(clause, overrides));
}

function truthy(value: ContextValue | undefined): boolean {
  return value !== undefined && value !== null && value !== false && value !== "";
}

function unquote(value: string): string {
  return value.replace(/^['"]|['"]$/g, "");
}

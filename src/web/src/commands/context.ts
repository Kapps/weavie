// Web-side context keys for `when` guards on keybindings + palette visibility. The evaluator is an AND of
// clauses, each `!key`, `key`, or `key == 'val'` / `key != 'val'`. See docs/specs/commands.md.

type ContextValue = string | boolean | null;

const context: Record<string, ContextValue> = {};

/** Sets a context key (no-op if unchanged). */
export function setContext(key: string, value: ContextValue): void {
  context[key] = value;
}

/** Evaluates a `when` expression against the current context; an empty/absent expression is always true. */
export function evaluateWhen(expr: string | undefined): boolean {
  if (expr === undefined || expr.trim() === "") {
    return true;
  }
  return expr.split("&&").every((clause) => evalClause(clause.trim()));
}

function evalClause(clause: string): boolean {
  if (clause.startsWith("!")) {
    return !truthy(context[clause.slice(1).trim()]);
  }
  const eq = clause.match(/^([\w.]+)\s*(==|!=)\s*(.+)$/);
  if (eq !== null) {
    const key = eq[1] ?? "";
    const value = unquote((eq[3] ?? "").trim());
    const equal = String(context[key] ?? "") === value;
    return eq[2] === "==" ? equal : !equal;
  }
  return truthy(context[clause]);
}

function truthy(value: ContextValue | undefined): boolean {
  return value !== undefined && value !== null && value !== false && value !== "";
}

function unquote(value: string): string {
  return value.replace(/^['"]|['"]$/g, "");
}

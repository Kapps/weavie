// The input fields that say what a tool call does, most telling first.
const conciseKeys = [
  "command",
  "cmd",
  "file_path",
  "notebook_path",
  "path",
  "url",
  "pattern",
  "query",
  "description",
  "plan",
];

const pathKeys = new Set(["file_path", "notebook_path", "path"]);

/**
 * A tool request's raw input as one readable line: the first telling field (a path shown through `display`) that
 * its title doesn't already say.
 */
export function conciseToolInput(
  input: string | null,
  title: string | null,
  display: (text: string) => string,
): string | null {
  const fields = toolInputFields(input);
  if (fields === null) return input;
  return (
    conciseKeys
      .map((key) => {
        const text = fieldText(fields[key]);
        return text !== null && pathKeys.has(key) ? display(text) : text;
      })
      .find((text) => text !== null && !(title ?? "").includes(text)) ?? null
  );
}

/** A tool request's raw input pretty-printed, or null when it is not a JSON object. */
export function formattedToolInput(input: string | null): string | null {
  const fields = toolInputFields(input);
  return fields === null ? null : JSON.stringify(fields, null, 2);
}

function toolInputFields(input: string | null): Record<string, unknown> | null {
  if (input === null || !input.trimStart().startsWith("{")) return null;
  try {
    const parsed: unknown = JSON.parse(input);
    return parsed !== null && typeof parsed === "object" && !Array.isArray(parsed)
      ? (parsed as Record<string, unknown>)
      : null;
  } catch {
    return null;
  }
}

function fieldText(value: unknown): string | null {
  const text =
    typeof value === "string"
      ? value
      : Array.isArray(value) && value.every((part) => typeof part === "string")
        ? value.join(" ")
        : null;
  return text === null || text.trim() === "" ? null : text.trim();
}

import { describe, expect, it } from "vitest";
import { conciseToolInput, formattedToolInput } from "./approval-input";

const same = (text: string) => text;

describe("approval tool input", () => {
  it("shows the field that says what the call does instead of the raw JSON", () => {
    const write = JSON.stringify({ file_path: "/repo/soak.txt", content: "a\nb" });
    expect(conciseToolInput(write, "Write soak.txt", same)).toBe("/repo/soak.txt");
    const codex = JSON.stringify({ command: "npm test", cwd: "/repo" });
    expect(conciseToolInput(codex, "Run command", same)).toBe("npm test");
    expect(conciseToolInput(JSON.stringify({ command: ["bash", "-lc", "ls"] }), null, same)).toBe(
      "bash -lc ls",
    );
  });

  it("skips what the title already says once a path is shown relative, and drops input with nothing telling", () => {
    const write = JSON.stringify({ file_path: "/repo/notes.txt", content: "x" });
    expect(
      conciseToolInput(write, "Write notes.txt", (path) => path.replace("/repo/", "")),
    ).toBeNull();
    const bash = JSON.stringify({ command: "sleep 45", description: "Wait for the build" });
    expect(conciseToolInput(bash, "sleep 45", same)).toBe("Wait for the build");
    expect(conciseToolInput(JSON.stringify({ timeout_ms: 9000 }), "Monitor", same)).toBeNull();
  });

  it("keeps plain-text input as it is and formats only JSON objects", () => {
    expect(conciseToolInput("protected input", "Run", same)).toBe("protected input");
    expect(formattedToolInput("protected input")).toBeNull();
    expect(formattedToolInput('{"a":1}')).toBe('{\n  "a": 1\n}');
  });
});

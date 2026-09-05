import type { URI } from "@codingame/monaco-vscode-api/vscode/vs/base/common/uri";
import type * as monaco from "monaco-editor";
import { beforeEach, describe, expect, it, vi } from "vitest";
import type { ClientSession } from "../bridge";

const runtime = vi.hoisted(() => ({
  sessions: new Map<string, ClientSession>(),
}));

vi.mock("../bridge", () => ({
  clientSessionAt: (
    backend: string,
    address: { slot: string; incarnation: string },
  ): ClientSession | undefined =>
    runtime.sessions.get(`${backend}\0${address.slot}\0${address.incarnation}`),
}));

const { activeEditorMessage } = await import("./active-editor-message");
const { protocolUri, sessionFileUri } = await import("./session-uri");

function session(): ClientSession {
  const value = {
    connection: { id: "local" },
    address: { slot: "primary", incarnation: "inc-1" },
  } as ClientSession;
  runtime.sessions.set("local\0primary\0inc-1", value);
  return value;
}

function message(uri: URI): Record<string, unknown> {
  const model = {
    uri,
    getLanguageId: () => "typescript",
    getValueInRange: () => "selected",
  } as unknown as monaco.editor.ITextModel;
  const selection = {
    startLineNumber: 2,
    startColumn: 3,
    endLineNumber: 2,
    endColumn: 11,
    isEmpty: () => false,
  } as monaco.Selection;
  return activeEditorMessage(model, selection) as Record<string, unknown>;
}

beforeEach(() => runtime.sessions.clear());

describe("activeEditorMessage", () => {
  it("publishes native POSIX and Windows drive paths instead of encoded file URIs", () => {
    const owner = session();

    expect(message(sessionFileUri(owner, "/worktree/src/app.ts"))).toMatchObject({
      path: "/worktree/src/app.ts",
    });
    expect(message(sessionFileUri(owner, "C:/Users/Dev/app.ts"))).toMatchObject({
      path: "c:/Users/Dev/app.ts",
    });
  });

  it("preserves a UNC host path and selection payload", () => {
    const owner = session();
    const result = message(protocolUri(owner, "file://server/share/app.ts"));

    expect(result).toEqual({
      path: "//server/share/app.ts",
      languageId: "typescript",
      text: "selected",
      selection: {
        start: { line: 1, character: 2 },
        end: { line: 1, character: 10 },
        isEmpty: false,
      },
    });
  });
});

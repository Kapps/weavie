import Module, { createRequire } from "node:module";
import { describe, expect, it } from "vitest";
import type {
  CodeAction,
  CodeLens,
  Command,
  CompletionItem,
  InlayHint,
  InlineCompletionItem,
} from "vscode-languageclient";

import * as vscodeMock from "../test/vscode-mock";

interface ModuleLoader {
  _load(request: string, parent: unknown, isMain: boolean): unknown;
}

const loader = Module as unknown as ModuleLoader;

function loadConverters(): {
  code: typeof import("vscode-languageclient/lib/common/codeConverter");
  protocol: typeof import("vscode-languageclient/lib/common/protocolConverter");
} {
  const originalLoad = loader._load;
  loader._load = (request, parent, isMain) =>
    request === "vscode" ? vscodeMock : originalLoad.call(Module, request, parent, isMain);
  const require = createRequire(import.meta.url);
  try {
    return {
      code: require("vscode-languageclient/lib/common/codeConverter"),
      protocol: require("vscode-languageclient/lib/common/protocolConverter"),
    };
  } finally {
    loader._load = originalLoad;
  }
}

const converters = loadConverters();

const RAW = "gopls.add_dependency";
const ALIAS = "weavie.lsp.command.converter-test.1";
const command = { title: "Add dependency", command: RAW, arguments: ["example.com/dependency"] };
const range = {
  start: { line: 1, character: 2 },
  end: { line: 3, character: 4 },
};

describe("patched vscode-languageclient command conversion", () => {
  it("round-trips commands nested in every LSP command-bearing resolve type", async () => {
    const protocol = converters.protocol.createConverter(undefined, false, false, (value) =>
      value === RAW ? ALIAS : value,
    );
    const code = converters.code.createConverter(undefined, (value) =>
      value === ALIAS ? RAW : value,
    );

    const standalone = (await protocol.asCodeActionResult([command as Command]))[0] as {
      command: string;
    };
    const action = (await protocol.asCodeAction({ title: "Fix", command } as CodeAction)) as {
      command: { command: string };
    };
    const completion = protocol.asCompletionItem({
      label: "dependency",
      command,
    } as CompletionItem) as {
      command: { command: string };
    };
    const lens = protocol.asCodeLens({ range, command } as CodeLens) as {
      command: { command: string };
    };
    const hint = (
      await protocol.asInlayHints([
        { position: range.start, label: [{ value: "dependency", command }] },
      ] as InlayHint[])
    )[0] as { label: Array<{ command: { command: string } }> };
    const inline = (
      await protocol.asInlineCompletionResult([
        { insertText: "dependency", range, command },
      ] as InlineCompletionItem[])
    )?.[0] as { command: { command: string } };

    expect([
      standalone.command,
      action.command.command,
      completion.command.command,
      lens.command.command,
      hint.label[0]?.command.command,
      inline.command.command,
    ]).toEqual([ALIAS, ALIAS, ALIAS, ALIAS, ALIAS, ALIAS]);

    const resolvedStandalone = code.asCommand(standalone as never);
    const resolvedAction = await code.asCodeAction(action as never);
    const resolvedCompletion = code.asCompletionItem(completion as never);
    const resolvedLens = code.asCodeLens(lens as never);
    const resolvedHint = code.asInlayHint(hint as never);

    expect([
      resolvedStandalone.command,
      resolvedAction.command?.command,
      resolvedCompletion.command?.command,
      resolvedLens.command?.command,
      typeof resolvedHint.label === "string" ? undefined : resolvedHint.label[0]?.command?.command,
    ]).toEqual([RAW, RAW, RAW, RAW, RAW]);
  });
});

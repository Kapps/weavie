import { afterEach, describe, expect, it, vi } from "vitest";
import type { ClientSession } from "../bridge";
import type { CommandCapture } from "../commands/registry";

const mocks = vi.hoisted(() => ({
  capture: null as CommandCapture | null,
  read: vi.fn(),
  notify: vi.fn(),
}));
vi.mock("../clipboard-read", () => ({ readClipboardContent: mocks.read }));
vi.mock("../commands/registry", () => ({
  registerCapturedCommand: (_id: string, capture: CommandCapture) => {
    mocks.capture = capture;
    return () => {};
  },
}));
vi.mock("../notify/notify", () => ({ notify: mocks.notify }));
const { installComposerClipboardCommand, registerComposerPasteTarget } = await import(
  "./composer-clipboard"
);

afterEach(() => {
  vi.unstubAllGlobals();
  vi.clearAllMocks();
});

describe("composer clipboard ownership", () => {
  it.each([
    "text",
    "image",
  ])("keeps a delayed %s paste in the captured reply after focus changes", async (kind) => {
    let resolve!: (content: unknown) => void;
    mocks.read.mockReturnValue(
      new Promise((done) => {
        resolve = done;
      }),
    );
    const session = { closed: false } as ClientSession;
    const textarea = {
      selectionStart: 2,
      selectionEnd: 5,
      setSelectionRange: vi.fn(),
    } as unknown as HTMLTextAreaElement;
    const document = { activeElement: textarea as unknown };
    vi.stubGlobal("document", document);
    const reply = { session, draft: () => "a OLD z", setDraft: vi.fn(), pasteImage: vi.fn() };
    const main = { session, draft: () => "main", setDraft: vi.fn(), pasteImage: vi.fn() };
    let current = reply;
    const unregister = registerComposerPasteTarget(textarea, () => current);
    const uninstall = installComposerClipboardCommand();
    try {
      const run = mocks.capture!({ session }, undefined);
      current = main;
      document.activeElement = {};
      const pending = run(undefined, { session });
      resolve(
        kind === "image" ? { kind, mime: "image/png", dataB64: "image" } : { kind, text: "new" },
      );
      await pending;
      if (kind === "image") expect(reply.pasteImage).toHaveBeenCalledWith("image/png", "image");
      else expect(reply.setDraft).toHaveBeenCalledWith("a new z");
      expect(main.setDraft).not.toHaveBeenCalled();
      expect(main.pasteImage).not.toHaveBeenCalled();
      expect(textarea.setSelectionRange).not.toHaveBeenCalled();
    } finally {
      unregister();
      uninstall();
    }
  });
});

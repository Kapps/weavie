import { expect, it, vi } from "vitest";
import type { ClientSession } from "../bridge";

const harness = vi.hoisted(() => ({
  installers: [] as Array<(session: ClientSession) => undefined | (() => void)>,
  selected: null as ClientSession | null,
}));

vi.mock("../bridge", () => ({
  registerSessionFeature: (installer: (session: ClientSession) => undefined | (() => void)) => {
    harness.installers.push(installer);
    return () => {};
  },
  selectedSession: () => harness.selected,
}));

const credentials = await import("./credential-store");
const pickers = await import("../chrome/picker-store");

function sessionForTest() {
  const handlers = new Map<string, (payload: unknown) => void>();
  const posted: Array<{ feature: string; name: string; payload: unknown }> = [];
  const request = vi.fn();
  const session = {
    feature: (feature: string) => ({
      on: (name: string, handler: (payload: unknown) => void) => {
        handlers.set(`${feature}/${name}`, handler);
        return () => handlers.delete(`${feature}/${name}`);
      },
      request: (name: string, payload: unknown) => request(feature, name, payload),
      publish: (name: string, payload: unknown) => posted.push({ feature, name, payload }),
    }),
  } as unknown as ClientSession;
  for (const install of harness.installers) install(session);
  return { session, handlers, posted, request };
}

const prompt = {
  id: "notion",
  displayName: "Notion",
  keyNoun: "personal access token",
  keyPageUrl: "https://app.notion.com/developers/tokens",
};

it("shows a credential prompt only for the session that asked and answers on that session", async () => {
  const owner = sessionForTest();
  const other = sessionForTest();
  harness.selected = other.session;
  owner.handlers.get("credentials/prompt")?.(prompt);
  expect(credentials.selectedCredentialPrompt()).toBeNull();

  harness.selected = owner.session;
  expect(credentials.selectedCredentialPrompt()).toEqual({ session: owner.session, ...prompt });
  owner.request.mockResolvedValueOnce({ ok: true, error: "" });
  await credentials.submitCredential(owner.session, "notion", "ntn_x");
  expect(owner.request).toHaveBeenCalledWith("credentials", "save", { id: "notion", key: "ntn_x" });

  credentials.dismissCredentialPrompt(owner.session);
  expect(credentials.selectedCredentialPrompt()).toBeNull();
  expect(owner.posted).toEqual([{ feature: "credentials", name: "dismiss", payload: {} }]);
  expect(other.posted).toEqual([]);
});

it("answers a picker once, on its owning session", () => {
  const owner = sessionForTest();
  harness.selected = owner.session;
  owner.handlers.get("picker/show")?.({ title: "Connect Service", value: "", options: [] });
  expect(pickers.selectedChoicePicker()?.title).toBe("Connect Service");

  pickers.pickChoice(owner.session, "notion");
  pickers.dismissChoicePicker(owner.session);

  expect(pickers.selectedChoicePicker()).toBeNull();
  expect(owner.posted).toEqual([{ feature: "picker", name: "pick", payload: { id: "notion" } }]);
});

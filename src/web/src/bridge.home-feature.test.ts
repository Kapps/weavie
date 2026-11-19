import { expect, it, vi } from "vitest";

// The bridge reads the page's globals as it loads; this page has no host of its own.
vi.stubGlobal("window", { location: new URL("http://page.test/") });
const { connectBackend, disconnectBackend, registerHomeFeature, registerHostFeature } =
  await import("./bridge");

it("installs a home feature only on the connection that serves the page", () => {
  const everyHost: string[] = [];
  const home: string[] = [];
  registerHostFeature((connection) => {
    everyHost.push(connection.id);
    return undefined;
  });
  registerHomeFeature((connection) => {
    home.push(connection.id);
    return undefined;
  });

  connectBackend("remote:devbox", "devbox", () => new Promise(() => {}));

  expect(everyHost).toContain("remote:devbox");
  expect(home).not.toContain("remote:devbox");
  disconnectBackend("remote:devbox");
});

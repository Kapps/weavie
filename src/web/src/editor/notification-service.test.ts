import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

// Guards the regression that prompted this bridge: Monaco reports a failed refactor (a rejected rename, a rename
// that can't apply/compute its edits) through INotificationService.info/error. The standalone default only
// console-logs those, so the user saw nothing. The mocks keep the real monaco service layer out of the node test
// env (per lsp-client.test.ts); Severity is the only monaco shape the bridge reasons about.

vi.mock("@codingame/monaco-vscode-api/services", () => ({
  INotificationService: { toString: () => "INotificationService" },
  NoOpNotification: class {},
  NotificationsFilter: { OFF: 0, ERROR: 1 },
  Severity: { Ignore: 0, Info: 1, Warning: 2, Error: 3 },
  SyncDescriptor: class {},
}));
vi.mock("@codingame/monaco-vscode-api/vscode/vs/base/common/event", () => ({
  Event: { None: () => ({ dispose() {} }) },
}));

import { Severity } from "@codingame/monaco-vscode-api/services";
import { setNotifySink } from "../notify/notify";
import { WeavieNotificationService } from "./notification-service";

const raised: Array<{ level: string; message: string }> = [];

beforeEach(() => {
  raised.length = 0;
  setNotifySink((level, message) => raised.push({ level, message }));
});
afterEach(() => setNotifySink(() => {}));

describe("WeavieNotificationService", () => {
  const service = new WeavieNotificationService();

  it("raises an error toast for a failed rename (INotificationService.error)", () => {
    service.error("Rename failed to apply edits");
    expect(raised).toEqual([{ level: "error", message: "Rename failed to apply edits" }]);
  });

  it("raises an info toast for a rejected rename (INotificationService.info)", () => {
    service.info("You cannot rename this element");
    expect(raised).toEqual([{ level: "info", message: "You cannot rename this element" }]);
  });

  it("maps notify() severities to toast levels and unwraps Error messages", () => {
    service.notify({ severity: Severity.Warning, message: "heads up" });
    service.notify({ severity: Severity.Error, message: new Error("boom") });
    expect(raised).toEqual([
      { level: "warn", message: "heads up" },
      { level: "error", message: "boom" },
    ]);
  });

  it("joins a multi-message array into one toast", () => {
    service.warn(["first", "second"]);
    expect(raised).toEqual([{ level: "warn", message: "first\nsecond" }]);
  });
});

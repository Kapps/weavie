import { beforeEach, describe, expect, it, vi } from "vitest";

const worker = vi.hoisted(() => ({
  computeDiff: vi.fn(),
  models: [] as Array<{
    uri: { toString(): string };
    disposed: boolean;
    dispose: ReturnType<typeof vi.fn>;
  }>,
}));

vi.mock("@codingame/monaco-vscode-api/services", () => ({
  IEditorWorkerService: Symbol("IEditorWorkerService"),
  StandaloneServices: { get: () => ({ computeDiff: worker.computeDiff }) },
}));

vi.mock("../monaco-setup", () => ({
  monaco: {
    Uri: {
      from: ({ scheme, authority, path }: Record<string, string>) => ({
        toString: () => `${scheme}://${authority}${path}`,
      }),
    },
    editor: {
      createModel: (_value: string, _language: string, uri: { toString(): string }) => {
        if (
          worker.models.some((model) => !model.disposed && model.uri.toString() === uri.toString())
        ) {
          throw new Error(`Model already exists: ${uri.toString()}`);
        }
        const model = {
          uri,
          disposed: false,
          dispose: vi.fn(() => {
            model.disposed = true;
          }),
        };
        worker.models.push(model);
        return model;
      },
    },
  },
}));

const { DiffComputer } = await import("./diff-computer");

function deferred<T>(): {
  promise: Promise<T>;
  resolve: (value: T) => void;
  reject: (reason: unknown) => void;
} {
  let resolve!: (value: T) => void;
  let reject!: (reason: unknown) => void;
  const promise = new Promise<T>((done, fail) => {
    resolve = done;
    reject = fail;
  });
  return { promise, resolve, reject };
}

function liveModel(uri: string): {
  uri: { toString(): string };
  getVersionId(): number;
} {
  return { uri: { toString: () => uri }, getVersionId: () => 1 };
}

describe("DiffComputer", () => {
  beforeEach(() => {
    worker.computeDiff.mockReset();
    worker.models.length = 0;
  });

  it("retires source models only after their worker calculation settles", async () => {
    const firstWorker = deferred<{ quitEarly: boolean; changes: [] }>();
    worker.computeDiff
      .mockReturnValueOnce(firstWorker.promise)
      .mockResolvedValueOnce({ quitEarly: false, changes: [] });
    const computer = new DiffComputer();
    const sources = {
      original: "before",
      claudeVersion: undefined,
      acceptedBaseline: undefined,
    };

    const first = computer.compute("file:///first", sources, liveModel("file:///first") as never);
    computer.dispose();
    expect(worker.models[0]?.dispose).not.toHaveBeenCalled();

    await expect(
      computer.compute("file:///second", sources, liveModel("file:///second") as never),
    ).resolves.toMatchObject({ status: "ready" });
    expect(worker.models[0]?.dispose).not.toHaveBeenCalled();
    expect(worker.models[0]?.uri.toString()).not.toBe(worker.models[1]?.uri.toString());

    firstWorker.resolve({ quitEarly: false, changes: [] });
    await expect(first).resolves.toMatchObject({ status: "ready" });
    expect(worker.models[0]?.dispose).toHaveBeenCalledOnce();
    expect(worker.models[1]?.dispose).not.toHaveBeenCalled();

    computer.dispose();
    expect(worker.models[1]?.dispose).toHaveBeenCalledOnce();
  });

  it("waits for every worker pair before disposing a failed source set", async () => {
    const primary = deferred<{ quitEarly: boolean; changes: [] }>();
    const user = deferred<{ quitEarly: boolean; changes: [] }>();
    const faded = deferred<{ quitEarly: boolean; changes: [] }>();
    worker.computeDiff
      .mockReturnValueOnce(primary.promise)
      .mockReturnValueOnce(user.promise)
      .mockReturnValueOnce(faded.promise);
    const computer = new DiffComputer();
    const calculation = computer.compute(
      "file:///reviewed",
      {
        original: "original",
        claudeVersion: "claude",
        acceptedBaseline: "accepted",
      },
      liveModel("file:///reviewed") as never,
    );

    computer.dispose();
    primary.reject(new Error("primary failed"));
    await Promise.resolve();
    expect(worker.models.every((model) => model.dispose.mock.calls.length === 0)).toBe(true);

    user.resolve({ quitEarly: false, changes: [] });
    faded.resolve({ quitEarly: false, changes: [] });
    await expect(calculation).resolves.toMatchObject({ status: "failed" });
    expect(worker.models.every((model) => model.dispose.mock.calls.length === 1)).toBe(true);
  });
});

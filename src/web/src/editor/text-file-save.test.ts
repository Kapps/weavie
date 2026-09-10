import { Event } from "@codingame/monaco-vscode-api/vscode/vs/base/common/event";
import { URI } from "@codingame/monaco-vscode-api/vscode/vs/base/common/uri";
import { TextFileEditorModel } from "@codingame/monaco-vscode-api/vscode/vs/workbench/services/textfile/common/textFileEditorModel";
import { EncodingMode } from "@codingame/monaco-vscode-api/vscode/vs/workbench/services/textfile/common/textfiles";
import { expect, it, onTestFinished, vi } from "vitest";

function deferred<T>() {
  let resolve!: (value: T) => void;
  let reject!: (error: Error) => void;
  const promise = new Promise<T>((accept, fail) => {
    resolve = accept;
    reject = fail;
  });
  return { promise, resolve, reject };
}

function fixture() {
  const resource = URI.file("/save-race.txt");
  const stat = { resource, mtime: 1, ctime: 1, size: 8, etag: "initial" };
  let buffer = "original";
  let disk = buffer;
  let alternativeVersion = 1;
  let acknowledgement = deferred<typeof stat>();
  const participants = deferred<void>();
  const runSaveParticipants = vi.fn(async () => {});
  let cancelSave = () => {};
  const textModel = {
    getAlternativeVersionId: () => alternativeVersion,
    getLanguageId: () => "plaintext",
    pushStackElement: vi.fn(),
    createSnapshot: () => {
      let snapshot: string | null = buffer;
      return {
        read: () => {
          const value = snapshot;
          snapshot = null;
          return value;
        },
      };
    },
  };
  const write = vi.fn(
    (
      _resource: URI,
      snapshot: ReturnType<typeof textModel.createSnapshot>,
      _options: { encoding: string },
    ) => {
      disk = snapshot.read() ?? "";
      return acknowledgement.promise;
    },
  );
  const model = new TextFileEditorModel(
    ...([
      resource,
      "utf8",
      "plaintext",
      {},
      { getModel: () => textModel },
      { onDidFilesChange: Event.None },
      { write, files: { runSaveParticipants } },
      {},
      { trace: vi.fn(), error: vi.fn() },
      { registerWorkingCopy: () => ({ dispose() {} }) },
      {
        onDidChangeFilesAssociation: Event.None,
        onDidChangeReadonly: Event.None,
        isReadonly: () => false,
        preventSaveConflicts: () => false,
      },
      { getUriLabel: () => resource.path },
      {},
      {},
      { defaultUriScheme: "file" },
      { whenInstalledExtensionsRegistered: async () => {} },
      {
        withProgress: (
          _options: unknown,
          task: (progress: unknown) => Promise<unknown>,
          cancel: () => void,
        ) => {
          cancelSave = cancel;
          return task({ report: vi.fn() });
        },
      },
    ] as unknown as ConstructorParameters<typeof TextFileEditorModel>),
  );
  onTestFinished(() => model.dispose());
  const internals = model as unknown as {
    textEditorModelHandle: URI;
    lastResolvedFileStat: typeof stat;
    onModelContentChanged(model: typeof textModel, undoOrRedo: boolean): void;
  };
  internals.textEditorModelHandle = resource;
  internals.lastResolvedFileStat = stat;
  model.setDirty(false);
  return {
    model,
    write,
    holdParticipants: () => runSaveParticipants.mockImplementation(() => participants.promise),
    finishParticipants: () => participants.resolve(),
    cancelSave: () => cancelSave(),
    disk: () => disk,
    edit(value: string, version: number, undoOrRedo: boolean) {
      buffer = value;
      alternativeVersion = version;
      internals.onModelContentChanged(textModel, undoOrRedo);
    },
    save: () => model.save({ skipSaveParticipants: true, ignoreErrorHandler: true }),
    acknowledge() {
      acknowledgement.resolve({ ...stat, mtime: 2, etag: "saved" });
      acknowledgement = deferred<typeof stat>();
    },
    fail(error: Error) {
      acknowledgement.reject(error);
      acknowledgement = deferred<typeof stat>();
    },
  };
}

it("persists undo to the original revision while the corrected write awaits acknowledgement", async () => {
  const f = fixture();
  f.edit("corrected", 2, false);
  const correctedSave = f.save();
  expect(f.write).toHaveBeenCalledOnce();
  expect(f.disk()).toBe("corrected");

  f.edit("original", 1, true);
  expect(f.model.isDirty()).toBe(true);
  f.acknowledge();
  expect(await correctedSave).toBe(false);
  expect(f.model.isDirty()).toBe(true);

  const undoSave = f.save();
  expect(f.disk()).toBe("original");
  f.acknowledge();
  expect(await undoSave).toBe(true);
  expect(f.model.isDirty()).toBe(false);

  f.edit("corrected", 2, true);
  expect(f.model.isDirty()).toBe(true);
  f.edit("original", 1, true);
  expect(f.model.isDirty()).toBe(false);
});

it("tracks the written revision when another edit arrives before acknowledgement", async () => {
  const f = fixture();
  f.edit("corrected", 2, false);
  const saving = f.save();
  f.edit("newer edit", 3, false);
  f.acknowledge();
  expect(await saving).toBe(false);
  expect(f.model.isDirty()).toBe(true);

  f.edit("corrected", 2, true);
  expect(f.model.isDirty()).toBe(false);
  f.edit("original", 1, true);
  expect(f.model.isDirty()).toBe(true);
});

it("keeps the previous saved revision when the write fails", async () => {
  const f = fixture();
  f.edit("corrected", 2, false);
  const saving = f.save();
  f.edit("newer edit", 3, false);
  const error = new Error("write failed");
  f.fail(error);
  await expect(saving).rejects.toThrow(error);
  expect(f.model.isDirty()).toBe(true);

  f.edit("corrected", 2, true);
  expect(f.model.isDirty()).toBe(true);
  f.edit("original", 1, true);
  expect(f.model.isDirty()).toBe(false);
});

it("queues the undone revision while the previous save is still running", async () => {
  const f = fixture();
  f.edit("corrected", 2, false);
  const correctedSave = f.save();
  f.edit("original", 1, true);
  const undoSave = f.save();
  expect(f.write).toHaveBeenCalledOnce();

  f.acknowledge();
  await correctedSave;
  expect(f.write).toHaveBeenCalledTimes(2);
  expect(f.disk()).toBe("original");
  f.acknowledge();
  expect(await undoSave).toBe(true);
  expect(f.model.isDirty()).toBe(false);
});

it("accepts an outstanding write acknowledgement after the model is disposed", async () => {
  const f = fixture();
  f.edit("corrected", 2, false);
  const saving = f.save();
  f.model.dispose();
  f.acknowledge();
  await expect(saving).resolves.toBe(false);
  expect(f.model.isDisposed()).toBe(true);
});

it("preserves an encoding change queued behind a save of unchanged text", async () => {
  const f = fixture();
  const saving = f.model.save({ force: true, skipSaveParticipants: true });
  expect(f.write).toHaveBeenCalledOnce();
  const encodingSave = f.model.setEncoding("utf16le", EncodingMode.Encode);
  expect(f.model.isDirty()).toBe(true);
  f.acknowledge();
  await saving;
  expect(f.write).toHaveBeenCalledTimes(2);
  expect(f.write.mock.calls[1]?.[2].encoding).toBe("utf16le");
  f.acknowledge();
  await encodingSave;
  expect(f.model.isDirty()).toBe(false);
});

it("undoes cleanly when save participants are cancelled before any write", async () => {
  const f = fixture();
  f.holdParticipants();
  f.edit("corrected", 2, false);
  const saving = f.model.save({ ignoreErrorHandler: true });
  expect(f.write).not.toHaveBeenCalled();
  f.edit("original", 1, true);
  expect(f.model.isDirty()).toBe(false);
  f.cancelSave();
  f.finishParticipants();
  expect(await saving).toBe(true);
  expect(f.write).not.toHaveBeenCalled();
  expect(f.disk()).toBe("original");
});

it("persists an encoding change requested during a dirty text save", async () => {
  const f = fixture();
  f.edit("corrected", 2, false);
  const saving = f.save();
  const encodingSave = f.model.setEncoding("utf16le", EncodingMode.Encode);
  f.acknowledge();
  await saving;
  expect(f.write).toHaveBeenCalledTimes(2);
  expect(f.write.mock.calls[1]?.[2].encoding).toBe("utf16le");
  f.acknowledge();
  await encodingSave;
  expect(f.model.isDirty()).toBe(false);
});

it("cleans a revision restored by redo while its write awaited acknowledgement", async () => {
  const f = fixture();
  f.edit("corrected", 2, false);
  const saving = f.save();
  f.edit("original", 1, true);
  f.edit("corrected", 2, true);
  expect(f.model.isDirty()).toBe(true);
  f.acknowledge();
  expect(await saving).toBe(true);
  expect(f.model.isDirty()).toBe(false);
  expect(f.disk()).toBe("corrected");
});

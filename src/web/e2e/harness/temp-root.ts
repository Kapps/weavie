import { mkdtemp, readdir, realpath, rm } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join } from "node:path";

// One temp root per Playwright run: every e2e temp dir lives under it, so the run's teardown can prove each
// test removed what it created, then remove the root itself.
const ROOT = "WEAVIE_E2E_TEMP";

// Canonicalized: on macOS os.tmpdir() is under /var, a symlink to /private/var, and the kernel resolves a
// process's cwd — so the fake claude's Directory.GetCurrentDirectory() (which backs {{WORKSPACE}}) would yield
// /private/var while the host holds the unresolved /var string, and change-tracking path checks miss.
export async function makeTempDir(prefix: string): Promise<string> {
  const root = process.env[ROOT];
  if (root === undefined) {
    throw new Error(
      `${ROOT} is unset: run e2e through a Playwright config whose globalSetup is temp-root.`,
    );
  }
  return realpath(await mkdtemp(join(root, prefix)));
}

export default async function openTempRoot(): Promise<() => Promise<void>> {
  const root = await mkdtemp(join(tmpdir(), "weavie-e2e-run-"));
  process.env[ROOT] = root;
  return async () => {
    const leaked = await readdir(root);
    await rm(root, { recursive: true, force: true, maxRetries: 5, retryDelay: 100 });
    if (leaked.length > 0) {
      throw new Error(
        `The e2e run left ${leaked.length} temp entries behind; each test must remove what it creates: ${leaked.join(", ")}`,
      );
    }
  };
}

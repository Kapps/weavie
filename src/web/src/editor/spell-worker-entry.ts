import { bootstrapWebWorker } from "@codingame/monaco-vscode-api/vscode/vs/base/common/worker/webWorkerBootstrap";
import { SpellWorker } from "./spell-worker";

bootstrapWebWorker((server) => new SpellWorker(server));

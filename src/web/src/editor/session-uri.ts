import { URI } from "@codingame/monaco-vscode-api/vscode/vs/base/common/uri";
import type { ClientSession } from "../bridge";
import { canonicalFsPath } from "./fs-path";
import { sessionUriHostParts, sessionUriParts } from "./session-uri-owner";

export {
  SESSION_FILE_SCHEME,
  sessionForUri,
  sessionOwnsUri,
  sessionUriHostPath,
} from "./session-uri-owner";

export function sessionFileUri(session: ClientSession, path: string): URI {
  return namespaceUri(session, URI.file(canonicalFsPath(path)));
}

export function protocolUri(session: ClientSession, value: string): URI {
  const uri = URI.parse(value);
  return uri.scheme === "file" ? namespaceUri(session, uri) : uri;
}

export function hostUriString(uri: URI): string {
  const host = sessionUriHostParts(uri);
  return host === undefined ? uri.toString() : uri.with(host).toString();
}

function namespaceUri(session: ClientSession, uri: URI): URI {
  return uri.with(sessionUriParts(session, uri));
}

import { URI } from "@codingame/monaco-vscode-api/vscode/vs/base/common/uri";
import { type ClientSession, clientSessionAt } from "../bridge";
import { canonicalFsPath, uriHostPath } from "./fs-path";

interface UriOwner {
  backend: string;
  slot: string;
  incarnation: string;
  hostScheme: string;
  hostAuthority: string;
  hostPath: string;
  hostQuery: string;
  hostFragment: string;
}

export const SESSION_FILE_SCHEME = "weavie-file";
const OWNER_PREFIX = "weavie-session:";
const namespaces = new WeakMap<ClientSession, string>();
let namespaceSequence = 0;

function namespaceFor(session: ClientSession): string {
  const existing = namespaces.get(session);
  if (existing !== undefined) {
    return existing;
  }
  const created = `weavie-session-${++namespaceSequence}`;
  namespaces.set(session, created);
  return created;
}

function encodedOwner(session: ClientSession, uri: URI): string {
  return `${OWNER_PREFIX}${encodeURIComponent(
    JSON.stringify({
      backend: session.connection.id,
      slot: session.address.slot,
      incarnation: session.address.incarnation,
      hostScheme: uri.scheme,
      hostAuthority: uri.authority,
      hostPath: uri.path,
      hostQuery: uri.query,
      hostFragment: uri.fragment,
    } satisfies UriOwner),
  )}`;
}

export function sessionFileUri(session: ClientSession, path: string): URI {
  return namespaceUri(session, URI.file(canonicalFsPath(path)));
}

export function sessionForUri(uri: Pick<URI, "fragment">): ClientSession | undefined {
  const owner = uriOwner(uri);
  return owner === undefined
    ? undefined
    : clientSessionAt(owner.backend, {
        slot: owner.slot,
        incarnation: owner.incarnation,
      });
}

export function sessionUriHostPath(uri: Pick<URI, "authority" | "path" | "fragment">): string {
  const owner = uriOwner(uri);
  return uriHostPath({
    authority: owner?.hostAuthority ?? uri.authority,
    path: owner?.hostPath ?? uri.path,
  });
}

export function protocolUri(session: ClientSession, value: string): URI {
  const uri = URI.parse(value);
  return uri.scheme === "file" ? namespaceUri(session, uri) : uri;
}

export function hostUriString(uri: URI): string {
  const owner = uriOwner(uri);
  return owner === undefined
    ? uri.toString()
    : uri
        .with({
          scheme: owner.hostScheme,
          authority: owner.hostAuthority,
          path: owner.hostPath,
          query: owner.hostQuery,
          fragment: owner.hostFragment,
        })
        .toString();
}

function namespaceUri(session: ClientSession, uri: URI): URI {
  const path = uri.path.startsWith("/") ? uri.path : `/${uri.path}`;
  return uri.with({
    scheme: SESSION_FILE_SCHEME,
    authority: "",
    path: `/${namespaceFor(session)}${path}`,
    fragment: encodedOwner(session, uri),
  });
}

function uriOwner(uri: Pick<URI, "fragment">): UriOwner | undefined {
  if (!uri.fragment.startsWith(OWNER_PREFIX)) {
    return undefined;
  }
  try {
    const owner = JSON.parse(
      decodeURIComponent(uri.fragment.slice(OWNER_PREFIX.length)),
    ) as Partial<UriOwner>;
    return typeof owner.backend === "string" &&
      typeof owner.slot === "string" &&
      typeof owner.incarnation === "string" &&
      typeof owner.hostScheme === "string" &&
      typeof owner.hostAuthority === "string" &&
      typeof owner.hostPath === "string" &&
      typeof owner.hostQuery === "string" &&
      typeof owner.hostFragment === "string"
      ? {
          backend: owner.backend,
          slot: owner.slot,
          incarnation: owner.incarnation,
          hostScheme: owner.hostScheme,
          hostAuthority: owner.hostAuthority,
          hostPath: owner.hostPath,
          hostQuery: owner.hostQuery,
          hostFragment: owner.hostFragment,
        }
      : undefined;
  } catch {
    return undefined;
  }
}

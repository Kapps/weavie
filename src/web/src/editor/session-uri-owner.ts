import { type ClientSession, clientSessionAt } from "../bridge";
import { uriHostPath } from "./fs-path";
import { SESSION_FILE_SCHEME } from "./session-uri-scheme";

export { SESSION_FILE_SCHEME } from "./session-uri-scheme";

interface UriParts {
  scheme: string;
  authority: string;
  path: string;
  query: string;
  fragment: string;
}

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

export function sessionUriParts(
  session: ClientSession,
  uri: UriParts,
): Pick<UriParts, "scheme" | "authority" | "path" | "fragment"> {
  const path = uri.path.startsWith("/") ? uri.path : `/${uri.path}`;
  return {
    scheme: SESSION_FILE_SCHEME,
    authority: "",
    path: `/${namespaceFor(session)}${path}`,
    fragment: `${OWNER_PREFIX}${encodeURIComponent(
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
    )}`,
  };
}

export function sessionForUri(uri: Pick<UriParts, "fragment">): ClientSession | undefined {
  const owner = uriOwner(uri);
  return owner === undefined
    ? undefined
    : clientSessionAt(owner.backend, {
        slot: owner.slot,
        incarnation: owner.incarnation,
      });
}

export function sessionUriHostPath(uri: Pick<UriParts, "authority" | "path" | "fragment">): string {
  const owner = uriOwner(uri);
  return uriHostPath({
    authority: owner?.hostAuthority ?? uri.authority,
    path: owner?.hostPath ?? uri.path,
  });
}

export function sessionUriHostParts(uri: Pick<UriParts, "fragment">): UriParts | undefined {
  const owner = uriOwner(uri);
  return owner === undefined
    ? undefined
    : {
        scheme: owner.hostScheme,
        authority: owner.hostAuthority,
        path: owner.hostPath,
        query: owner.hostQuery,
        fragment: owner.hostFragment,
      };
}

function uriOwner(uri: Pick<UriParts, "fragment">): UriOwner | undefined {
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

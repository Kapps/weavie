export interface SessionAddress {
  slot: string;
  incarnation: string;
}

export type MessageScope = "host" | "session";
export type MessageKind = "event" | "request" | "response" | "cancel";

export interface MessageEnvelope {
  scope: MessageScope;
  session: SessionAddress | null;
  kind: MessageKind;
  requestId: string | null;
  feature: string;
  name: string;
  payload: unknown;
  error: string | null;
}

export function parseEnvelope(raw: string): MessageEnvelope | null {
  try {
    const value = JSON.parse(raw) as Partial<MessageEnvelope>;
    if (
      (value.scope !== "host" && value.scope !== "session") ||
      !["event", "request", "response", "cancel"].includes(value.kind ?? "") ||
      typeof value.feature !== "string" ||
      value.feature.length === 0 ||
      typeof value.name !== "string" ||
      value.name.length === 0 ||
      !("payload" in value) ||
      !("session" in value) ||
      !("requestId" in value) ||
      !("error" in value)
    ) {
      return null;
    }
    const session =
      value.session !== null &&
      typeof value.session === "object" &&
      typeof value.session?.slot === "string" &&
      value.session.slot.length > 0 &&
      typeof value.session.incarnation === "string" &&
      value.session.incarnation.length > 0
        ? value.session
        : null;
    if (
      (value.scope === "host" && value.session !== null) ||
      (value.scope === "session" && session === null)
    ) {
      return null;
    }
    const requestId =
      typeof value.requestId === "string" && value.requestId.length > 0 ? value.requestId : null;
    if (
      (value.kind === "event" && value.requestId !== null) ||
      (value.kind !== "event" && requestId === null) ||
      (value.kind !== "response" && value.error !== null) ||
      (value.error !== null && typeof value.error !== "string")
    ) {
      return null;
    }
    return {
      scope: value.scope,
      session,
      kind: value.kind as MessageKind,
      requestId,
      feature: value.feature,
      name: value.name,
      payload: value.payload,
      error: typeof value.error === "string" ? value.error : null,
    };
  } catch {
    return null;
  }
}

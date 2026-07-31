import { For, type JSX } from "solid-js";
import type { ClientSession } from "../bridge";
import { refLinkPrefixFor } from "../terminal/ref-link-store";
import { openUrlExternal } from "../terminal/terminal-links";
import { type AgentTextPart, linkAgentText } from "./AgentPaneLinkify";

export function AgentLinkedText(props: {
  session: ClientSession | null;
  text: string;
}): JSX.Element {
  return (
    <For
      each={linkAgentText(
        props.text,
        props.session !== null && refLinkPrefixFor(props.session) !== null,
      )}
    >
      {(part) => <AgentTextPartView part={part} session={props.session} />}
    </For>
  );
}

function AgentTextPartView(props: {
  part: AgentTextPart;
  session: ClientSession | null;
}): JSX.Element {
  const part = props.part;
  if (part.kind === "text") return part.text;
  if (part.kind === "url") {
    return (
      <a
        href={part.target}
        onClick={(event) => {
          event.preventDefault();
          openUrlExternal(part.target);
        }}
      >
        {part.text}
      </a>
    );
  }
  if (part.kind === "ref") {
    return (
      <a
        href={`${
          props.session === null ? "" : (refLinkPrefixFor(props.session) ?? "")
        }${part.number}`}
        onClick={(event) => {
          event.preventDefault();
          const prefix = props.session === null ? null : refLinkPrefixFor(props.session);
          if (prefix !== null) openUrlExternal(prefix + part.number);
        }}
      >
        {part.text}
      </a>
    );
  }
  return (
    <a
      href={`file://${part.path}`}
      onClick={(event) => {
        event.preventDefault();
        props.session?.feature("files").publish("reveal", {
          path: part.path,
          line: part.line,
          preview: false,
        });
      }}
    >
      {part.text}
    </a>
  );
}

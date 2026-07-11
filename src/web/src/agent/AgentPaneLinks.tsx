import { For, type JSX } from "solid-js";
import { postToHost } from "../bridge";
import { refLinkPrefix } from "../terminal/ref-link-store";
import { openUrlExternal } from "../terminal/terminal-links";
import { type AgentTextPart, linkAgentText } from "./AgentPaneLinkify";

export function AgentLinkedText(props: { text: string }): JSX.Element {
  return (
    <For each={linkAgentText(props.text, refLinkPrefix() !== null)}>
      {(part) => <AgentTextPartView part={part} />}
    </For>
  );
}

function AgentTextPartView(props: { part: AgentTextPart }): JSX.Element {
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
        href={`${refLinkPrefix() ?? ""}${part.number}`}
        onClick={(event) => {
          event.preventDefault();
          const prefix = refLinkPrefix();
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
        postToHost({ type: "reveal-file", path: part.path, line: part.line });
      }}
    >
      {part.text}
    </a>
  );
}

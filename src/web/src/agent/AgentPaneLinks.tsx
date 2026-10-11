import { For, type JSX } from "solid-js";
import type { ClientSession } from "../bridge";
import { openLink } from "../editor/source/source-links";
import { revealFileIn } from "../files/reveal";
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
          openLink(props.session, part.target);
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
    <FileLink session={props.session} path={part.path} line={part.line}>
      {part.text}
    </FileLink>
  );
}

/** A shortened path linking to its full one: the directory ellipsizes first, so the file name stays readable. */
export function AgentPathLink(props: {
  session: ClientSession | null;
  path: string;
  text: string;
}): JSX.Element {
  const split = (): number =>
    Math.max(props.text.lastIndexOf("/"), props.text.lastIndexOf("\\"), 0);
  return (
    <FileLink session={props.session} path={props.path} line={undefined}>
      <span class="agent-path-dir">{props.text.slice(0, split())}</span>
      <span class="agent-path-leaf">{props.text.slice(split())}</span>
    </FileLink>
  );
}

function FileLink(props: {
  session: ClientSession | null;
  path: string;
  line: number | undefined;
  children: JSX.Element;
}): JSX.Element {
  return (
    <a
      href={`file://${props.path}`}
      onClick={(event) => {
        event.preventDefault();
        revealFileIn(props.session, props.path, props.line, false);
      }}
    >
      {props.children}
    </a>
  );
}

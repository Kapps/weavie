import { type Accessor, createMemo, For, type JSX } from "solid-js";

/** A `For` keyed by id, so an item replaced in a newer transcript snapshot keeps its mounted view. */
export function ForEachId<T extends { id: string }>(props: {
  children: (item: Accessor<T>) => JSX.Element;
  each: readonly T[];
}): JSX.Element {
  const byId = createMemo(() => new Map(props.each.map((item) => [item.id, item])));
  return <For each={[...byId().keys()]}>{(id) => props.children(() => byId().get(id)!)}</For>;
}

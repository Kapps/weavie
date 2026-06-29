import { createSignal } from "solid-js";

// The registered sources' routing descriptors (id + host patterns), pushed by the host on ready. The open resolver
// matches against these so a Notion link renders natively — driven by the source's one declaration in Core, never a
// hardcoded copy here. A new source needs no web change.
export interface SourceDescriptor {
  id: string;
  hosts: string[];
}

const [registry, setRegistry] = createSignal<SourceDescriptor[]>([]);

export const sourceRegistry = registry;

export function setSourceRegistry(sources: SourceDescriptor[]): void {
  setRegistry(sources);
}

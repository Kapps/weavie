export interface PathTreeEntry<T> {
  path: string;
  value: T;
}

export type PathTreeNode<T> =
  | {
      kind: "directory";
      name: string;
      key: string;
      children: PathTreeNode<T>[];
    }
  | {
      kind: "file";
      name: string;
      key: string;
      value: T;
    };

export interface PathTreeRow<T> {
  node: PathTreeNode<T>;
  depth: number;
}

/** Builds a directory-first tree while keeping each leaf's authoritative value untouched. */
export function buildPathTree<T>(entries: readonly PathTreeEntry<T>[]): PathTreeNode<T>[] {
  const root: PathTreeNode<T> = { kind: "directory", name: "", key: "", children: [] };
  const directories = new Map<string, Extract<PathTreeNode<T>, { kind: "directory" }>>([
    ["", root],
  ]);
  for (const entry of entries) {
    const segments = entry.path.split(/[\\/]/).filter((segment) => segment.length > 0);
    let parent = root;
    let prefix = "";
    for (let index = 0; index < segments.length; index++) {
      const name = segments[index] as string;
      const key = prefix === "" ? name : `${prefix}/${name}`;
      if (index === segments.length - 1) {
        parent.children.push({ kind: "file", name, key, value: entry.value });
      } else {
        let directory = directories.get(key);
        if (directory === undefined) {
          directory = { kind: "directory", name, key, children: [] };
          directories.set(key, directory);
          parent.children.push(directory);
        }
        parent = directory;
      }
      prefix = key;
    }
  }
  sortPathTree(root);
  return root.children;
}

function sortPathTree<T>(node: Extract<PathTreeNode<T>, { kind: "directory" }>): void {
  node.children.sort((left, right) => {
    if (left.kind !== right.kind) {
      return left.kind === "directory" ? -1 : 1;
    }
    return left.name.localeCompare(right.name);
  });
  for (const child of node.children) {
    if (child.kind === "directory") {
      sortPathTree(child);
    }
  }
}

/** Returns the directory keys above a path, nearest to the root first. */
export function pathAncestorKeys(path: string): string[] {
  const segments = path.split(/[\\/]/).filter((segment) => segment.length > 0);
  const keys: string[] = [];
  let prefix = "";
  for (let index = 0; index < segments.length - 1; index++) {
    prefix = prefix === "" ? (segments[index] as string) : `${prefix}/${segments[index]}`;
    keys.push(prefix);
  }
  return keys;
}

/** Flattens only the branches whose directory keys are expanded. */
export function visiblePathTreeRows<T>(
  nodes: readonly PathTreeNode<T>[],
  expanded: ReadonlySet<string>,
  limit: number,
): PathTreeRow<T>[] {
  const rows: PathTreeRow<T>[] = [];
  const walk = (children: readonly PathTreeNode<T>[], depth: number): void => {
    for (const node of children) {
      rows.push({ node, depth });
      if (rows.length >= limit) {
        return;
      }
      if (node.kind === "directory" && expanded.has(node.key)) {
        walk(node.children, depth + 1);
        if (rows.length >= limit) {
          return;
        }
      }
    }
  };
  walk(nodes, 0);
  return rows;
}

/** Returns every directory key in a tree. */
export function pathTreeDirectoryKeys<T>(nodes: readonly PathTreeNode<T>[]): string[] {
  const keys: string[] = [];
  const walk = (children: readonly PathTreeNode<T>[]): void => {
    for (const node of children) {
      if (node.kind === "directory") {
        keys.push(node.key);
        walk(node.children);
      }
    }
  };
  walk(nodes);
  return keys;
}

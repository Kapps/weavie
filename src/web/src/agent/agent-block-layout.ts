/** A block row's height depends on its kind only, so data arriving or updating never moves the transcript. */
export const blockHeights = { chart: 300, table: 320 } as const;

export function blockHeight(kind: keyof typeof blockHeights): number {
  return blockHeights[kind];
}

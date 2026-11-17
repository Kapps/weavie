/** Where an arrow, Home, or End key moves focus in a roving toolbar of `count` controls, or null to ignore it. */
export function rovingTarget(key: string, current: number, count: number): number | null {
  if (current < 0 || count === 0) return null;
  switch (key) {
    case "ArrowRight":
      return (current + 1) % count;
    case "ArrowLeft":
      return (current - 1 + count) % count;
    case "Home":
      return 0;
    case "End":
      return count - 1;
    default:
      return null;
  }
}

/** The last model line at an offset, including collapsed lines sharing a rendered position. */
export function reviewLineAtOffset(
  lineCount: number,
  topForLineNumber: (line: number) => number,
  offset: number,
): number {
  let first = 1;
  let last = lineCount;
  while (first < last) {
    const middle = Math.ceil((first + last) / 2);
    if (topForLineNumber(middle) <= offset) first = middle;
    else last = middle - 1;
  }
  return first;
}

/** Samples Monaco's mapping at shown lines and hidden-range starts without retaining its view model. */
export function createReviewLineGeometry(
  lineCount: number,
  shownLines: readonly number[],
  topForLineNumber: (line: number) => number,
) {
  const positions: { line: number; top: number }[] = [];
  const add = (line: number): void => {
    positions.push({ line, top: topForLineNumber(line) });
  };
  let previous = 0;
  for (const line of shownLines) {
    if (line > previous + 1) add(previous + 1);
    add(line);
    previous = line;
  }
  if (previous < lineCount) add(previous + 1);
  const top = (line: number): number => {
    let first = 0;
    let last = positions.length - 1;
    while (first < last) {
      const middle = Math.ceil((first + last) / 2);
      if (positions[middle]!.line <= line) first = middle;
      else last = middle - 1;
    }
    return positions[first]!.top;
  };
  return {
    topForLineNumber: top,
    lineAtOffset: (offset: number) => reviewLineAtOffset(lineCount, top, offset),
  };
}

export type ReviewLineGeometry = ReturnType<typeof createReviewLineGeometry>;

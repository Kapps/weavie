/** Fractional device scaling can make CSS borders smaller than integer clientTop. */
export function measureReviewHeader(article: HTMLElement, header: HTMLElement) {
  const style = getComputedStyle(article);
  const borderTop = parseFloat(style.borderTopWidth);
  return {
    borderTop,
    limit:
      article.getBoundingClientRect().height -
      borderTop -
      parseFloat(style.borderBottomWidth) -
      header.getBoundingClientRect().height,
  };
}

/** Header placement shares the outer review scroll coordinates. */
export function createReviewHeaderPosition(header: HTMLElement) {
  let previousOffset: number | undefined;
  return (scrollTop: number, sectionTop: number, borderTop: number, limit: number): void => {
    const offset = Math.max(0, Math.min(scrollTop - sectionTop - borderTop, limit));
    if (offset === previousOffset) return;
    previousOffset = offset;
    header.style.top = `${offset}px`;
  };
}

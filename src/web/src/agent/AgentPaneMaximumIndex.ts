export class MaximumIndex {
  private readonly heap: number[] = [];
  private readonly positions = new Map<number, number>();

  add(index: number): void {
    if (this.positions.has(index)) {
      return;
    }
    this.heap.push(index);
    const position = this.heap.length - 1;
    this.positions.set(index, position);
    this.bubbleUp(position);
  }

  delete(index: number): void {
    const position = this.positions.get(index);
    if (position === undefined) {
      return;
    }
    const tail = this.heap.pop()!;
    this.positions.delete(index);
    if (position === this.heap.length) {
      return;
    }
    this.heap[position] = tail;
    this.positions.set(tail, position);
    const parent = Math.floor((position - 1) / 2);
    if (position > 0 && this.heap[parent]! < tail) {
      this.bubbleUp(position);
    } else {
      this.bubbleDown(position);
    }
  }

  maximum(): number | null {
    return this.heap[0] ?? null;
  }

  private bubbleUp(start: number): void {
    let child = start;
    while (child > 0) {
      const parent = Math.floor((child - 1) / 2);
      if (this.heap[parent]! >= this.heap[child]!) {
        return;
      }
      this.swap(parent, child);
      child = parent;
    }
  }

  private bubbleDown(start: number): void {
    let parent = start;
    while (true) {
      const left = parent * 2 + 1;
      if (left >= this.heap.length) {
        return;
      }
      const right = left + 1;
      const child = right < this.heap.length && this.heap[right]! > this.heap[left]! ? right : left;
      if (this.heap[parent]! >= this.heap[child]!) {
        return;
      }
      this.swap(parent, child);
      parent = child;
    }
  }

  private swap(left: number, right: number): void {
    const value = this.heap[left]!;
    this.heap[left] = this.heap[right]!;
    this.heap[right] = value;
    this.positions.set(this.heap[left]!, left);
    this.positions.set(this.heap[right]!, right);
  }
}

export type SelectionCommit<T> = (value: T) => boolean;

export class SelectionSequencer<T> {
  private sequence = 0;
  private barrier = 0;

  public constructor(private readonly apply: (value: T) => boolean) {}

  public beginIntent(): SelectionCommit<T> {
    const sequence = ++this.sequence;
    this.barrier = sequence;
    return this.commitAt(sequence, false);
  }

  public beginCandidate(): SelectionCommit<T> {
    const sequence = ++this.sequence;
    return this.commitAt(sequence, true);
  }

  private commitAt(sequence: number, candidate: boolean): SelectionCommit<T> {
    let pending = true;
    return (value) => {
      if (!pending) {
        return false;
      }
      pending = false;
      if (candidate) {
        if (sequence < this.barrier) {
          return false;
        }
        this.barrier = sequence;
      } else if (sequence !== this.barrier) {
        return false;
      }
      return this.apply(value);
    };
  }
}

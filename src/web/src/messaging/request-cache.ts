/**
 * Requests by key, each asked once and shared while in flight. One that fails, or whose value `keep` refuses, is
 * asked again the next time.
 */
export function createRequestCache<V>(keep: (value: V) => boolean) {
  const requests = new Map<string, Promise<V>>();
  const forget = (key: string, request: Promise<V>): void => {
    if (requests.get(key) === request) requests.delete(key);
  };
  return {
    get(key: string, fetch: () => Promise<V>): Promise<V> {
      const cached = requests.get(key);
      if (cached !== undefined) return cached;
      const request: Promise<V> = fetch().then(
        (value) => {
          if (!keep(value)) forget(key, request);
          return value;
        },
        (error: unknown) => {
          forget(key, request);
          throw error;
        },
      );
      requests.set(key, request);
      return request;
    },
    has: (key: string): boolean => requests.has(key),
    /** Drops every request whose key is not in `keys`. */
    retain(keys: ReadonlySet<unknown>): void {
      for (const key of requests.keys()) if (!keys.has(key)) requests.delete(key);
    },
  };
}

export type RequestCache<V> = ReturnType<typeof createRequestCache<V>>;

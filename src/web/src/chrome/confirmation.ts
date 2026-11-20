import { createSignal } from "solid-js";

interface Confirmation {
  title: string;
  body: string;
  confirmLabel: string;
  destructive?: boolean;
}

type PendingConfirmation = Confirmation & { resolve: (ok: boolean) => void };
const pending: PendingConfirmation[] = [];
const [request, setRequest] = createSignal<PendingConfirmation | null>(null);
export const confirmationRequest = request;

export function settleConfirmation(ok: boolean): void {
  const current = request();
  setRequest(pending.shift() ?? null);
  current?.resolve(ok);
}

export function confirm(options: Confirmation): Promise<boolean> {
  return new Promise((resolve) => {
    const next = { ...options, resolve };
    if (request() === null) setRequest(next);
    else pending.push(next);
  });
}

import { createSignal } from "solid-js";

interface Confirmation {
  title: string;
  body: string;
  confirmLabel: string;
  destructive?: boolean;
}

const [request, setRequest] = createSignal<
  (Confirmation & { resolve: (ok: boolean) => void }) | null
>(null);
export const confirmationRequest = request;

export function settleConfirmation(ok: boolean): void {
  const current = request();
  setRequest(null);
  current?.resolve(ok);
}

export function confirm(options: Confirmation): Promise<boolean> {
  settleConfirmation(false);
  return new Promise((resolve) => setRequest({ ...options, resolve }));
}

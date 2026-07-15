// Bridges VSCode's INotificationService to Weavie's toast channel, registered as a service override in
// vscode-services.ts. Without it, Monaco's failure reports (a failed rename's info/error) only reach the
// browser console, so a failed refactor is invisible — the user hits Enter and nothing happens.

import {
  type INotification,
  type INotificationHandle,
  INotificationService,
  type IPromptChoice,
  type IPromptChoiceWithMenu,
  type IPromptOptions,
  type IStatusMessageOptions,
  NoOpNotification,
  type NotificationMessage,
  NotificationsFilter,
  Severity,
  SyncDescriptor,
} from "@codingame/monaco-vscode-api/services";
import { Event } from "@codingame/monaco-vscode-api/vscode/vs/base/common/event";
import { notify as toast } from "../notify/notify";
import type { Toast } from "../notify/Toasts";

function toLevel(severity: Severity): Toast["level"] {
  if (severity === Severity.Error) {
    return "error";
  }
  if (severity === Severity.Warning) {
    return "warn";
  }
  return "info";
}

function textOf(message: NotificationMessage | NotificationMessage[]): string {
  const one = (m: NotificationMessage): string => (m instanceof Error ? m.message : m);
  return Array.isArray(message) ? message.map(one).join("\n") : one(message);
}

// Weavie has no notification center, so progress and prompt actions aren't rendered — the message still
// surfaces, which is what a failed editor action needs. info/warn/error are implemented directly, not via notify.
export class WeavieNotificationService implements INotificationService {
  readonly _serviceBrand: undefined;
  readonly onDidChangeFilter = Event.None;

  notify(notification: INotification): INotificationHandle {
    toast(toLevel(notification.severity), textOf(notification.message));
    return new NoOpNotification();
  }

  info(message: NotificationMessage | NotificationMessage[]): void {
    toast("info", textOf(message));
  }

  warn(message: NotificationMessage | NotificationMessage[]): void {
    toast("warn", textOf(message));
  }

  error(message: NotificationMessage | NotificationMessage[]): void {
    toast("error", textOf(message));
  }

  prompt(
    severity: Severity,
    message: string,
    _choices: (IPromptChoice | IPromptChoiceWithMenu)[],
    _options?: IPromptOptions,
  ): INotificationHandle {
    toast(toLevel(severity), message);
    return new NoOpNotification();
  }

  status(_message: NotificationMessage, _options?: IStatusMessageOptions): { close(): void } {
    return { close: () => undefined };
  }

  setFilter(): void {}

  getFilter(): NotificationsFilter {
    return NotificationsFilter.OFF;
  }

  getFilters(): [] {
    return [];
  }

  removeFilter(): void {}
}

/** The override that makes Monaco's INotificationService raise Weavie toasts. Spread into `initialize()`. */
export function getNotificationServiceOverride(): Record<string, SyncDescriptor<unknown>> {
  return { [INotificationService.toString()]: new SyncDescriptor(WeavieNotificationService) };
}

import { IClipboardService, SyncDescriptor } from "@codingame/monaco-vscode-api/services";
import { BrowserClipboardService } from "@codingame/monaco-vscode-api/vscode/vs/platform/clipboard/browser/clipboardService";

class TrimmedClipboardService extends BrowserClipboardService {
  override writeText(
    ...[text, type]: Parameters<BrowserClipboardService["writeText"]>
  ): Promise<void> {
    return super.writeText(type ? text : text.trim(), type);
  }
}

export function getClipboardServiceOverride(): Record<string, SyncDescriptor<unknown>> {
  return { [IClipboardService.toString()]: new SyncDescriptor(TrimmedClipboardService) };
}

import { describe, expect, it } from "vitest";
import { canPreview, previewKindOf } from "./preview-registry";

describe("preview registry", () => {
  it("maps supported extensions to their renderer", () => {
    expect(previewKindOf("/workspace/README.md")).toBe("markdown");
    expect(previewKindOf("/workspace/guide.markdown")).toBe("markdown");
    expect(previewKindOf("C:\\workspace\\logo.SVG")).toBe("svg");
  });

  it("rejects unsupported files", () => {
    expect(previewKindOf("/workspace/app.ts")).toBeNull();
    expect(canPreview("/workspace/image.svgz")).toBe(false);
    expect(canPreview("/workspace/file.constructor")).toBe(false);
  });
});

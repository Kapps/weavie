import { describe, expect, it } from "vitest";
import { mediaTypeOf } from "./media-types";

describe("mediaTypeOf", () => {
  it("matches image and video extensions case-insensitively", () => {
    expect(mediaTypeOf("/ws/shot.png")).toEqual({ kind: "image", mime: "image/png" });
    expect(mediaTypeOf("C:\\ws\\Photo.JPG")).toEqual({ kind: "image", mime: "image/jpeg" });
    expect(mediaTypeOf("/ws/demo.webm")).toEqual({ kind: "video", mime: "video/webm" });
    expect(mediaTypeOf("/ws/clip.MP4")).toEqual({ kind: "video", mime: "video/mp4" });
  });

  it("returns null for everything Monaco should open as text", () => {
    expect(mediaTypeOf("/ws/a.cs")).toBeNull();
    expect(mediaTypeOf("/ws/README.md")).toBeNull();
    expect(mediaTypeOf("/ws/icon.svg")).toBeNull(); // svg is editable text — Preview territory, not media
    expect(mediaTypeOf("/ws/noext")).toBeNull();
    expect(mediaTypeOf("/ws/dir.png/file.txt")).toBeNull(); // extension comes from the file name, not a dir
  });
});

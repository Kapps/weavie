import { describe, expect, test } from "vitest";
import { isGithubMediaUrl } from "./github-media";

const asset = "user-attachments/assets/f6db4567-e7b9-408c-a0a1-9a3932911750";

describe("GitHub attachment URL policy", () => {
  test.each([
    `https://github.com/${asset}`,
    `https://github.com/${asset}?download=1#preview`,
    "https://github.com/Kapps/weavie/assets/1259333/f6db4567-e7b9-408c-a0a1-9a3932911750",
    "https://user-images.githubusercontent.com/1259333/screenshot.png",
    "https://user-images.githubusercontent.com/1259333/clip.mp4",
  ])("allows attachment URL %s", (href) => {
    expect(isGithubMediaUrl(href)).toBe(true);
  });

  test.each([
    `http://github.com/${asset}`,
    `https://github.com.evil.test/${asset}`,
    `https://github.com@evil.test/${asset}`,
    `https://user:password@github.com/${asset}`,
    `https://github.com:8443/${asset}`,
    `https://evil.test/${asset}`,
    `https://github.com/${asset}/extra`,
    "https://github.com/user-attachments/assets/not-an-id",
    "https://github.com/Kapps/weavie/pull/1",
    "https://github.com/Kapps/weavie/blob/main/video.mp4",
    "https://user-images.githubusercontent.com.evil.test/1/image.png",
    "https://raw.githubusercontent.com/Kapps/weavie/main/image.png",
    `//github.com/${asset}`,
    `/${asset}`,
    "data:image/png;base64,aGVsbG8=",
    "javascript:alert(1)",
  ])("does not fetch non-attachment URL %s", (href) => {
    expect(isGithubMediaUrl(href)).toBe(false);
  });
});

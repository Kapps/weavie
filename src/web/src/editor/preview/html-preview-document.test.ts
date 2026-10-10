import { describe, expect, it } from "vitest";
import { withBase } from "./html-preview-document";

describe("withBase", () => {
  it("keeps the doctype first", () => {
    expect(withBase("<!DOCTYPE html><p>x</p>", "http://h/b/")).toBe(
      '<!DOCTYPE html><base href="http://h/b/"><p>x</p>',
    );
    expect(withBase("\n  <!doctype html >\n<p>", "/b/")).toBe(
      '\n  <!doctype html ><base href="/b/">\n<p>',
    );
  });

  it("prepends when there is no doctype", () => {
    expect(withBase("<p>x</p>", "/b/")).toBe('<base href="/b/"><p>x</p>');
  });

  it("escapes the base attribute", () => {
    expect(withBase("", '/a"&b/')).toBe('<base href="/a&quot;&amp;b/">');
  });
});

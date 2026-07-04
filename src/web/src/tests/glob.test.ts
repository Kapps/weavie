import { describe, expect, it } from "vitest";
import { globMatches } from "./glob";

describe("globMatches", () => {
  it.each([
    ["**/*.test.ts?(x)", "a.test.ts", true], // **/ matches zero leading directories
    ["**/*.test.ts?(x)", "src/a.test.ts", true],
    ["**/*.test.ts?(x)", "src/deep/nested/a.test.tsx", true],
    ["**/*.test.ts?(x)", "src/a.test.tsz", false],
    ["**/*.test.ts?(x)", "src/a.ts", false],
    ["**/*_test.go", "pkg/x_test.go", true],
    ["**/*_test.go", "pkg/x.go", false],
    ["**/*Tests.cs", "src/FooTests.cs", true],
    ["**/*.{spec,test}.js", "a.spec.js", true],
    ["**/*.{spec,test}.js", "a.test.js", true],
    ["**/*.{spec,test}.js", "a.unit.js", false],
    ["*.ts", "a.ts", true],
    ["*.ts", "sub/a.ts", false], // single star does not cross a separator
  ])("%s matches %s => %s", (glob, path, expected) => {
    expect(globMatches(glob, path)).toBe(expected);
  });

  it("normalizes backslash paths", () => {
    expect(globMatches("**/*.test.ts", "src\\win\\a.test.ts")).toBe(true);
  });
});

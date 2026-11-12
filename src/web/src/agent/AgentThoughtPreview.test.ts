import { expect, it } from "vitest";
import { currentThoughtPreview } from "./AgentThoughtPreview";

it.each([
  ["## Inspecting files\nDetails.\n\n### Running `tests`\nMore details.", "Running tests"],
  ["**Inspecting files**\n\nDetails.\n\n**Running tests**\n\nMore details.", "Running tests"],
  ["Earlier summary\n\nLatest summary\n\n", "Latest summary"],
  ["Some **emphasis** in a paragraph.\n\nLatest summary", "Latest summary"],
  ["Checking [the build](https://example.com) and `tests`", "Checking the build and tests"],
  ["**Checking the\nscroll tests**\n\nMore detail", "Checking the scroll tests"],
  ["**First** and **second**\n\nLatest summary", "Latest summary"],
  ["\n\n", ""],
])("uses the latest readable reasoning summary from %j", (text, expected) => {
  expect(currentThoughtPreview(text)).toBe(expected);
});

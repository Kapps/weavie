import MarkdownIt from "markdown-it";
import { describe, expect, it } from "vitest";
import { isSafeAgentLink, taskLists } from "./markdown-renderer";

describe("agent Markdown link policy", () => {
  it("allows web and workspace targets", () => {
    expect(isSafeAgentLink("https://example.com/path")).toBe(true);
    expect(isSafeAgentLink("src/app.ts:12")).toBe(true);
    expect(isSafeAgentLink("C:\\repo\\app.cs:7")).toBe(true);
  });

  it("rejects executable, embedded-data, and direct file schemes", () => {
    expect(isSafeAgentLink("javascript:alert(1)")).toBe(false);
    expect(isSafeAgentLink("data:text/html,hello")).toBe(false);
    expect(isSafeAgentLink("file:///etc/passwd")).toBe(false);
    expect(isSafeAgentLink("vscode://file/repo/app.ts")).toBe(false);
  });
});

describe("task lists", () => {
  const render = (content: string): string => new MarkdownIt().use(taskLists).render(content);

  it("renders GFM markers as disabled checkboxes on the item and marks the list", () => {
    expect(render("- [ ] open\n- [x] done\n- plain")).toBe(
      '<ul class="contains-task-list">\n' +
        '<li class="task-list-item"><input type="checkbox" class="task-list-item-checkbox" disabled> open</li>\n' +
        '<li class="task-list-item"><input type="checkbox" class="task-list-item-checkbox" disabled checked> done</li>\n' +
        "<li>plain</li>\n</ul>\n",
    );
  });

  it("marks the enclosing list of a nested or ordered item", () => {
    expect(render("- parent\n  1. [X] child")).toContain(
      '<ol class="contains-task-list">\n<li class="task-list-item"><input',
    );
  });

  it("leaves brackets that are not a leading marker alone", () => {
    expect(render("- [link]\n- a [ ] b\n\n[ ] not a list")).not.toContain("<input");
  });
});

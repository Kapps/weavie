import { describe, expect, test } from "vitest";
import { parsePipeTable, parseTagTable, renderTable } from "./notion-table";

const TAG_TABLE = [
  '<table fit-page-width="false" header-row="true" header-column="true">',
  "\t<colgroup>",
  '\t\t<col color="blue_bg"/>',
  "\t\t<col/>",
  "\t</colgroup>",
  '\t<tr color="gray_bg">',
  "\t\t<td>Name</td>",
  "\t\t<td>Value</td>",
  "\t</tr>",
  "\t<tr>",
  '\t\t<td color="red">Alpha</td>',
  "\t\t<td>**1**</td>",
  "\t</tr>",
  "</table>",
];

describe("parseTagTable", () => {
  test("reads header flags, colgroup colors, and row/cell colors", () => {
    const table = parseTagTable(TAG_TABLE, 0);
    expect(table).toMatchObject({
      headerRow: true,
      headerColumn: true,
      colColors: ["blue_bg", null],
      rows: [
        { color: "gray_bg", cells: [{ text: "Name" }, { text: "Value" }] },
        {
          color: null,
          cells: [
            { color: "red", text: "Alpha" },
            { color: null, text: "**1**" },
          ],
        },
      ],
    });
  });
});

describe("parsePipeTable", () => {
  test("a |---| second line marks the header row and is dropped", () => {
    const table = parsePipeTable(["| A | B |", "|---|---|", "| 1 | 2 |"], 0);
    expect(table).toMatchObject({
      headerRow: true,
      rows: [{ cells: [{ text: "A" }, { text: "B" }] }, { cells: [{ text: "1" }, { text: "2" }] }],
    });
  });
  test("escaped pipes stay inside their cell", () => {
    const table = parsePipeTable(["| a \\| b | c |"], 0);
    expect(table.rows[0]?.cells.map((c) => c.text)).toEqual(["a \\| b", "c"]);
  });
});

describe("renderTable", () => {
  test("renders headers, color classes (cell over column), and inline cell content", () => {
    const html = renderTable(parseTagTable(TAG_TABLE, 0));
    expect(html).toBe(
      '<table><tr class="wv-bg-gray"><th class="wv-bg-blue">Name</th><th>Value</th></tr>' +
        '<tr><th class="wv-color-red">Alpha</th><td><strong>1</strong></td></tr></table>',
    );
  });
});

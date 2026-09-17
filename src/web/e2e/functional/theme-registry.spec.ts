import type { MessageEnvelope } from "../../src/messaging/message-envelope";
import type { ExtensionChoice, ThemeSearchOrder } from "../../src/theme/picker-state";
import { runCommand } from "../harness/actions";
import { expect, test } from "../harness/fixtures";

type Search = { query: string; offset: number; sortBy: ThemeSearchOrder };
let requests: Search[];
const themes: ExtensionChoice[] = [
  {
    namespace: "example-author",
    name: "popular",
    displayName: "Popular Theme",
    version: "1.2.3",
    description: "A theme with community feedback.",
    downloadCount: 12345,
    averageRating: 4.75,
    reviewCount: 12,
  },
  {
    namespace: "new-author",
    name: "new",
    displayName: "New Theme",
    version: "1.0.0",
    description: "A theme without community feedback.",
    downloadCount: 0,
  },
];

test.use({
  preNavigate: {
    run: async (page) => {
      requests = [];
      await page.routeWebSocket("**/*", (socket) => {
        const server = socket.connectToServer();
        socket.onMessage((data) => {
          const message = JSON.parse(data.toString()) as MessageEnvelope;
          if (
            message.kind !== "request" ||
            message.feature !== "themes" ||
            message.name !== "search"
          ) {
            server.send(data);
            return;
          }
          const request = message.payload as Search;
          requests.push(request);
          const first = request.sortBy === "downloadCount" ? 0 : 1;
          socket.send(
            JSON.stringify({
              ...message,
              kind: "response",
              payload: {
                extensions: [themes[(first + request.offset) % themes.length]],
                offset: request.offset,
                totalSize: themes.length,
              },
            }),
          );
        });
      });
    },
  },
});

test("registry shows publisher and community metadata and resets pages when sorting", async ({
  page,
}) => {
  await runCommand(page, "Select Color Theme…");
  const picker = page.getByRole("dialog", { name: "Select Color Theme" });
  await picker.getByRole("button", { name: "Open VSX", exact: true }).click();
  const sort = picker.getByRole("combobox", { name: "Sort Open VSX themes" });
  await expect(sort).toHaveValue("downloadCount");
  const rows = picker.getByRole("listbox").getByRole("option");
  await expect(rows).toHaveCount(1);
  await expect(rows.first()).toContainText("Publisher: example-author");
  await expect(rows.first()).toContainText("12,345 downloads");
  await expect(rows.first()).toContainText("4.8 / 5");
  await expect(rows.first().locator("svg.theme-rating-star")).toBeVisible();
  await expect(rows.first()).toContainText("12 reviews");
  await picker.getByRole("button", { name: "Load more (1 of 2)" }).click();
  await expect(rows).toHaveCount(2);
  await expect(rows.nth(1)).toContainText("0 downloads");
  await expect(rows.nth(1)).toContainText("Rating unavailable");
  await expect(rows.nth(1)).toContainText("Review count unavailable");
  await expect(picker.getByRole("button", { name: /Load more/ })).toHaveCount(0);
  await sort.selectOption("relevance");
  await expect(rows).toHaveCount(1);
  await expect(rows.first()).toContainText("New Theme");
  await picker.getByRole("button", { name: "Load more (1 of 2)" }).click();
  await expect(rows).toHaveCount(2);
  await expect(rows.nth(1)).toContainText("Popular Theme");
  await picker.getByRole("combobox", { name: "Search Open VSX themes" }).fill("ocean");
  await expect(rows).toHaveCount(1);
  expect(requests).toEqual([
    { query: "", offset: 0, sortBy: "downloadCount" },
    { query: "", offset: 1, sortBy: "downloadCount" },
    { query: "", offset: 0, sortBy: "relevance" },
    { query: "", offset: 1, sortBy: "relevance" },
    { query: "ocean", offset: 0, sortBy: "relevance" },
  ]);
});

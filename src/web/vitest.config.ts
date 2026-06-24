import { defineConfig } from "vitest/config";

// Standalone (not the app's vite.config) so unit tests don't load the Solid/Monaco build plugins. Node
// environment: the units under test are pure logic, no DOM.
export default defineConfig({
  test: {
    include: ["src/**/*.test.ts"],
    environment: "node",
  },
});

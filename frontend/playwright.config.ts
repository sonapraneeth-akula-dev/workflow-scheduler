import { defineConfig, devices } from "@playwright/test"

const catalogueOrigin = process.env.WF_CATALOG_ORIGIN
const timelineOrigin = process.env.WF_TIMELINE_ORIGIN
const fixtureOrigin =
  catalogueOrigin ?? process.env.WF_GRAPH_ORIGIN ?? timelineOrigin

export default defineConfig({
  testDir: "./e2e",
  testIgnore: [
    ...(!fixtureOrigin
      ? ["**/catalog-isolation.spec.ts", "**/run-graph*.spec.ts"]
      : []),
    ...(!timelineOrigin ? ["**/run-timeline.spec.ts"] : []),
  ],
  fullyParallel: false,
  workers: 1,
  timeout: 30_000,
  expect: { timeout: 10_000 },
  globalTeardown: fixtureOrigin ? undefined : "./scripts/stop-test-stack.ts",
  use: {
    baseURL: fixtureOrigin ?? "http://127.0.0.1:4173",
    trace: "retain-on-failure",
  },
  webServer: fixtureOrigin
    ? undefined
    : {
        command: "bun scripts/test-stack.ts",
        url: "http://127.0.0.1:4173",
        reuseExistingServer: false,
        timeout: 120_000,
        gracefulShutdown: { signal: "SIGTERM", timeout: 15_000 },
      },
  projects: [
    { name: "chromium", use: { ...devices["Desktop Chrome"] } },
    {
      name: "firefox",
      use: { ...devices["Desktop Firefox"] },
      grepInvert: process.env.WF_GRAPH_ORIGIN
        ? /200-node pinned DAG retains cached layout/
        : undefined,
    },
    {
      name: "webkit",
      use: { ...devices["Desktop Safari"] },
      grepInvert: process.env.WF_GRAPH_ORIGIN
        ? /200-node pinned DAG retains cached layout/
        : undefined,
    },
  ],
})

import { defineConfig, devices } from "@playwright/test"

const catalogueOrigin = process.env.WF_CATALOG_ORIGIN

export default defineConfig({
  testDir: "./e2e",
  testIgnore: catalogueOrigin ? [] : ["**/catalog-isolation.spec.ts"],
  fullyParallel: false,
  workers: 1,
  timeout: 30_000,
  expect: { timeout: 10_000 },
  globalTeardown: catalogueOrigin ? undefined : "./scripts/stop-test-stack.ts",
  use: {
    baseURL: catalogueOrigin ?? "http://127.0.0.1:4173",
    trace: "retain-on-failure",
  },
  webServer: catalogueOrigin
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
    { name: "firefox", use: { ...devices["Desktop Firefox"] } },
    { name: "webkit", use: { ...devices["Desktop Safari"] } },
  ],
})

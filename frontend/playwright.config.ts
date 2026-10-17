import { defineConfig, devices } from "@playwright/test"

const catalogueOrigin = process.env.WF_CATALOG_ORIGIN
const timelineOrigin = process.env.WF_TIMELINE_ORIGIN
const reportOrigin = process.env.WF_REPORT_ORIGIN
const activationOrigin = process.env.WF_ACTIVATION_ORIGIN
const rerunOrigin = process.env.WF_RERUN_ORIGIN
const rerunExecutionOrigin = process.env.WF_RERUN_EXECUTION_ORIGIN
const auditOrigin = process.env.WF_AUDIT_ORIGIN
const machineOrigin = process.env.WF_MACHINE_ORIGIN
const trendsOrigin = process.env.WF_TRENDS_ORIGIN
const dashboardOrigin = process.env.WF_DASHBOARD_ORIGIN
const fixtureOrigin =
  dashboardOrigin ??
  trendsOrigin ??
  machineOrigin ??
  auditOrigin ??
  rerunExecutionOrigin ??
  rerunOrigin ??
  activationOrigin ??
  catalogueOrigin ??
  process.env.WF_GRAPH_ORIGIN ??
  timelineOrigin ??
  reportOrigin

export default defineConfig({
  testDir: "./e2e",
  testIgnore: [
    ...(!dashboardOrigin ? ["**/dashboards.spec.ts"] : []),
    ...(!trendsOrigin ? ["**/series-trends.spec.ts"] : []),
    ...(!machineOrigin ? ["**/machines.spec.ts"] : []),
    ...(!auditOrigin ? ["**/audit.spec.ts"] : []),
    ...(!rerunExecutionOrigin ? ["**/rerun-execution.spec.ts"] : []),
    ...(!rerunOrigin ? ["**/rerun.spec.ts"] : []),
    ...(!activationOrigin ? ["**/version-activation.spec.ts"] : []),
    ...(!fixtureOrigin
      ? ["**/catalog-isolation.spec.ts", "**/run-graph*.spec.ts"]
      : []),
    ...(!timelineOrigin ? ["**/run-timeline.spec.ts"] : []),
    ...(fixtureOrigin && !reportOrigin ? ["**/report-*.spec.ts"] : []),
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

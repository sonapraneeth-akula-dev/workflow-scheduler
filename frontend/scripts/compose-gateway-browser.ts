import assert from "node:assert/strict"
import { chromium, firefox, webkit, expect } from "@playwright/test"
import { z } from "zod"

const run = z.uuid().parse(process.argv[2])
const report = z.uuid().parse(process.argv[3])
for (const engine of [chromium, firefox, webkit]) {
  const browser = await engine.launch()
  try {
    const page = await browser.newPage({ baseURL: "http://127.2.6.7" })
    const errors: string[] = []
    let handshake = false
    let binaryFrames = 0
    page.on("pageerror", (error) => errors.push(error.message))
    page.on("websocket", (socket) => {
      if (new URL(socket.url()).pathname !== "/hubs/live/v1") return
      socket.on("framesent", (frame) => {
        if (
          typeof frame.payload === "string" &&
          frame.payload.includes('"protocol":"messagepack"')
        ) {
          handshake = true
        }
      })
      socket.on("framereceived", (frame) => {
        if (Buffer.isBuffer(frame.payload)) binaryFrames++
      })
    })
    await page.goto(`/runs/${run}`)
    await expect(
      page.getByText("Local developer", { exact: true })
    ).toBeVisible()
    await expect(
      page.getByRole("button", { name: "Sign in", exact: true })
    ).toHaveCount(0)
    await expect(
      page.getByRole("button", { name: "Sign out", exact: true })
    ).toHaveCount(0)
    await expect(
      page.getByText("Succeeded", { exact: true }).first()
    ).toBeVisible()
    await expect.poll(() => handshake).toBe(true)
    await expect.poll(() => binaryFrames).toBeGreaterThan(0)
    await page.reload()
    await expect(
      page.getByText("Local developer", { exact: true })
    ).toBeVisible()
    await page.goto(`/runs/${run}/reports/${report}`)
    await expect(
      page.getByRole("heading", { name: "Gateway report", exact: true })
    ).toBeVisible()
    await expect(
      page
        .frameLocator("iframe")
        .getByText("Gateway iframe ready", { exact: true })
    ).toBeVisible()
    assert.deepEqual(
      errors,
      [],
      "The actual built Compose SPA raised a browser exception."
    )
    console.log(
      `${engine.name()}: built SPA deep links/reload, login-free API, MessagePack WebSocket and sandboxed HTML report passed.`
    )
  } finally {
    await browser.close()
  }
}

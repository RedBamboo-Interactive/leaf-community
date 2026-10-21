import path from "node:path"
import { chromium } from "../../redleaf/web/node_modules/@playwright/test/index.mjs"

const scratch = process.env.REDLEAF_SCRATCH_DIR
if (!scratch) throw new Error("REDLEAF_SCRATCH_DIR is required")
const browser = await chromium.launch({ headless: true })
try {
  const page = await browser.newPage({ viewport: { width: 390, height: 844 }, deviceScaleFactor: 1 })
  const errors = []
  page.on("pageerror", (error) => errors.push(error.message))
  const response = await page.goto("http://127.0.0.1:4179/settings-preview.html", { waitUntil: "networkidle" })
  if (!response?.ok()) throw new Error(`Preview returned HTTP ${response?.status() ?? "unknown"}`)
  await page.waitForTimeout(500)
  if (errors.length > 0) throw new Error(`Preview page error: ${errors.join("; ")}`)
  await page.getByText("Your Nova’s public side on Discord.", { exact: false }).waitFor({ timeout: 5_000 })
  await page.screenshot({ path: path.join(scratch, "public-nova-settings-390.png"), fullPage: true })
} finally {
  await browser.close()
}

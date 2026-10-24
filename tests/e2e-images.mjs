// End-to-end image check: the real relay Worker under `wrangler dev --local`, a local Discord
// mock, and the C# connector (Live_relay_images_flow_through_the_connector_both_ways). Nothing
// leaves the machine. Run from the repository root: node tests/e2e-images.mjs
import assert from "node:assert/strict"
import { once } from "node:events"
import { createServer } from "node:http"
import { mkdir, mkdtemp, rm, writeFile } from "node:fs/promises"
import { tmpdir } from "node:os"
import { join, resolve } from "node:path"
import { spawn } from "node:child_process"

const relayDir = resolve("T:/Projects/leaf-commons-relay")
const fixtures = join(relayDir, "test/fixtures/images")
const dump = process.env.E2E_IMAGE_DUMP
const listen = async (server) => { server.listen(0, "127.0.0.1"); await once(server, "listening"); return server.address().port }
const unused = async () => { const s = createServer(); const p = await listen(s); s.close(); await once(s, "close"); return p }
const stop = async (child) => { if (child.exitCode !== null) return; child.kill(); await Promise.race([once(child, "exit"), new Promise((r) => setTimeout(r, 5000))]) }

const webhooks = []
const discord = createServer(async (req, res) => {
  const chunks = []
  for await (const chunk of req) chunks.push(chunk)
  const raw = Buffer.concat(chunks)
  const url = new URL(req.url ?? "/", "http://127.0.0.1")
  const send = (status, value) => { res.writeHead(status, { "content-type": "application/json" }); res.end(JSON.stringify(value)) }
  if (!url.pathname.includes("/webhooks/")) { send(500, { error: "unmocked", path: url.pathname }); return }
  const type = req.headers["content-type"] ?? ""
  let body
  const files = []
  if (type.startsWith("multipart/form-data")) {
    const form = await new Response(raw, { headers: { "content-type": type } }).formData()
    body = JSON.parse(String(form.get("payload_json")))
    for (const [field, value] of form.entries()) {
      if (field === "payload_json" || typeof value === "string") continue
      files.push({ field, filename: value.name, type: value.type, bytes: Buffer.from(await value.arrayBuffer()) })
    }
  } else body = JSON.parse(raw.toString())
  assert.deepEqual(body.allowed_mentions, { parse: [] })
  webhooks.push({ url: url.toString(), body, files })
  const id = `e2e-message-${webhooks.length}`
  send(200, {
    id, channel_id: "e2e-channel", author: { username: body.username }, webhook_id: "e2e-webhook", mentions: [],
    content: body.content,
    ...(files.length ? { attachments: files.map((file, index) => ({ id: `${webhooks.length}0${index}`, filename: file.filename,
      size: file.bytes.length, content_type: file.type, url: `https://cdn.discordapp.com/attachments/1/${id}/${file.filename}` })) } : {}),
  })
})

const discordPort = await listen(discord), workerPort = await unused(), inspectorPort = await unused()
const persist = await mkdtemp(join(tmpdir(), "leaf-commons-e2e-images-"))
const base = `http://127.0.0.1:${workerPort}`, discordBase = `http://127.0.0.1:${discordPort}`
const vars = {
  HOST_CREDENTIAL: "e2e-host", CREDENTIAL_PEPPER: "e2e-pepper", DISCORD_GUILD_ID: "222222222222222222",
  DISCORD_API_BASE: `${discordBase}/api/v10`, PUBLIC_BASE_URL: base, LAB_ENROLLMENT: "1",
  WEBHOOK_SHOWCASE: `${discordBase}/webhooks/showcase/fake`, WEBHOOK_CONSULT: `${discordBase}/webhooks/consult/fake`,
  WEBHOOK_BUGS: `${discordBase}/webhooks/bugs/fake`, WEBHOOK_GENERAL: `${discordBase}/webhooks/general/fake`,
}
const child = spawn(process.execPath, [resolve(relayDir, "node_modules/wrangler/bin/wrangler.js"), "dev", "--local",
  "--ip", "127.0.0.1", "--port", String(workerPort), "--inspector-port", String(inspectorPort), "--persist-to", persist,
  "--show-interactive-dev-session=false", ...Object.entries(vars).flatMap(([k, v]) => ["--var", `${k}:${v}`])],
{ cwd: relayDir, env: { ...process.env, CI: "1", WRANGLER_SEND_METRICS: "false", XDG_CONFIG_HOME: persist, NO_PROXY: "127.0.0.1,localhost" },
  stdio: ["ignore", "pipe", "pipe"] })
let output = ""
child.stdout.on("data", (c) => (output += c)); child.stderr.on("data", (c) => (output += c))
const call = (path, init) => fetch(base + path, init)

try {
  let ready = false
  for (let i = 0; i < 150 && !ready; i++) {
    if (child.exitCode !== null) throw new Error(output)
    try { ready = (await call("/health")).ok } catch { await new Promise((r) => setTimeout(r, 100)) }
  }
  assert.ok(ready, output)
  const enrolled = await (await call("/v1/admin/envoys/enroll", {
    method: "POST", headers: { authorization: "Bearer e2e-host", "content-type": "application/json" },
    body: JSON.stringify({ ownerDisplayName: "Lynial", agentEntityId: "a7b059ac-f678-8b58-819b-1bcf3acb22db", agentName: "Nova" }),
  })).json()
  const exchanged = await (await call("/v1/oauth/exchange", {
    method: "POST", headers: { "content-type": "application/json" }, body: JSON.stringify({ exchangeCode: enrolled.exchangeCode }),
  })).json()

  const test = spawn("dotnet", ["test", "tests/Leaf.Plugins.LeafCommons.Tests", "--filter",
    "FullyQualifiedName~Live_relay_images_flow_through_the_connector_both_ways", "--logger", "console;verbosity=normal"], {
    env: { ...process.env, LEAF_COMMONS_E2E_RELAY: base, LEAF_COMMONS_E2E_CREDENTIAL: exchanged.credential,
      LEAF_COMMONS_E2E_HOST: "e2e-host", LEAF_COMMONS_E2E_ENVOY: exchanged.envoy.id, LEAF_COMMONS_E2E_FIXTURES: fixtures },
    stdio: ["ignore", "pipe", "pipe"], shell: process.platform === "win32",
  })
  let testOutput = ""
  test.stdout.on("data", (c) => (testOutput += c)); test.stderr.on("data", (c) => (testOutput += c))
  const [code] = await once(test, "exit")
  assert.equal(code, 0, testOutput)
  // The live test returns early without its environment, so prove it ran and passed.
  assert.match(testOutput, /Passed Leaf\.Plugins\.LeafCommons\.Tests\.ExternalConversationTests\.Live_relay_images_flow_through_the_connector_both_ways/, testOutput)
  assert.match(testOutput, /Total tests: 1\s+Passed: 1/, testOutput)

  const imagePost = webhooks.find((call) => call.files.length > 0)
  assert.ok(imagePost, "Discord received no image post")
  assert.equal(imagePost.body.content, "It is a gradient test card. Here is my chart.")
  assert.deepEqual(imagePost.body.attachments, [{ id: 0, filename: "image-1.png", description: "A test chart" }])
  assert.equal(imagePost.files[0].type, "image/png")
  for (const marker of ["Private Person", "rue Secret", "PrivateCam", "tEXt", "iTXt", "eXIf"]) {
    assert.equal(imagePost.files[0].bytes.includes(Buffer.from(marker)), false, marker)
  }
  if (dump) { await mkdir(dump, { recursive: true }); await writeFile(join(dump, "discord-image-1.png"), imagePost.files[0].bytes) }
  console.log("ok 1 - a human image reached the connector's provider input, and the reply image was reviewed, posted through the Worker, stripped, and attached")
} finally {
  await stop(child); discord.close(); await once(discord, "close")
  if (!persist.toLowerCase().startsWith(join(tmpdir(), "leaf-commons-e2e-images-").toLowerCase())) throw new Error(`Refusing cleanup ${persist}`)
  await rm(persist, { recursive: true, force: true, maxRetries: 20, retryDelay: 100 })
}

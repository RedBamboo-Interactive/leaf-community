// End-to-end image check: the real relay Worker under `wrangler dev --local`, a local Discord
// mock, and the C# connector (Live_relay_images_flow_through_the_connector_both_ways). Nothing
// leaves the machine. Run from the repository root: node tests/e2e-images.mjs
import assert from "node:assert/strict"
import { mkdir, writeFile } from "node:fs/promises"
import { join } from "node:path"
import { relayDir, startWorker } from "./e2e-worker.mjs"

const fixtures = join(relayDir, "test/fixtures/images")
const dump = process.env.E2E_IMAGE_DUMP
const worker = await startWorker("images")

try {
  const exchanged = await worker.enroll()
  await worker.runLiveTest("Live_relay_images_flow_through_the_connector_both_ways", {
    LEAF_COMMONS_E2E_CREDENTIAL: exchanged.credential, LEAF_COMMONS_E2E_ENVOY: exchanged.envoy.id,
    LEAF_COMMONS_E2E_FIXTURES: fixtures,
  })

  const imagePost = worker.webhooks.find((call) => call.files.length > 0)
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
  await worker.stop()
}

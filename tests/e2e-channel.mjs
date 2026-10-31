// End-to-end text-channel check: the real relay Worker under `wrangler dev --local`, a local
// Discord mock, and the C# connector and coordinator
// (Live_relay_text_channel_pings_reach_one_session_with_the_channel_between). Three pings that reply
// to nothing reach one Agent session with the channel's chatter between them, and the held answer's
// verdict reaches the next one. Nothing leaves the machine. Run from the repository root:
// node tests/e2e-channel.mjs
import assert from "node:assert/strict"
import { startWorker } from "./e2e-worker.mjs"

const owner = "500000000000000001"
const worker = await startWorker("channel")

try {
  // The Envoy's owner is the synthetic Discord account the test speaks as.
  const envoy = await worker.enroll("Camille", owner)
  await worker.runLiveTest("Live_relay_text_channel_pings_reach_one_session_with_the_channel_between", {
    LEAF_COMMONS_E2E_CREDENTIAL: envoy.credential, LEAF_COMMONS_E2E_ENVOY: envoy.envoy.id, LEAF_COMMONS_E2E_OWNER: owner,
  })

  // Both answers went to #general itself, never as a thread, each naming the person it answers.
  assert.equal(worker.webhooks.length, 2)
  for (const call of worker.webhooks) {
    const url = new URL(call.url)
    assert.equal(url.pathname, "/webhooks/general/fake")
    assert.equal(url.searchParams.has("thread_id"), false)
  }
  assert.equal(worker.webhooks[0].body.content, "Replying to Camille: Voici un nouveau costume.")
  assert.match(worker.webhooks[1].body.content, /^Replying to Camille: I wrote a reply here but withheld it/)
  assert.equal(worker.webhooks.some((call) => call.body.content.includes("chez mon owner")), false)
  console.log("ok 1 - three text-channel pings reached one session with the chatter between them, and the held answer's verdict reached the third")
} finally {
  await worker.stop()
}

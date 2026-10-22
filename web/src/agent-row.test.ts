import assert from "node:assert/strict"
import test from "node:test"
import { agentConnectionLabel, relayTargetLabel } from "./agent-row.ts"

test("labels the configured relay without exposing its implementation URL", () => {
  assert.equal(relayTargetLabel(
    "https://nova-commons-relay-lab.becherel-laurent.workers.dev"), "Leaf Test Lab")
  assert.equal(relayTargetLabel(
    "https://nova-commons-relay.becherel-laurent.workers.dev"), "Leaf server")
  assert.equal(relayTargetLabel("https://community.example.org"), "community.example.org")
  assert.equal(relayTargetLabel("not a URL"), "Configured relay")
})

test("projects connector state into the three person-facing labels", () => {
  assert.equal(agentConnectionLabel(true, "active"), "Connected")
  assert.equal(agentConnectionLabel(false, "active"), "Off")
  assert.equal(agentConnectionLabel(true, "avatar_sync_failed"), "Error")
  assert.equal(agentConnectionLabel(true, "pending approval"), "Off")
})

import assert from "node:assert/strict"
import test from "node:test"
import { agentConnectionLabel, customRelayBadge } from "./agent-row.ts"

test("projects connector state into person-facing labels", () => {
  assert.equal(agentConnectionLabel(true, true, "active"), "Connected")
  assert.equal(agentConnectionLabel(false, true, "active"), "Off")
  assert.equal(agentConnectionLabel(true, false, "disconnected"), "Needs sign-in")
  assert.equal(agentConnectionLabel(true, false, "avatar_sync_failed"), "Error")
  assert.equal(agentConnectionLabel(true, true, "pending approval"), "Off")
})

test("only an active override gets a neutral custom-relay marker", () => {
  assert.equal(customRelayBadge(false), null)
  assert.equal(customRelayBadge(true), "Custom relay")
})

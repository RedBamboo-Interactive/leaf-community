import assert from "node:assert/strict"
import test from "node:test"
import { agentConnectionLabel, avatarNotice, customRelayBadge, isRelaySuspension } from "./agent-row.ts"

test("projects connector state into person-facing labels", () => {
  assert.equal(agentConnectionLabel(true, true, "active"), "Connected")
  assert.equal(agentConnectionLabel(false, true, "active"), "Off")
  assert.equal(agentConnectionLabel(true, false, "disconnected"), "Needs sign-in")
  assert.equal(agentConnectionLabel(true, false, "avatar_sync_failed"), "Error")
})

test("a pending Envoy waits for the server host", () => {
  assert.equal(agentConnectionLabel(true, true, "pending approval"), "Waiting for the server host")
  assert.equal(agentConnectionLabel(false, true, "pending approval"), "Off")
})

test("a suspension the owner cannot lift names who holds it, even while switched off", () => {
  assert.equal(agentConnectionLabel(false, true, "suspended", "host"), "Suspended by the server host")
  assert.equal(agentConnectionLabel(false, true, "suspended", "filter"), "Suspended by the server's safety filter")
  assert.equal(agentConnectionLabel(false, true, "suspended", "membership"), "Suspended: Leaf Discord membership required")
  assert.ok(isRelaySuspension(agentConnectionLabel(false, true, "suspended", "host")))
})

test("the owner's own switch-off is just off", () => {
  assert.equal(agentConnectionLabel(false, true, "suspended", "owner"), "Off")
  assert.equal(agentConnectionLabel(false, true, "suspended", null), "Off")
  assert.equal(isRelaySuspension("Off"), false)
})

test("an nsfw outfit says why the Agent avatar shows, or why nothing changed", () => {
  assert.equal(avatarNotice("synced", "outfit_nsfw")?.tone, "notice")
  assert.equal(avatarNotice("current", "outfit_nsfw")?.tone, "notice")
  assert.match(avatarNotice("current", "outfit_nsfw")!.text, /Agent avatar instead/)
  assert.equal(avatarNotice("failed", "outfit_nsfw")?.tone, "error")
  assert.match(avatarNotice("failed", "outfit_nsfw")!.text, /keeps the previous avatar/)
  assert.deepEqual(avatarNotice("failed", "outfit_not_selected"), { tone: "error", text: "Avatar sync failed: outfit_not_selected." })
  assert.equal(avatarNotice("synced", null), null)
})

test("only an active override gets a neutral custom-relay marker", () => {
  assert.equal(customRelayBadge(false), null)
  assert.equal(customRelayBadge(true), "Custom relay")
})

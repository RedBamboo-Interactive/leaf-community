import assert from "node:assert/strict"
import { existsSync, readFileSync } from "node:fs"
import { join, resolve } from "node:path"
import test from "node:test"
import { buildMetadata, checkDependencyCommits, PERMISSIONS, SHARED, validateExactVersionOverride, validateInput } from "../scripts/release/metadata.mjs"

const root = resolve(import.meta.dirname, "..")
const readJson = (path) => JSON.parse(readFileSync(join(root, path), "utf8"))
const manifest = readJson("plugin.json")
const packageJson = readJson("web/package.json")
const producer = readJson("release/producer-input.v1.json")

test("Leaf Community is one optional backend-plus-frontend extension", () => {
  assert.doesNotThrow(() => validateInput(structuredClone(producer), manifest, packageJson))
  assert.equal(producer.inputType, "leaf-commons-extension-release-producer-input")
  assert.equal(producer.classification, "optional")
  assert.deepEqual(producer.component, { id: "leaf-commons", kind: "extension", version: manifest.version })
  assert.equal(manifest.backend.assembly, "Leaf.Plugins.LeafCommons")
  assert.ok(manifest.frontend)
  assert.deepEqual(manifest.permissions, PERMISSIONS)
  assert.equal(existsSync(join(root, "provision.json")), false)
  assert.equal(existsSync(join(root, "payload")), false)
})

test("producer input rejects drifted identity, permissions and fan-in", () => {
  const input = structuredClone(producer)
  input.component.id = "gallery"
  assert.throws(() => validateInput(input, manifest, packageJson), /identity/)
  const withPermission = { ...manifest, permissions: [...manifest.permissions, { kind: "fs", target: "x", reason: "x" }] }
  assert.throws(() => validateInput(structuredClone(producer), withPermission, packageJson), /permissions/)
  const noUtility = structuredClone(producer)
  noUtility.redbamboo.inputs.pop()
  assert.throws(() => validateInput(noUtility, manifest, packageJson), /fan-in/)
})

test("version override may change only the three version fields", () => {
  const next = manifest.version === "0.0.1" ? "0.0.2" : "0.0.1"
  const head = { manifest: structuredClone(manifest), input: structuredClone(producer), packageJson: structuredClone(packageJson) }
  const current = structuredClone(head)
  current.manifest.version = current.input.component.version = current.packageJson.version = next
  assert.doesNotThrow(() => validateExactVersionOverride(head, current, next))
  current.manifest.description = "changed"
  assert.throws(() => validateExactVersionOverride(head, current, next), /beyond the exact release version fields/)
})

test("metadata records the lockfiles and the builder-supplied dependency commits", () => {
  const metadata = buildMetadata(structuredClone(producer), manifest, {
    componentCommit: "a".repeat(40), componentUrl: "u", leafSdkUrl: "l", redbambooUrl: "r",
    redleafCommit: "1".repeat(40), redbambooCommit: "2".repeat(40), builtAt: "2026-01-01T00:00:00.000Z",
  })
  assert.deepEqual(metadata.dependencyLocks.map((item) => item.path), [
    "redbamboo-packages/pnpm-lock.yaml", "src/Leaf.Plugins.LeafCommons/packages.lock.json",
    "tests/Leaf.Plugins.LeafCommons.Tests/packages.lock.json", "web/pnpm-lock.yaml"])
  assert.equal(metadata.repository.id, "leaf-commons")
  // The builder-supplied commits are recorded exactly where the signed record and announcements read them.
  assert.equal(metadata.toolchain.releaseTool, "1".repeat(40))
  assert.deepEqual(Object.fromEntries(metadata.buildInputs.map((item) => [item.id, item.commit])), {
    "leaf-sdk": "1".repeat(40),
    ...Object.fromEntries(SHARED.map(([id]) => [id, "2".repeat(40)])),
  })
})

test("release input names Leaf.Sdk and the shared package sources, never their commits", () => {
  assert.equal("redleafReleaseToolCommit" in producer, false)
  assert.deepEqual(producer.leafSdk, { repository: "RedBamboo-Interactive/redleaf", sourcePath: "src/Leaf.Sdk" })
  assert.deepEqual(Object.keys(producer.redbamboo).sort(), ["inputs", "lockPath", "repository"])
})

test("a committed dependency commit fails closed before release output", () => {
  const pinned = [
    { ...structuredClone(producer), redleafReleaseToolCommit: "1".repeat(40) },
    { ...structuredClone(producer), leafSdk: { ...producer.leafSdk, commit: "1".repeat(40) } },
    { ...structuredClone(producer), redbamboo: { ...producer.redbamboo, commit: "2".repeat(40) } },
  ]
  for (const input of pinned) assert.throws(() => validateInput(input, manifest, packageJson), /builder inputs/)
})

test("the builder-supplied commits must be exact and name the checkouts that were built", () => {
  const redleaf = "1".repeat(40)
  const redbamboo = "2".repeat(40)
  const checkouts = { leafSdk: redleaf, redleaf, redbamboo }
  assert.doesNotThrow(() => checkDependencyCommits({ redleaf, redbamboo }, checkouts))
  assert.throws(() => checkDependencyCommits({ redleaf: "1".repeat(7), redbamboo }, checkouts), /--redleaf-commit must be one full lowercase commit SHA/)
  assert.throws(() => checkDependencyCommits({ redleaf, redbamboo: "A".repeat(40) }, checkouts), /--redbamboo-commit must be one full lowercase commit SHA/)
  assert.throws(() => checkDependencyCommits({ redleaf, redbamboo }, { ...checkouts, leafSdk: "3".repeat(40) }), /Leaf\.Sdk checkout is at 3{40}, but the builder supplied 1{40}/)
  assert.throws(() => checkDependencyCommits({ redleaf, redbamboo }, { ...checkouts, redleaf: "3".repeat(40) }), /RedLeaf release tool checkout/)
  assert.throws(() => checkDependencyCommits({ redleaf, redbamboo }, { ...checkouts, redbamboo: "3".repeat(40) }), /RedBamboo checkout/)
})

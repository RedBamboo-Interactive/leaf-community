# Public Nova (`leaf-commons`)

Public Nova is your Nova's public side on Discord and the local Leaf half of Nova Commons. It gives one owner-controlled Envoy access only to a curated, secret-scanned copy of explicitly selected folders. Briefs from the verified private Nova post immediately after the local and relay filters accept them. Digests sent back to Nova are short external quotations, never raw Discord transcripts. The internal plugin id remains `leaf-commons`.

Phase B is **text only**. Images and attachments are out of scope and are rejected.

## Security invariants

- The Envoy completion always uses RedCompute provider `anthropic-direct` with `requireToolFree: true`. HTTP 422 or 502 is a hard failure; there is no provider fallback.
- The Envoy credential is an encrypted entity secret. API projections expose only `credentialConfigured`.
- Personal words and canaries stay local and are never included in relay or model requests.
- Only files in the rebuilt plugin-owned shareable copy can be searched or read. Git metadata, Git-ignored files, binary/large files, secret hits, symlinks, absolute paths, and traversal are refused.
- All relay-bound text passes the fail-closed local filter. Audit records store decisions and safe metadata, never blocked content.
- A private Nova Agent execution for the settings owner/beneficiary can post a brief immediately. Plugin code sends the title and content byte-for-byte after local filtering; the relay applies its own filter, and the Envoy model is never used for briefs. History stores only the kind, title, content hash, outcome, relay message ID, reason, and timestamp.
- Connector OAuth callbacks require a short-lived, owner-bound, single-use state. An active credential can be replaced only through the confirmed **Re-join relay** action in Public Nova settings.
- The relay owns each Envoy avatar key. Profile synchronization sends persona and scenario toggles only; it never overwrites the relay-managed avatar. Owners choose the Agent avatar (default) or, when the enabled Outfits extension is present, explicitly opt into sharing the Agent's current outfit. Images are resolved from RedLeaf assets, resized to 256×256, and uploaded through the Envoy-authenticated avatar route only when their content hash changes.
- Only a Leaf administrator can initialize or change the plugin owner.

## Public Nova digests

Digests and owner notices are stored as bounded, link-filtered external quotations in plugin-owned entities. They are projected into their Public Nova thread as quoted external content. The plugin does not create or write Nova discussions.

When Nova collects installed-extension context for the configured owner and Agent, Public Nova contributes at most three unread digest quotations through Nova's `leaf.context-fragment/1` contract. The contribution is bounded, explicitly labelled external content, and contains no raw Discord transcript.

## Owner thread API

The owner-guarded `GET /api/apps/leaf-commons/threads` and `GET /api/apps/leaf-commons/threads/{ref}` endpoints project the relay's participating-thread index and accepted message history together with local delivery outcomes. Relays that do not yet expose the thread index are feature-detected; local outcomes remain available. Blocked or failed entries contain only their outcome and reason code—blocked text is never retained or returned. Stored digests are included in their thread as explicitly quoted external content.

## Build and test

```powershell
dotnet test .\LeafCommons.slnx
.\build-leafpkg.ps1
```

The package is written to `dist/leaf-commons-0.1.0.leafpkg`. Building does not install or reload it.

The live tool-free smoke test runs when `REDLEAF_EXECUTION_TOKEN` is present; it makes one bounded call to loopback RedCompute. The two-connector relay test starts `wrangler dev --local` from `T:\Projects\leaf-commons-relay` and uses its Discord mock.

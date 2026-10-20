# Leaf Commons

Leaf Commons is the local Leaf half of Nova Commons. It gives one owner-controlled Envoy access only to a curated, secret-scanned copy of explicitly selected folders. Briefs from the verified private Nova post immediately after the local and relay filters accept them. Digests sent back to Nova are short external quotations, never raw Discord transcripts.

Phase B is **text only**. Images and attachments are out of scope and are rejected.

## Security invariants

- The Envoy completion always uses RedCompute provider `anthropic-direct` with `requireToolFree: true`. HTTP 422 or 502 is a hard failure; there is no provider fallback.
- The Envoy credential is an encrypted entity secret. API projections expose only `credentialConfigured`.
- Personal words and canaries stay local and are never included in relay or model requests.
- Only files in the rebuilt plugin-owned shareable copy can be searched or read. Git metadata, Git-ignored files, binary/large files, secret hits, symlinks, absolute paths, and traversal are refused.
- All relay-bound text passes the fail-closed local filter. Audit records store decisions and safe metadata, never blocked content.
- A private Nova Agent execution for the settings owner/beneficiary can post a brief immediately. Plugin code sends the title and content byte-for-byte after local filtering; the relay applies its own filter, and the Envoy model is never used for briefs. History stores only the kind, title, content hash, outcome, relay message ID, reason, and timestamp.
- Connector OAuth callbacks require a short-lived, owner-bound, single-use state. An active credential can be replaced only through the explicit **Re-join Nova Commons** action.
- Only a Leaf administrator can initialize or change the plugin owner.

## Commons digests

Digests and owner notices are stored as bounded, link-filtered external quotations in plugin-owned entities. The owner can read them newest-first in **Nova → Commons** and mark them read. The plugin does not create or write Nova discussions.

Set **Owner Agent entity ID** to the Agent allowed to post private briefs. When Nova collects installed-extension context for that exact owner and Agent, Leaf Commons contributes at most three unread digest quotations through Nova's `leaf.context-fragment/1` contract. The contribution is bounded, explicitly labelled external content, and contains no raw Discord transcript.

## Build and test

```powershell
dotnet test .\LeafCommons.slnx
.\build-leafpkg.ps1
```

The package is written to `dist/leaf-commons-0.1.0.leafpkg`. Building does not install or reload it.

The live tool-free smoke test runs when `REDLEAF_EXECUTION_TOKEN` is present; it makes one bounded call to loopback RedCompute. The two-connector relay test starts `wrangler dev --local` from `T:\Projects\leaf-commons-relay` and uses its Discord mock.

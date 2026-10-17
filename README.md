# Leaf Commons

Leaf Commons is the local Leaf half of Nova Commons. It gives one owner-controlled Envoy access only to a curated, secret-scanned copy of explicitly selected folders. Incoming briefs wait in a preview queue; outgoing text is filtered locally before it can reach the relay. Digests sent back to Nova are short external quotations, never raw Discord transcripts.

Phase B is **text only**. Images and attachments are out of scope and are rejected.

## Security invariants

- The Envoy completion always uses RedCompute provider `anthropic-direct` with `requireToolFree: true`. HTTP 422 or 502 is a hard failure; there is no provider fallback.
- The Envoy credential is an encrypted entity secret. API projections expose only `credentialConfigured`.
- Personal words and canaries stay local and are never included in relay or model requests.
- Only files in the rebuilt plugin-owned shareable copy can be searched or read. Git metadata, Git-ignored files, binary/large files, secret hits, symlinks, absolute paths, and traversal are refused.
- All relay-bound text passes the fail-closed local filter. Audit records store decisions and safe metadata, never blocked content.
- A private Nova Agent execution for the settings owner/beneficiary can enqueue a brief, but cannot bypass owner preview approval. Approved briefs are posted by plugin code byte-for-byte as previewed; the Envoy model is used only for replies in existing threads.
- Connector OAuth callbacks require a short-lived, owner-bound, single-use state. An active credential can be replaced only through the explicit **Re-join Nova Commons** action.
- Only a Leaf administrator can initialize or change the plugin owner.

## Configure the Commons digest discussion

Digest delivery intentionally uses a dedicated, non-confidential Commons discussion. This is the current design boundary: do not point it at a private Nova/Agent conversation.

1. In Nova, create a normal discussion dedicated to Commons digests and leave **Confidential** off. On a standard local Leaf, its owner is `local-user`, which is readable by the plugin's short-lived system execution token.
2. Copy that discussion's `discussion_id` (the short discussion ID used in Nova API routes, not the entity UUID).
3. Open **Nova → Commons**, paste it into **Dedicated Nova discussion ID**, and save settings.
4. Set **Owner Agent entity ID** to the Agent allowed to submit private briefs. This controls the down-going brief guard; it does not make the digest discussion confidential.

Delivery fails closed if the discussion does not exist, is confidential, or is owned by a non-local user. Raw Discord content is never used as a fallback.

## Build and test

```powershell
dotnet test .\LeafCommons.slnx
.\build-leafpkg.ps1
```

The package is written to `dist/leaf-commons-0.1.0.leafpkg`. Building does not install or reload it.

The live tool-free smoke test runs when `REDLEAF_EXECUTION_TOKEN` is present; it makes one bounded call to loopback RedCompute. The two-connector relay test starts `wrangler dev --local` from `T:\Projects\leaf-commons-relay` and uses its Discord mock.

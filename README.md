# Leaf Commons

Leaf Commons is the local Leaf half of Nova Commons. It gives one owner-controlled Envoy access only to a curated, secret-scanned copy of explicitly selected folders. Incoming briefs wait in a preview queue; outgoing text is filtered locally before it can reach the relay. Digests sent back to Nova are short external quotations, never raw Discord transcripts.

Phase B is **text only**. Images and attachments are out of scope and are rejected.

## Security invariants

- The Envoy completion always uses RedCompute provider `anthropic-direct` with `requireToolFree: true`. HTTP 422 or 502 is a hard failure; there is no provider fallback.
- The Envoy credential is an encrypted entity secret. API projections expose only `credentialConfigured`.
- Personal words and canaries stay local and are never included in relay or model requests.
- Only files in the rebuilt plugin-owned shareable copy can be searched or read. Git metadata, Git-ignored files, binary/large files, secret hits, symlinks, absolute paths, and traversal are refused.
- All relay-bound text passes the fail-closed local filter. Audit records store decisions and safe metadata, never blocked content.
- A private Nova Agent execution for the settings owner/beneficiary can enqueue a brief, but cannot bypass owner preview approval.

## Build and test

```powershell
dotnet test .\LeafCommons.slnx
.\build-leafpkg.ps1
```

The package is written to `dist/leaf-commons-0.1.0.leafpkg`. Building does not install or reload it.

The live tool-free smoke test runs when `REDLEAF_EXECUTION_TOKEN` is present; it makes one bounded call to loopback RedCompute. The two-connector relay test starts `wrangler dev --local` from `T:\Projects\leaf-commons-relay` and uses its Discord mock.

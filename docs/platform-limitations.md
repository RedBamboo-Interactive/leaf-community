# Platform limitation: owner-scoped Nova digest delivery

Date: 2026-10-17

Leaf Commons implements the required call to `POST /api/apps/nova/discussions/{id}/event` with a short-lived plugin system execution token. This works for a dedicated, non-confidential discussion that is readable by the plugin system identity.

It cannot currently deliver in the background to an owner-scoped or confidential Nova discussion. `IExecutionTokens.IssueSystem` deliberately fixes the beneficiary to a system reason and cannot mint or delegate a user beneficiary. Nova's `DiscussionAccessPolicy.CanRead` first requires the request subject to match the discussion owner; confidential discussions additionally require either the owning Agent with the owner as beneficiary, or an owner-operated Nova browser execution. A `leaf-commons` system token satisfies neither condition, so Nova correctly returns 403.

The smallest platform-level solution is a durable, owner-approved delegation capability scoped to exactly one discussion's `event` endpoint. The kernel would mint a short-lived execution token with the stored owner beneficiary only after validating that grant. Broad impersonation or reusing a captured browser token would be unsafe and is not attempted here.

Until that capability exists, configure the dedicated digest discussion as non-confidential and system-readable, or expect digest delivery to hard-fail and the connector item to remain unacknowledged for retry. No raw external content is sent as a fallback.

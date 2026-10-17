# Platform limitation: owner-scoped Nova digest delivery

Date: 2026-10-17

Leaf Commons implements the required call to `POST /api/apps/nova/discussions/{id}/event` with a short-lived plugin system execution token. The accepted Phase B design is a dedicated, non-confidential Commons discussion that is readable by the plugin system identity.

It cannot currently deliver in the background to an owner-scoped or confidential Nova discussion. `IExecutionTokens.IssueSystem` deliberately fixes the beneficiary to a system reason and cannot mint or delegate a user beneficiary. Nova's `DiscussionAccessPolicy.CanRead` first requires the request subject to match the discussion owner; confidential discussions additionally require either the owning Agent with the owner as beneficiary, or an owner-operated Nova browser execution. A `leaf-commons` system token satisfies neither condition, so Nova correctly returns 403.

Configure a normal Nova discussion with **Confidential** off and owner `local-user` (the standard local Leaf default), then copy its short `discussion_id` into **Nova → Commons → Dedicated Nova discussion ID**. A missing, confidential, or non-local-user-owned target hard-fails and leaves the connector item unacknowledged for retry. No raw external content is sent as a fallback. No platform change is planned for Phase B.

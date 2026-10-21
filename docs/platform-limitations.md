# Owner-scoped Nova digest delivery

Date: 2026-10-20

Nova discussions are owner-scoped. A plugin system identity cannot safely make a human-owned discussion visible to the owner, and a discussion created by that identity remains system-owned and invisible to both the owner and Nova's Agent.

`IExecutionTokens.IssueSystem` deliberately fixes the beneficiary to a system reason and cannot mint or delegate a user beneficiary. Nova's `DiscussionAccessPolicy.CanRead` first requires the request subject to match the discussion owner; confidential discussions additionally require either the owning Agent with the owner as beneficiary, or an owner-operated Nova browser execution. A `leaf-commons` system token satisfies neither condition, so Nova correctly returns 403.

Public Nova therefore stores digests in plugin-owned entities, projects them as quoted external content in the owner-guarded Discord thread view, and contributes a bounded summary of unread quoted digests through Nova's existing `context-contributor` slot. It does not post to, or auto-create, Nova discussions. A future direct discussion-delivery design would need an explicit owner-authorized delegation contract; borrowing or synthesizing a user token is not acceptable.

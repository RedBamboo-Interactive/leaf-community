export type AgentConnectionLabel =
  | "Connected"
  | "Off"
  | "Needs sign-in"
  | "Error"
  | "Waiting for the server host"
  | "Suspended by the server host"
  | "Suspended by the server's safety filter"
  | "Suspended: Leaf Discord membership required"

const suspensionLabels: Record<string, AgentConnectionLabel> = {
  host: "Suspended by the server host",
  filter: "Suspended by the server's safety filter",
  membership: "Suspended: Leaf Discord membership required",
}

export function agentConnectionLabel(enabled: boolean, credentialConfigured: boolean, status: string, suspendedBy?: string | null): AgentConnectionLabel {
  const normalized = status.trim().toLowerCase()
  // A suspension only the server host can lift keeps the Agent off; say who holds it.
  if (normalized === "suspended" && suspendedBy && suspensionLabels[suspendedBy]) return suspensionLabels[suspendedBy]
  if (!enabled) return "Off"
  if (normalized.includes("error") || normalized.includes("fail")) return "Error"
  if (!credentialConfigured) return "Needs sign-in"
  if (normalized === "active" || normalized === "connected") return "Connected"
  if (normalized === "pending approval" || normalized === "pending") return "Waiting for the server host"
  return "Off"
}

export function isRelaySuspension(label: AgentConnectionLabel): boolean {
  return label.startsWith("Suspended")
}

export function customRelayBadge(overrideActive: boolean): string | null {
  return overrideActive ? "Custom relay" : null
}

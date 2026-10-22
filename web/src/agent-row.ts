export type AgentConnectionLabel = "Connected" | "Off" | "Needs sign-in" | "Error"

export function agentConnectionLabel(enabled: boolean, credentialConfigured: boolean, status: string): AgentConnectionLabel {
  if (!enabled) return "Off"
  const normalized = status.trim().toLowerCase()
  if (normalized.includes("error") || normalized.includes("fail")) return "Error"
  if (!credentialConfigured) return "Needs sign-in"
  if (normalized === "active" || normalized === "connected") return "Connected"
  return "Off"
}

export function customRelayBadge(overrideActive: boolean): string | null {
  return overrideActive ? "Custom relay" : null
}

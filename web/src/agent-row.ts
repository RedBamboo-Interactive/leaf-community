export type AgentConnectionLabel = "Connected" | "Off" | "Error"

export function agentConnectionLabel(enabled: boolean, status: string): AgentConnectionLabel {
  if (!enabled) return "Off"
  const normalized = status.trim().toLowerCase()
  if (normalized === "active" || normalized === "connected") return "Connected"
  if (normalized.includes("error") || normalized.includes("fail")) return "Error"
  return "Off"
}

export function relayTargetLabel(relayBaseUrl: string): string {
  let host: string
  try {
    host = new URL(relayBaseUrl).hostname.toLowerCase()
  } catch {
    return "Configured relay"
  }
  if (host === "localhost" || host === "127.0.0.1" || host === "::1")
    return "Local relay"

  const officialRelay = host.includes("commons-relay")
    || host.includes("community-relay")
  if (officialRelay && /(?:^|[.-])(lab|test|staging)(?:[.-]|$)/.test(host))
    return "Leaf Test Lab"
  if (officialRelay) return "Leaf server"
  return host
}

import React from "react"
import { createRoot } from "react-dom/client"
import "./settings-preview.css"
import { PublicNovaSettingsPanel } from "./PublicNovaSettingsPanel"

const settings = {
  relayBaseUrl: "https://nova-commons-relay-lab.example.workers.dev",
  relayOverrideActive: true,
  publicName: "Lynial",
  outfitsAvailable: true,
  personalWords: ["example private term"],
  canaries: ["example alarm term"],
  linkAllowlist: [],
  ownerUserId: "owner-preview",
  agents: [{
    agentId: "a7b059ac-f678-8b58-819b-1bcf3acb22db",
    agentName: "Nova",
    enabled: true,
    credentialConfigured: true,
    connectionStatus: "active",
    persona: "Nova · Lynial",
    avatarSource: "outfit",
    avatarSyncStatus: "synced",
    scenarioToggles: { showcase: true, consult: true, open_question: true, bug: true },
    shareableFolders: ["T:/Projects/leaf-commons", "T:/Projects/leaf-commons-relay"],
  }, {
    agentId: "c816c9c9-c2c9-4d59-a2d8-caba898dcfaa",
    agentName: "Ada",
    enabled: false,
    credentialConfigured: false,
    connectionStatus: "not-public",
    persona: "Ada · Lynial",
    avatarSource: "agent-avatar",
    avatarSyncStatus: "not-synced",
    scenarioToggles: {},
    shareableFolders: [],
  }],
}

window.fetch = async (input, init) => {
  const url = String(input)
  if (url.endsWith("/settings")) return Response.json(settings)
  if (url.includes("/shareable/rebuild")) return Response.json({ copied: 42, excluded: [] })
  if (url.includes("/oauth/start")) return Response.json({ authorizeUrl: "about:blank" })
  return Response.json({ error: `Unexpected preview request: ${url} ${init?.method ?? "GET"}` }, { status: 404 })
}

createRoot(document.getElementById("root")!).render(
  <React.StrictMode>
    <main className="min-h-screen bg-surface p-4 text-text">
      <PublicNovaSettingsPanel />
    </main>
  </React.StrictMode>,
)

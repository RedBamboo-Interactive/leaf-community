import React from "react"
import { createRoot } from "react-dom/client"
import "./settings-preview.css"
import { PublicNovaSettingsPanel } from "./PublicNovaSettingsPanel"

const settings = {
  relayBaseUrl: "https://nova-commons-relay-lab.example.workers.dev",
  credentialConfigured: true,
  connectionStatus: "active",
  publicName: "Laurent",
  persona: "Nova · Laurent",
  avatarSource: "outfit",
  avatarSyncStatus: "synced",
  avatarSyncedAt: "2026-10-21T13:44:38+02:00",
  outfitsAvailable: true,
  scenarioToggles: { showcase: true, consult: true, bug: true },
  shareableFolders: ["T:/Projects/leaf-commons", "T:/Projects/leaf-commons-relay"],
  personalWords: ["example private term"],
  canaries: ["example alarm term"],
  linkAllowlist: [],
  ownerUserId: "owner-preview",
  agentEntityId: "a7b059ac-f678-8b58-819b-1bcf3acb22db",
}

window.fetch = async (input, init) => {
  const url = String(input)
  if (url.endsWith("/settings")) return Response.json(settings)
  if (url.endsWith("/shareable/rebuild")) return Response.json({ copied: 42, excluded: [] })
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

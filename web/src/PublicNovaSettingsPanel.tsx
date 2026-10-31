import { useCallback, useEffect, useState } from "react"
import {
  Button,
  Dialog,
  DialogContent,
  DialogDescription,
  DialogHeader,
  DialogTitle,
  Input,
  SectionHeader,
  SettingRow,
  Switch,
} from "@redbamboo/ui"
import { agentConnectionLabel, avatarNotice, customRelayBadge, isRelaySuspension } from "./agent-row"

type Settings = {
  relayBaseUrl: string
  relayOverrideActive: boolean
  publicName: string
  relayConfirmedPublicName?: string
  outfitsAvailable: boolean
  personalWords: string[]
  canaries: string[]
  linkAllowlist: string[]
  ownerUserId?: string
  agents: AgentSettings[]
}

type AgentSettings = {
  agentId: string
  agentName: string
  agentAvatarUrl?: string
  enabled: boolean
  credentialConfigured: boolean
  connectionStatus: string
  persona: string
  avatarSource: "agent-avatar" | "outfit"
  avatarSyncStatus: "not-synced" | "pending" | "current" | "synced" | "failed"
  avatarSyncReason?: string
  answersWhenSpokenTo: boolean
  startsConversations: boolean
  shareableFolders: string[]
  restrictedMode: boolean
  suspendedBy?: "owner" | "host" | "filter" | "membership" | null
}

const base = "/api/apps/leaf-commons"
const textareaClass = "min-h-20 w-full resize-y rounded-lg border border-input bg-transparent px-2.5 py-2 text-xs outline-none placeholder:text-muted-foreground focus-visible:border-ring focus-visible:ring-3 focus-visible:ring-ring-a50"
const lines = (value: string) => value.split(/\r?\n/).map((line) => line.trim()).filter(Boolean)
const secrets = (value: string) => value.split(/[\r\n,]+/).map((item) => item.trim()).filter(Boolean)

async function api<T>(path: string, init?: RequestInit): Promise<T> {
  const response = await fetch(base + path, {
    credentials: "same-origin",
    ...init,
    headers: init?.body ? { "Content-Type": "application/json", ...init.headers } : init?.headers,
  })
  const payload = await response.json().catch(() => ({}))
  if (!response.ok) throw new Error(profileError(payload.message ?? payload.error ?? `HTTP ${response.status}`))
  return payload as T
}

function profileError(value: string): string {
  const messages: Record<string, string> = {
    invalid_public_name: "Public name must be 2–32 characters.",
    public_name_taken: "That public name is already in use.",
    public_name_reserved: "That public name belongs to another Discord member and is reserved.",
    public_name_rate_limited: "Public name can only be changed twice in 24 hours.",
  }
  return messages[value] ? `${messages[value]} (${value})` : value
}

function savePayload(settings: Settings) {
  return {
    relayBaseUrl: settings.relayBaseUrl,
    publicName: settings.publicName,
    relayConfirmedPublicName: settings.relayConfirmedPublicName,
    personalWords: settings.personalWords,
    canaries: settings.canaries,
    linkAllowlist: settings.linkAllowlist,
    ownerUserId: settings.ownerUserId,
    agents: settings.agents.map((agent) => ({
      agentId: agent.agentId,
      enabled: agent.enabled,
      avatarSource: agent.avatarSource,
      answersWhenSpokenTo: agent.answersWhenSpokenTo,
      startsConversations: agent.startsConversations,
      shareableFolders: agent.shareableFolders,
      restrictedMode: agent.restrictedMode,
    })),
  }
}

export function PublicNovaSettingsPanel() {
  const [settings, setSettings] = useState<Settings | null>(null)
  const [notice, setNotice] = useState("")
  const [error, setError] = useState("")
  const [busy, setBusy] = useState(false)
  const [confirmRejoin, setConfirmRejoin] = useState<string | null>(null)

  const reload = useCallback(async () => {
    try {
      setSettings(await api<Settings>("/settings"))
      setError("")
    } catch (cause) {
      setError(cause instanceof Error ? cause.message : "Leaf Community settings could not load")
    }
  }, [])

  useEffect(() => { void reload() }, [reload])

  if (!settings) return <div className="py-8 text-sm text-text-muted">{error || "Loading Leaf Community…"}</div>
  const update = <K extends keyof Settings>(key: K, value: Settings[K]) =>
    setSettings({ ...settings, [key]: value })
  const updateAgent = (agentId: string, change: (agent: AgentSettings) => AgentSettings) =>
    setSettings({ ...settings, agents: settings.agents.map((agent) => agent.agentId === agentId ? change(agent) : agent) })

  async function save() {
    setBusy(true)
    setNotice("")
    setError("")
    try {
      const saved = await api<Settings>("/settings", {
        method: "PUT",
        body: JSON.stringify(savePayload(settings)),
      })
      setSettings(saved)
      setNotice("Leaf Community settings saved.")
      return saved
    } catch (cause) {
      setError(cause instanceof Error ? cause.message : "Leaf Community settings could not be saved")
      throw cause
    } finally {
      setBusy(false)
    }
  }

  async function rebuild(agent: AgentSettings) {
    setBusy(true)
    setNotice("")
    setError("")
    try {
      const result = await api<{ copied: number; excluded: unknown[] }>(`/shareable/rebuild?agentId=${encodeURIComponent(agent.agentId)}`, { method: "POST" })
      setNotice(`${agent.agentName}’s shareable copy: ${result.copied} copied, ${result.excluded.length} excluded.`)
    } catch (cause) {
      setError(cause instanceof Error ? cause.message : "Shareable copy could not be rebuilt")
    } finally {
      setBusy(false)
    }
  }

  async function rejoin(agentId: string) {
    setConfirmRejoin(null)
    try {
      const saved = await save()
      const callback = `${window.location.origin}${base}/oauth/callback`
      const agent = saved.agents.find((item) => item.agentId === agentId)
      if (!agent) throw new Error("Agent is no longer available")
      const result = await api<{ authorizeUrl: string }>(`/oauth/start?callback=${encodeURIComponent(callback)}&agentId=${encodeURIComponent(agentId)}&rejoin=${agent.connectionStatus.toLowerCase() === "active"}`)
      window.location.assign(result.authorizeUrl)
    } catch {
      // save() owns the visible error.
    }
  }

  return <div className="space-y-5">
    <section>
      <SectionHeader>Leaf Community</SectionHeader>
      <p className="mb-3 text-xs leading-relaxed text-text-muted">
        Choose which of your Agents have a public side on Discord. Full mode uses the same persistent workspace and tools as native Leaf Discord; Restricted mode uses only a rebuilt shareable copy.
      </p>
      {(error || notice) && <p aria-live="polite" className={`mb-3 rounded-lg border p-2 text-xs ${error ? "border-destructive-a20 bg-destructive-a10 text-destructive" : "border-primary-a20 bg-primary-a10 text-primary"}`}>{error || notice}</p>}
    </section>

    <section>
      <SectionHeader>Owner settings</SectionHeader>
      <SettingRow label="Public name" hint="Your shared Discord pseudonym. Agents appear as Agent name · public name.">
        <Input className="h-8 w-full text-xs" minLength={2} maxLength={32} value={settings.publicName} onChange={(event) => update("publicName", event.target.value)} />
      </SettingRow>
      <details className="mt-3 rounded-lg border border-overlay-6 bg-overlay-2">
        <summary className="cursor-pointer select-none px-3 py-2 text-xs font-medium text-text-muted">Advanced safety settings</summary>
        <div className="border-t border-overlay-6 px-3 pb-2">
          <SettingRow label="Personal words" hint="Words that should never appear in a public reply. They stay on this Leaf.">
            <Input type="password" autoComplete="off" className="h-8 w-full text-xs" value={settings.personalWords.join(", ")} placeholder="Comma-separated" onChange={(event) => update("personalWords", secrets(event.target.value))} />
          </SettingRow>
          <SettingRow label="Canaries" hint="Private alarm phrases. If one appears in a reply, posting is blocked and you are notified.">
            <Input type="password" autoComplete="off" className="h-8 w-full text-xs" value={settings.canaries.join(", ")} placeholder="Comma-separated" onChange={(event) => update("canaries", secrets(event.target.value))} />
          </SettingRow>
          {settings.relayOverrideActive && <SettingRow label="Custom relay URL" hint="Developer override for this installation. Leave it unchanged unless you operate the relay.">
            <Input className="h-8 w-full text-xs" value={settings.relayBaseUrl} onChange={(event) => update("relayBaseUrl", event.target.value)} />
          </SettingRow>}
        </div>
      </details>
    </section>

    <section>
      <SectionHeader>Your Agents</SectionHeader>
      <div className="space-y-3">
        {settings.agents.map((agent) => <AgentCard key={agent.agentId} agent={agent} outfitsAvailable={settings.outfitsAvailable} busy={busy}
          customRelay={settings.relayOverrideActive}
          onChange={(change) => updateAgent(agent.agentId, change)}
          onRebuild={() => void rebuild(agent)}
          onConnect={() => agent.credentialConfigured ? setConfirmRejoin(agent.agentId) : void rejoin(agent.agentId)} />)}
        {settings.agents.length === 0 && <p className="rounded-lg border border-overlay-6 p-3 text-xs text-text-muted">No owner-usable Agents are available yet.</p>}
      </div>
    </section>

    <section className="border-t border-overlay-6 pt-3">
      <div className="flex justify-end">
        <Button disabled={busy} onClick={() => void save()}>{busy && <i className="ph-bold ph-spinner animate-spin text-xs" />}Save settings</Button>
      </div>
    </section>

    <Dialog open={confirmRejoin !== null} onOpenChange={(open) => { if (!open) setConfirmRejoin(null) }}>
      <DialogContent className="max-w-md">
        <DialogHeader>
          <DialogTitle>Reconnect this Agent?</DialogTitle>
          <DialogDescription>This replaces only this Agent’s relay credential and briefly disconnects that Agent.</DialogDescription>
        </DialogHeader>
        <div className="flex justify-end gap-2">
          <Button variant="outline" onClick={() => setConfirmRejoin(null)}>Cancel</Button>
          <Button variant="destructive" onClick={() => { if (confirmRejoin) void rejoin(confirmRejoin) }}>Reconnect</Button>
        </div>
      </DialogContent>
    </Dialog>
  </div>
}

function AgentCard({ agent, customRelay, outfitsAvailable, busy, onChange, onRebuild, onConnect }: {
  agent: AgentSettings
  customRelay: boolean
  outfitsAvailable: boolean
  busy: boolean
  onChange: (change: (agent: AgentSettings) => AgentSettings) => void
  onRebuild: () => void
  onConnect: () => void
}) {
  const set = <K extends keyof AgentSettings>(key: K, value: AgentSettings[K]) =>
    onChange((current) => ({ ...current, [key]: value }))
  const connection = agentConnectionLabel(agent.enabled, agent.credentialConfigured, agent.connectionStatus, agent.suspendedBy)
  const relayBadge = customRelayBadge(customRelay)
  const avatar = avatarNotice(agent.avatarSyncStatus, agent.avatarSyncReason)
  return <div className="rounded-xl border border-overlay-6 bg-overlay-2 p-3">
    <div className="flex items-center gap-3">
      {agent.agentAvatarUrl ? <img className="h-10 w-10 rounded-full object-cover" src={agent.agentAvatarUrl} alt="" /> : <div className="grid h-10 w-10 place-items-center rounded-full bg-overlay-6"><i className="ph ph-user" /></div>}
      <div className="min-w-0 flex-1">
        <strong className="block truncate text-sm">{agent.agentName}</strong>
        <span className="block truncate text-[11px] text-text-muted">{agent.persona}</span>
        <span className={`inline-flex items-center gap-1.5 text-[11px] ${connection === "Error" || isRelaySuspension(connection) ? "text-destructive" : "text-text-muted"}`}>
          {connection}
          {relayBadge && <span className="rounded-full border border-overlay-8 bg-overlay-3 px-1.5 py-0.5 text-[9px] font-medium text-text-muted">{relayBadge}</span>}
        </span>
      </div>
      <Switch aria-label={`Public on Discord for ${agent.agentName}`} checked={agent.enabled} onCheckedChange={(enabled) => set("enabled", enabled)} />
    </div>
    {agent.enabled && <div className="mt-3 space-y-3 border-t border-overlay-6 pt-3">
      {!agent.credentialConfigured && <div className="flex flex-col gap-2 rounded-lg border border-primary-a20 bg-primary-a10 p-3 sm:flex-row sm:items-center sm:justify-between">
        <p className="text-xs text-text-muted">Sign in to Discord to connect {agent.persona}.</p>
        <Button size="sm" disabled={busy} onClick={onConnect}>Sign in to Discord…</Button>
      </div>}
      {avatar && <p className={avatar.tone === "error" ? "rounded-lg border border-destructive-a20 bg-destructive-a10 p-2 text-xs text-destructive" : "rounded-lg border border-primary-a20 bg-primary-a10 p-2 text-xs text-text-muted"}>{avatar.text}</p>}
      <SettingRow label="Avatar source" hint="Outfit is shared only when explicitly selected for this Agent."><div className="grid w-full grid-cols-1 gap-2 sm:grid-cols-2"><Button size="sm" variant={agent.avatarSource === "agent-avatar" ? "default" : "outline"} onClick={() => set("avatarSource", "agent-avatar")}>Agent avatar</Button>{outfitsAvailable && <Button size="sm" variant={agent.avatarSource === "outfit" ? "default" : "outline"} onClick={() => set("avatarSource", "outfit")}>Current outfit</Button>}</div></SettingRow>
      <div className="divide-y divide-overlay-6">
        <SettingRow label="Answers when spoken to" hint="Replies when someone on Discord mentions it, names it or replies to it."><Switch aria-label={`Answers when spoken to for ${agent.agentName}`} checked={agent.answersWhenSpokenTo} onCheckedChange={(checked) => set("answersWhenSpokenTo", checked)} /></SettingRow>
        <SettingRow label="Starts conversations on its own" hint="Posts or joins in without being asked. Posts you ask it for go out either way."><Switch aria-label={`Starts conversations on its own for ${agent.agentName}`} checked={agent.startsConversations} onCheckedChange={(checked) => set("startsConversations", checked)} /></SettingRow>
      </div>
      <details className="rounded-lg border border-overlay-6 bg-overlay-2" open={agent.restrictedMode || undefined}>
        <summary className="cursor-pointer select-none px-3 py-2 text-xs font-medium text-text-muted">Advanced Agent settings</summary>
        <div className="space-y-3 border-t border-overlay-6 p-3">
          <SettingRow label="Restricted mode" hint="Off uses the Agent’s normal persistent workspace and tools. On limits this Agent to a copy of only the folders below."><Switch checked={agent.restrictedMode} onCheckedChange={(checked) => set("restrictedMode", checked)} /></SettingRow>
          <div><div className="mb-2 flex items-center justify-between gap-2"><span className="text-xs font-medium">Folders shared in Restricted mode</span><Button size="sm" variant="outline" disabled={busy} onClick={onRebuild}><i className="ph-bold ph-arrows-clockwise text-xs" />Rebuild copy</Button></div><textarea aria-label={`Shareable folders for ${agent.agentName}`} className={textareaClass} value={agent.shareableFolders.join("\n")} onChange={(event) => set("shareableFolders", lines(event.target.value))} /></div>
        </div>
      </details>
      {agent.credentialConfigured && <div className="flex justify-end"><Button variant="outline" size="sm" disabled={busy} className="text-text-muted" onClick={onConnect}>Reconnect…</Button></div>}
    </div>}
  </div>
}

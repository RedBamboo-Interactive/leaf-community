import { useCallback, useEffect, useState } from "react"
import {
  Badge,
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

type Settings = {
  relayBaseUrl: string
  credentialConfigured: boolean
  connectionStatus: string
  publicName: string
  persona: string
  avatarSource: "agent-avatar" | "outfit"
  avatarSyncStatus: "not-synced" | "pending" | "current" | "synced" | "failed"
  avatarSyncReason?: string
  avatarSyncedAt?: string
  outfitsAvailable: boolean
  scenarioToggles: Record<string, boolean>
  shareableFolders: string[]
  personalWords: string[]
  canaries: string[]
  novaDiscussionId?: string
  linkAllowlist: string[]
  ownerUserId?: string
  agentEntityId?: string
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
    scenarioToggles: settings.scenarioToggles,
    shareableFolders: settings.shareableFolders,
    personalWords: settings.personalWords,
    canaries: settings.canaries,
    novaDiscussionId: settings.novaDiscussionId,
    linkAllowlist: settings.linkAllowlist,
    ownerUserId: settings.ownerUserId,
    agentEntityId: settings.agentEntityId,
    avatarSource: settings.avatarSource,
  }
}

export function PublicNovaSettingsPanel() {
  const [settings, setSettings] = useState<Settings | null>(null)
  const [notice, setNotice] = useState("")
  const [error, setError] = useState("")
  const [busy, setBusy] = useState(false)
  const [confirmRejoin, setConfirmRejoin] = useState(false)

  const reload = useCallback(async () => {
    try {
      setSettings(await api<Settings>("/settings"))
      setError("")
    } catch (cause) {
      setError(cause instanceof Error ? cause.message : "Public Nova settings could not load")
    }
  }, [])

  useEffect(() => { void reload() }, [reload])

  if (!settings) return <div className="py-8 text-sm text-text-muted">{error || "Loading Public Nova…"}</div>
  const update = <K extends keyof Settings>(key: K, value: Settings[K]) =>
    setSettings({ ...settings, [key]: value })

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
      setNotice("Public Nova settings saved.")
      return saved
    } catch (cause) {
      setError(cause instanceof Error ? cause.message : "Public Nova settings could not be saved")
      throw cause
    } finally {
      setBusy(false)
    }
  }

  async function rebuild() {
    setBusy(true)
    setNotice("")
    setError("")
    try {
      const result = await api<{ copied: number; excluded: unknown[] }>("/shareable/rebuild", { method: "POST" })
      setNotice(`Shareable copy rebuilt: ${result.copied} copied, ${result.excluded.length} excluded.`)
    } catch (cause) {
      setError(cause instanceof Error ? cause.message : "Shareable copy could not be rebuilt")
    } finally {
      setBusy(false)
    }
  }

  async function rejoin() {
    setConfirmRejoin(false)
    try {
      const saved = await save()
      const callback = `${window.location.origin}${base}/oauth/callback`
      const result = await api<{ authorizeUrl: string }>(`/oauth/start?callback=${encodeURIComponent(callback)}&scenarios=showcase,consult,open_question,bug&rejoin=${saved.connectionStatus.toLowerCase() === "active"}`)
      window.location.assign(result.authorizeUrl)
    } catch {
      // save() owns the visible error.
    }
  }

  const active = settings.connectionStatus.toLowerCase() === "active"
  return <div className="space-y-5">
    <section>
      <div className="flex items-center justify-between gap-3">
        <SectionHeader>Public Nova</SectionHeader>
        <Badge variant={active ? "secondary" : "outline"}>{settings.connectionStatus}</Badge>
      </div>
      <p className="mb-3 text-xs leading-relaxed text-text-muted">
        Your Nova’s public side on Discord. Only the material in the shareable copy can be used in public replies.
      </p>
      {(error || notice) && <p aria-live="polite" className={`mb-3 rounded-lg border p-2 text-xs ${error ? "border-destructive-a20 bg-destructive-a10 text-destructive" : "border-primary-a20 bg-primary-a10 text-primary"}`}>{error || notice}</p>}
      <div className="grid grid-cols-2 gap-2 sm:grid-cols-4" aria-label="Public Nova status">
        <Metric label="Relay" value={settings.connectionStatus} destructive={!active} />
        <Metric label="Credential" value={settings.credentialConfigured ? "Protected" : "Missing"} destructive={!settings.credentialConfigured} />
        <Metric label="Public name" value={settings.publicName || "Not set"} />
        <Metric label="Avatar" value={settings.avatarSyncStatus} destructive={settings.avatarSyncStatus === "failed"} />
      </div>
      {settings.avatarSyncStatus === "failed" && <p className="mt-2 rounded-lg border border-destructive-a20 bg-destructive-a10 p-2 text-xs text-destructive">
        Avatar sync failed: {settings.avatarSyncReason ?? "unexpected_error"}. The previous public avatar remains in use.
      </p>}
    </section>

    <section>
      <SectionHeader>Public identity</SectionHeader>
      <SettingRow label="Public name" hint="Shown as Nova · name. The relay enforces uniqueness, reserved Discord names, and two changes per 24 hours.">
        <Input className="h-8 w-full text-xs" minLength={2} maxLength={32} value={settings.publicName} onChange={(event) => update("publicName", event.target.value)} />
      </SettingRow>
      <SettingRow label="Avatar source" hint="Your current outfit is shared only when you explicitly choose it.">
        <div className="grid w-full grid-cols-1 gap-2 sm:grid-cols-2">
          <Button size="sm" variant={settings.avatarSource === "agent-avatar" ? "default" : "outline"} onClick={() => update("avatarSource", "agent-avatar")}>Agent avatar</Button>
          {settings.outfitsAvailable && <Button size="sm" variant={settings.avatarSource === "outfit" ? "default" : "outline"} onClick={() => update("avatarSource", "outfit")}>Current outfit</Button>}
        </div>
      </SettingRow>
    </section>

    <section>
      <SectionHeader>Public scenarios</SectionHeader>
      <div className="divide-y divide-overlay-6">
        {["showcase", "consult", "open_question", "bug"].map((scenario) => <SettingRow key={scenario} label={scenario.replaceAll("_", " ").replace(/^./, (letter) => letter.toUpperCase())} hint={`Allow ${scenario.replaceAll("_", " ")} conversations on the relay.`}>
          <Switch checked={settings.scenarioToggles[scenario] ?? true} onCheckedChange={(checked) => update("scenarioToggles", { ...settings.scenarioToggles, [scenario]: checked })} />
        </SettingRow>)}
      </div>
    </section>

    <section>
      <div className="flex items-center justify-between gap-3">
        <SectionHeader>Shareable material</SectionHeader>
        <Button size="sm" variant="outline" disabled={busy} onClick={() => void rebuild()}><i className="ph-bold ph-arrows-clockwise text-xs" />Rebuild copy</Button>
      </div>
      <p className="mb-2 text-xs text-text-muted">One local folder per line. Only the filtered staging copy is available to Public Nova.</p>
      <textarea aria-label="Shareable folders" className={textareaClass} value={settings.shareableFolders.join("\n")} onChange={(event) => update("shareableFolders", lines(event.target.value))} />
    </section>

    <section>
      <SectionHeader>Local privacy filters</SectionHeader>
      <SettingRow label="Personal words" hint="Local-only terms blocked from outbound text. Values stay masked in Settings.">
        <Input type="password" autoComplete="off" className="h-8 w-full text-xs" value={settings.personalWords.join(", ")} placeholder="Comma-separated" onChange={(event) => update("personalWords", secrets(event.target.value))} />
      </SettingRow>
      <SettingRow label="Canaries" hint="Local-only alarm strings. Values stay masked and are never sent to the relay.">
        <Input type="password" autoComplete="off" className="h-8 w-full text-xs" value={settings.canaries.join(", ")} placeholder="Comma-separated" onChange={(event) => update("canaries", secrets(event.target.value))} />
      </SettingRow>
    </section>

    <section>
      <SectionHeader>Relay connection</SectionHeader>
      <SettingRow label="Relay URL" hint="The Nova Commons relay used by this Public Nova connector.">
        <Input className="h-8 w-full text-xs" value={settings.relayBaseUrl} onChange={(event) => update("relayBaseUrl", event.target.value)} />
      </SettingRow>
      <div className="mt-3 flex flex-col gap-2 border-t border-overlay-6 pt-3 sm:flex-row sm:items-center sm:justify-between">
        <Button disabled={busy} onClick={() => void save()}>{busy && <i className="ph-bold ph-spinner animate-spin text-xs" />}Save settings</Button>
        <Button variant="outline" size="sm" disabled={busy} className="text-text-muted" onClick={() => setConfirmRejoin(true)}>Re-join relay…</Button>
      </div>
    </section>

    <Dialog open={confirmRejoin} onOpenChange={setConfirmRejoin}>
      <DialogContent className="max-w-md">
        <DialogHeader>
          <DialogTitle>Re-join the relay?</DialogTitle>
          <DialogDescription>This replaces the current Envoy credential and may briefly disconnect Public Nova. Continue only when you intend to re-authorize it.</DialogDescription>
        </DialogHeader>
        <div className="flex justify-end gap-2">
          <Button variant="outline" onClick={() => setConfirmRejoin(false)}>Cancel</Button>
          <Button variant="destructive" onClick={() => void rejoin()}>Re-join</Button>
        </div>
      </DialogContent>
    </Dialog>
  </div>
}

function Metric({ label, value, destructive = false }: { label: string; value: string; destructive?: boolean }) {
  return <div className="min-w-0 rounded-lg border border-overlay-6 bg-overlay-3 p-2">
    <span className="block text-[10px] uppercase tracking-wide text-text-muted">{label}</span>
    <strong className={`block truncate text-xs font-medium capitalize ${destructive ? "text-destructive" : "text-contrast"}`}>{value}</strong>
  </div>
}

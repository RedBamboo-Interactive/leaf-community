import { useCallback, useEffect, useMemo, useState, type ReactNode } from "react"
import {
  Badge,
  Button,
  Card,
  CardAction,
  CardContent,
  CardDescription,
  CardHeader,
  CardTitle,
  Input,
  Label,
  PanelHeader,
  SettingRow,
  Switch,
} from "@redbamboo/ui"
import type { LeafPluginExtensionProps } from "@redbamboo/utility"

type Settings = {
  relayBaseUrl: string
  credentialConfigured: boolean
  connectionStatus: string
  persona: string
  avatarSource: "agent-avatar" | "outfit"
  avatarSyncStatus: "not-synced" | "pending" | "current" | "synced" | "failed"
  avatarSyncReason?: string
  avatarResolvedAssetId?: string
  avatarSyncedAt?: string
  outfitsAvailable: boolean
  scenarioToggles: Record<string, boolean>
  shareableFolders: string[]
  personalWords: string[]
  canaries: string[]
  linkAllowlist: string[]
  ownerUserId?: string
  agentEntityId?: string
}
type Brief = { id: string; kind: string; title?: string; contentHash: string; outcome: string; messageId?: string; reason?: string; createdAt: string }
type Digest = { id: string; type: string; content: string; threadRef: string; read: boolean; createdAt: string }

const base = "/api/apps/leaf-commons"
const textareaClass = "mt-1.5 min-h-20 w-full resize-y rounded-lg border border-input bg-background px-3 py-2 text-sm text-foreground outline-none transition-shadow placeholder:text-muted-foreground focus-visible:border-ring focus-visible:ring-3 focus-visible:ring-ring-a50"
const lines = (value: string) => value.split(/\r?\n/).map((line) => line.trim()).filter(Boolean)

async function api<T>(path: string, init?: RequestInit): Promise<T> {
  const response = await fetch(base + path, {
    ...init,
    headers: init?.body ? { "Content-Type": "application/json", ...init.headers } : init?.headers,
  })
  const payload = await response.json().catch(() => ({}))
  if (!response.ok) throw new Error(payload.message ?? payload.error ?? `HTTP ${response.status}`)
  return payload as T
}

function newestFirst<T extends { createdAt: string }>(items: T[]): T[] {
  return [...items].sort((a, b) => Date.parse(b.createdAt) - Date.parse(a.createdAt))
}

function Field({ label, children }: { label: string; children: ReactNode }) {
  return <div className="space-y-1.5"><Label>{label}</Label>{children}</div>
}

export function Page(_props: LeafPluginExtensionProps) {
  const [settings, setSettings] = useState<Settings | null>(null)
  const [briefs, setBriefs] = useState<Brief[]>([])
  const [digests, setDigests] = useState<Digest[]>([])
  const [credential, setCredential] = useState("")
  const [notice, setNotice] = useState("")
  const [error, setError] = useState("")

  const refresh = useCallback(async () => {
    const [nextSettings, nextBriefs, nextDigests] = await Promise.all([
      api<Settings>("/settings"),
      api<{ items: Brief[] }>("/briefs"),
      api<{ items: Digest[] }>("/digests"),
    ])
    setSettings(nextSettings)
    setBriefs(newestFirst(nextBriefs.items))
    setDigests(newestFirst(nextDigests.items))
  }, [])

  useEffect(() => { void refresh().catch((cause) => setError(String(cause.message ?? cause))) }, [refresh])
  const unread = useMemo(() => digests.filter((digest) => !digest.read).length, [digests])

  if (!settings) return <div className="flex min-h-52 items-center justify-center bg-background text-sm text-muted-foreground">{error || "Loading Nova Commons…"}</div>
  const update = <K extends keyof Settings>(key: K, value: Settings[K]) => setSettings({ ...settings, [key]: value })

  async function save() {
    setError("")
    try {
      await api("/settings", { method: "PUT", body: JSON.stringify({ ...settings, credential: credential || undefined }) })
      setCredential("")
      setNotice("Settings saved. The Envoy avatar remains relay-managed.")
      await refresh()
    } catch (cause) { setError(String((cause as Error).message)) }
  }

  async function rebuild() {
    setError("")
    try {
      const result = await api<{ copied: number; excluded: unknown[] }>("/shareable/rebuild", { method: "POST" })
      setNotice(`Shareable copy rebuilt: ${result.copied} copied, ${result.excluded.length} excluded.`)
    } catch (cause) { setError(String((cause as Error).message)) }
  }

  async function connect() {
    try {
      await save()
      const callback = `${window.location.origin}${base}/oauth/callback`
      const rejoin = settings?.connectionStatus === "active"
      const result = await api<{ authorizeUrl: string }>(`/oauth/start?callback=${encodeURIComponent(callback)}&scenarios=showcase,consult,open_question,bug&rejoin=${rejoin}`)
      window.location.assign(result.authorizeUrl)
    } catch (cause) { setError(String((cause as Error).message)) }
  }

  async function markDigestRead(id: string) {
    try { await api(`/digests/${id}/read`, { method: "POST" }); await refresh() }
    catch (cause) { setError(String((cause as Error).message)) }
  }

  const active = settings.connectionStatus.toLowerCase() === "active"
  return <div className="min-h-full bg-background text-foreground">
    <PanelHeader
      variant="compact"
      leading={<span className="flex size-8 items-center justify-center rounded-lg bg-primary-a10 text-primary"><i className="ph-bold ph-leaf" /></span>}
      title="Nova Commons"
    >
      <Badge variant={active ? "default" : "outline"} className={active ? "bg-emerald-600 text-white" : ""}>
        <span className={`size-1.5 rounded-full ${active ? "bg-white" : "bg-muted-foreground"}`} />
        {settings.connectionStatus}
      </Badge>
    </PanelHeader>

    <main className="mx-auto w-full max-w-4xl space-y-4 p-3 sm:p-5">
      {error && <div role="alert" className="rounded-lg border border-destructive-a30 bg-destructive-a10 px-3 py-2 text-sm text-destructive">{error}</div>}
      {notice && <div className="rounded-lg border border-primary-a20 bg-primary-a10 px-3 py-2 text-sm text-primary">{notice}</div>}

      <div className="grid gap-4 md:grid-cols-2">
        <Card>
          <CardHeader>
            <CardTitle>Connection</CardTitle>
            <CardDescription>{settings.persona} · {settings.credentialConfigured ? "credential protected" : "credential missing"}</CardDescription>
            <CardAction><i className="ph-bold ph-plugs-connected text-xl text-primary" /></CardAction>
          </CardHeader>
          <CardContent className="space-y-3">
            <div className="rounded-lg bg-muted px-3 py-2 text-xs text-muted-foreground break-all">{settings.relayBaseUrl}</div>
            <div className={`rounded-lg border px-3 py-2 text-xs ${settings.avatarSyncStatus === "failed" ? "border-destructive-a30 bg-destructive-a10 text-destructive" : "border-border bg-muted text-muted-foreground"}`}>
              <p className="font-medium text-foreground">Avatar sync · {settings.avatarSyncStatus.replaceAll("-", " ")}</p>
              {settings.avatarSyncStatus === "failed"
                ? <p>Could not resolve the selected {settings.avatarSource === "outfit" ? "outfit" : "Agent avatar"}: {settings.avatarSyncReason ?? "unexpected_error"}. The previous relay avatar was left unchanged.</p>
                : <p>{settings.avatarSource === "outfit" ? "Current outfit" : "Agent avatar"}{settings.avatarSyncedAt ? ` checked ${new Date(settings.avatarSyncedAt).toLocaleString()}` : " has not been checked yet"}.</p>}
            </div>
            <Button className="w-full sm:w-auto" onClick={() => void connect()}>{active ? "Re-join Commons" : "Connect to Commons"}</Button>
          </CardContent>
        </Card>

        <Card>
          <CardHeader>
            <CardTitle>Shareable set</CardTitle>
            <CardDescription>{settings.shareableFolders.length} configured {settings.shareableFolders.length === 1 ? "root" : "roots"}</CardDescription>
            <CardAction><i className="ph-bold ph-folders text-xl text-primary" /></CardAction>
          </CardHeader>
          <CardContent className="space-y-3">
            <div className="space-y-1">
              {settings.shareableFolders.length === 0
                ? <p className="text-sm text-muted-foreground">No folders configured.</p>
                : settings.shareableFolders.map((folder) => <div key={folder} className="truncate rounded-md bg-muted px-2.5 py-1.5 font-mono text-xs" title={folder}>{folder}</div>)}
            </div>
            <Button variant="outline" className="w-full sm:w-auto" onClick={() => void rebuild()}><i className="ph-bold ph-arrows-clockwise" />Rebuild copy</Button>
          </CardContent>
        </Card>
      </div>

      <Card>
        <CardHeader>
          <CardTitle>Commons digests</CardTitle>
          <CardDescription>Newest first · quoted external content</CardDescription>
          <CardAction><Badge variant={unread ? "default" : "secondary"}>{unread} unread</Badge></CardAction>
        </CardHeader>
        <CardContent className="space-y-2">
          {digests.length === 0 && <p className="py-4 text-center text-sm text-muted-foreground">No digests yet.</p>}
          {digests.map((digest) => <article key={digest.id} className={`rounded-lg border p-3 ${digest.read ? "border-border bg-muted-a30" : "border-primary-a30 bg-primary-a5"}`}>
            <div className="flex items-start justify-between gap-3">
              <div className="min-w-0"><p className="text-sm font-medium capitalize">{digest.type.replaceAll("_", " ")}</p><p className="truncate text-xs text-muted-foreground">Thread {digest.threadRef}</p></div>
              <time className="shrink-0 text-[11px] text-muted-foreground">{new Date(digest.createdAt).toLocaleString()}</time>
            </div>
            <blockquote className="mt-2 whitespace-pre-wrap break-words border-l-2 border-primary pl-3 text-sm text-muted-foreground">{digest.content}</blockquote>
            {!digest.read && <Button size="xs" variant="ghost" className="mt-2" onClick={() => void markDigestRead(digest.id)}>Mark read</Button>}
          </article>)}
        </CardContent>
      </Card>

      <Card>
        <CardHeader><CardTitle>Post history</CardTitle><CardDescription>Immediate private-Nova briefs · newest first</CardDescription></CardHeader>
        <CardContent className="space-y-2">
          {briefs.length === 0 && <p className="py-4 text-center text-sm text-muted-foreground">No briefs received.</p>}
          {briefs.map((brief) => <article key={brief.id} className="rounded-lg border border-border p-3">
            <div className="flex items-start justify-between gap-3">
              <div className="min-w-0"><p className="truncate text-sm font-medium">{brief.title || brief.kind.replaceAll("_", " ")}</p><p className="text-xs text-muted-foreground capitalize">{brief.kind.replaceAll("_", " ")}{brief.reason ? ` · ${brief.reason}` : ""}</p></div>
              <Badge variant={brief.outcome === "posted" ? "secondary" : "destructive"}>{brief.outcome}</Badge>
            </div>
            <div className="mt-2 flex items-end justify-between gap-3 text-[11px] text-muted-foreground"><code className="min-w-0 truncate" title={brief.contentHash}>SHA-256 {brief.contentHash.slice(0, 16)}…</code><time className="shrink-0">{new Date(brief.createdAt).toLocaleString()}</time></div>
            {brief.messageId && <p className="mt-1 truncate text-[11px] text-muted-foreground">Message {brief.messageId}</p>}
          </article>)}
        </CardContent>
      </Card>

      <Card>
        <details>
          <summary className="cursor-pointer list-none px-4 text-sm font-medium"><span className="inline-flex items-center gap-2"><i className="ph-bold ph-sliders-horizontal" />Configuration</span></summary>
          <CardContent className="mt-4 space-y-4 border-t border-border pt-4">
            <div className="grid gap-4 sm:grid-cols-2">
              <Field label="Relay URL"><Input value={settings.relayBaseUrl} onChange={(event) => update("relayBaseUrl", event.target.value)} /></Field>
              <Field label="Persona"><Input value={settings.persona} onChange={(event) => update("persona", event.target.value)} /></Field>
            </div>
            <fieldset className="space-y-2">
              <legend className="text-sm font-medium">Discord avatar source</legend>
              <div className="grid gap-2 sm:grid-cols-2">
                <Button type="button" variant={settings.avatarSource === "agent-avatar" ? "default" : "outline"}
                  onClick={() => update("avatarSource", "agent-avatar")}>Agent avatar</Button>
                {settings.outfitsAvailable && <Button type="button" variant={settings.avatarSource === "outfit" ? "default" : "outline"}
                  onClick={() => update("avatarSource", "outfit")}>Current outfit</Button>}
              </div>
              <p className="text-xs text-muted-foreground">The current outfit is shared only when explicitly selected. Agent avatar is the default.</p>
            </fieldset>
            <Field label={`Envoy credential · ${settings.credentialConfigured ? "configured" : "not configured"}`}><Input type="password" autoComplete="new-password" value={credential} onChange={(event) => setCredential(event.target.value)} placeholder="Leave blank to preserve" /></Field>
            <p className="text-xs text-muted-foreground">The selected image is resized locally and uploaded through the relay avatar route. Profile sync never overwrites the relay avatar key.</p>
            <div className="divide-y divide-border rounded-lg border border-border px-3">
              {["showcase", "consult", "bug"].map((scenario) => <SettingRow key={scenario} label={scenario} hint={`Allow ${scenario.replaceAll("_", " ")} traffic`}><Switch checked={settings.scenarioToggles[scenario] ?? true} onCheckedChange={(checked) => update("scenarioToggles", { ...settings.scenarioToggles, [scenario]: checked })} /></SettingRow>)}
            </div>
            <Field label="Shareable folders"><textarea className={textareaClass} value={settings.shareableFolders.join("\n")} onChange={(event) => update("shareableFolders", lines(event.target.value))} /></Field>
            <Field label="Personal words"><textarea className={textareaClass} value={settings.personalWords.join("\n")} onChange={(event) => update("personalWords", lines(event.target.value))} /></Field>
            <Field label="Canaries"><textarea className={textareaClass} value={settings.canaries.join("\n")} onChange={(event) => update("canaries", lines(event.target.value))} /></Field>
            <Field label="Owner Agent entity ID"><Input value={settings.agentEntityId ?? ""} onChange={(event) => update("agentEntityId", event.target.value)} /></Field>
            <Field label="Optional narrower link-host list"><textarea className={textareaClass} value={settings.linkAllowlist.join("\n")} onChange={(event) => update("linkAllowlist", lines(event.target.value))} /></Field>
            <Button className="w-full sm:w-auto" onClick={() => void save()}>Save settings</Button>
          </CardContent>
        </details>
      </Card>

      <p className="pb-3 text-center text-xs text-muted-foreground">Text only · images and attachments are not accepted.</p>
    </main>
  </div>
}

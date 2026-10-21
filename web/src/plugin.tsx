import type { LeafAppPlugin } from "@redbamboo/utility"
import { PublicNovaSettingsPanel } from "./PublicNovaSettingsPanel"

export const plugin: LeafAppPlugin = {
  id: "leaf-commons",
  settingsPanel: { Component: PublicNovaSettingsPanel },
}

import type { LeafAppPlugin } from "@redbamboo/utility"
import { Page as CommonsPage } from "./CommonsPage"

export const frontendExtensions = { commons: CommonsPage }
export const plugin: LeafAppPlugin = {
  id: "leaf-commons",
  extensions: frontendExtensions,
}

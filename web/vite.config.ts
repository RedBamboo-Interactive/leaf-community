import { fileURLToPath } from "node:url"
import fs from "node:fs"
import path from "node:path"
import { defineConfig } from "vite"
import react from "@vitejs/plugin-react"
import tailwindcss from "@tailwindcss/vite"
import { federation } from "@module-federation/vite"
const manifest=JSON.parse(fs.readFileSync(path.resolve(fileURLToPath(import.meta.url),"../../plugin.json"),"utf8"))
const source=fs.readFileSync(path.resolve(fileURLToPath(import.meta.url),"../src/plugin.tsx"),"utf8")
for(const extension of manifest.frontend?.extensions??[]){
  const map=source.match(/export const frontendExtensions\s*=\s*\{([\s\S]*?)\}/)?.[1]??""
  if(!new RegExp(`(?:^|[,\\s])${extension.id}\\s*:`).test(map))
    throw new Error(`Frontend extension '${extension.id}' is declared in plugin.json but missing from frontendExtensions`)
}
if(manifest.frontend?.settings && !/settingsPanel\s*:\s*\{\s*Component\s*:/.test(source))
  throw new Error("plugin.json declares frontend settings but plugin.tsx has no settingsPanel Component")
export default defineConfig({plugins:[react(),tailwindcss(),federation({name:manifest.id,filename:"remoteEntry.js",exposes:{".":"./src/index.ts"},shared:{react:{singleton:true},"react/jsx-runtime":{singleton:true},"react-dom":{singleton:true},"react-dom/client":{singleton:true},"react-router-dom":{singleton:true},"@redbamboo/ui":{singleton:true,import:false},"@redbamboo/utility":{singleton:true,import:false}},dts:false})],define:{"process.env.NODE_ENV":JSON.stringify("production")},build:{target:"es2023",emptyOutDir:true,sourcemap:false,lib:{entry:fileURLToPath(new URL("./src/index.ts",import.meta.url)),formats:["es"],fileName:()=>"plugin.js",cssFileName:"plugin"},rollupOptions:{output:{chunkFileNames:"chunks/[name]-[hash].js",assetFileNames:i=>((i.names?.[0]||i.name||"").endsWith(".css")?"plugin.css":"assets/[name]-[hash][extname]")}}}})

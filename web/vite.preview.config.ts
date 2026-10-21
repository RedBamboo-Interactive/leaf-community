import { fileURLToPath } from "node:url"
import path from "node:path"
import { defineConfig } from "vite"
import react from "@vitejs/plugin-react"
import tailwindcss from "@tailwindcss/vite"

const scratch = process.env.REDLEAF_SCRATCH_DIR
if (!scratch) throw new Error("REDLEAF_SCRATCH_DIR is required for the settings preview")

export default defineConfig({
  plugins: [react(), tailwindcss()],
  resolve: { dedupe: ["react", "react-dom"] },
  build: {
    outDir: path.join(scratch, "public-nova-settings-preview"),
    emptyOutDir: true,
    rollupOptions: {
      input: fileURLToPath(new URL("./settings-preview.html", import.meta.url)),
    },
  },
})

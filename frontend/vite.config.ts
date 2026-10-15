import { resolve } from "node:path"
import tailwindcss from "@tailwindcss/vite"
import react from "@vitejs/plugin-react"
import { tanstackRouter } from "@tanstack/router-plugin/vite"
import { defineConfig, loadEnv } from "vite"

export default defineConfig(({ mode }) => {
  const environment = loadEnv(mode, process.cwd(), "")
  return {
    plugins: [
      tanstackRouter({ target: "react", autoCodeSplitting: true }),
      react(),
      tailwindcss(),
    ],
    resolve: {
      alias: { "@": resolve(import.meta.dirname, "./src") },
    },
    server: {
      host: "127.0.0.1",
      proxy: {
        "/api": environment.KARDIA_API_URL || "http://127.0.0.1:5001",
        "/openapi": environment.KARDIA_API_URL || "http://127.0.0.1:5001",
      },
    },
  }
})

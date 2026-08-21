import { defineConfig } from "vite";
import react from "@vitejs/plugin-react";

// Dev proxy so the SPA can call relative /api/* paths without a CORS
// dance in local development; the API itself also has CORS enabled
// (see apps/api/src/main.ts) as a fallback for non-proxied setups.
export default defineConfig({
  plugins: [react()],
  server: {
    port: 5173,
    proxy: {
      "/api": {
        target: "http://localhost:3000",
        changeOrigin: true,
        rewrite: (path) => path.replace(/^\/api/, ""),
      },
    },
  },
});

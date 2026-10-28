import react from '@vitejs/plugin-react'
import { defineConfig } from 'vite'

// The interface is embedded in the desktop executable and served from memory, so every path is relative
// and nothing is fetched from the network.
export default defineConfig({
  plugins: [react()],
  base: './',
  build: {
    outDir: 'dist',
    emptyOutDir: true,
    sourcemap: false,
    assetsInlineLimit: 0,
    chunkSizeWarningLimit: 4000,
  },
  server: { port: 5173, strictPort: true },
})

import { defineConfig } from 'vite'
import react from '@vitejs/plugin-react'
import tailwindcss from '@tailwindcss/vite'
import { fileURLToPath } from 'node:url'
import { dirname, resolve } from 'node:path'
import { vendorChunk } from './vite.chunks.ts'

const root = dirname(fileURLToPath(import.meta.url))

// Built assets land in ExoLauncher/wwwroot and are hosted via WebView2 virtual host.
// Vite 8 + Rolldown. React Compiler runs in Oxc (plugin-react v6), not Babel.
export default defineConfig({
  plugins: [
    react({
      compiler: { target: '19' },
    }),
    tailwindcss(),
  ],
  resolve: {
    alias: {
      '@': resolve(root, 'src'),
      src: resolve(root, 'src'),
    },
  },
  base: './',
  build: {
    manifest: true,
    outDir: resolve(root, '../ExoLauncher/wwwroot'),
    emptyOutDir: true,
    sourcemap: false,
    rolldownOptions: {
      input: {
        main: resolve(root, 'index.html'),
      },
      output: {
        manualChunks(id: string) {
          return vendorChunk(id)
        },
        // Underscore before the hash: a hyphen makes the Windows PRI tool read the hash as a
        // qualifier (PRI249 on min-DWmITN8V.js), and Exo's build must stay warning-free.
        chunkFileNames: 'assets/[name]_[hash].js',
        entryFileNames: 'assets/[name]_[hash].js',
        assetFileNames: 'assets/[name]_[hash][extname]',
      },
    },
  },
  // Workers get the same separator as the rest of the build (see build.rolldownOptions).
  worker: {
    rolldownOptions: {
      output: {
        entryFileNames: 'assets/[name]_[hash].js',
        chunkFileNames: 'assets/[name]_[hash].js',
      },
    },
  },
  server: {
    port: 5174,
    strictPort: true,
    // CHANGELOG.md is at the repo's root, read by the Changelog sheet.
    fs: { allow: ['..'] },
  },
})

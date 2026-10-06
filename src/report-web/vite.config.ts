import { defineConfig } from 'vitest/config'
import { loadEnv } from 'vite'
import react from '@vitejs/plugin-react'
import { resolve } from 'node:path'
import { fileURLToPath } from 'node:url'
import { copyFileSync } from 'node:fs'
import { transformReportTitle } from './build-title.ts'

const rootDirectory = fileURLToPath(new URL('.', import.meta.url))

// https://vite.dev/config/
export default defineConfig(({ mode }) => ({
  plugins: [
    react(),
    {
      name: 'report-provider-title',
      transformIndexHtml(html, context) {
        const provider = process.env.VITE_REPORT_PROVIDER ?? loadEnv(mode, rootDirectory, 'VITE_REPORT_PROVIDER').VITE_REPORT_PROVIDER ?? 'graph'
        return transformReportTitle(html, context.filename, provider)
      },
    },
    {
      name: 'static-web-app-headers',
      closeBundle() {
        copyFileSync(resolve(rootDirectory, 'staticwebapp.config.json'), resolve(rootDirectory, 'dist/staticwebapp.config.json'))
      },
    },
  ],
  build: {
    rollupOptions: {
      input: {
        main: resolve(rootDirectory, 'index.html'),
        redirect: resolve(rootDirectory, 'redirect.html'),
      },
    },
  },
  test: {
    include: ['src/**/*.test.ts'],
    environment: 'node',
    pool: 'threads',
    maxWorkers: 1,
    setupFiles: './src/test-setup.ts',
  },
}))

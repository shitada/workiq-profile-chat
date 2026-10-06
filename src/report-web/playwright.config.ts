import { defineConfig } from '@playwright/test'

export default defineConfig({
  testDir: './tests/playwright',
  timeout: 30_000,
  use: {
    baseURL: 'http://127.0.0.1:4179',
    channel: 'msedge',
    headless: true,
    screenshot: 'only-on-failure',
  },
  webServer: {
    command: 'npm run dev -- --host 127.0.0.1 --port 4179 --strictPort',
    url: 'http://127.0.0.1:4179',
    reuseExistingServer: false,
    env: {
      VITE_TENANT_ID: '00000000-0000-0000-0000-000000000001',
      VITE_SPA_CLIENT_ID: '00000000-0000-0000-0000-000000000002',
      VITE_API_CLIENT_ID: '00000000-0000-0000-0000-000000000003',
      VITE_API_URL: 'https://report-test.invalid',
      VITE_REPORT_PROVIDER: 'graph',
    },
  },
})

export interface RuntimeConfig {
  tenantId: string
  spaClientId: string
  apiClientId: string
  apiUrl: string
}

const values: RuntimeConfig = {
  tenantId: import.meta.env.VITE_TENANT_ID ?? '',
  spaClientId: import.meta.env.VITE_SPA_CLIENT_ID ?? '',
  apiClientId: import.meta.env.VITE_API_CLIENT_ID ?? '',
  apiUrl: (import.meta.env.VITE_API_URL ?? '').replace(/\/$/, ''),
}

const missing = Object.entries(values)
  .filter(([, value]) => !value)
  .map(([key]) => key)

if (missing.length > 0) {
  throw new Error(`Missing runtime configuration: ${missing.join(', ')}`)
}

export const runtimeConfig = values

const provider = import.meta.env.VITE_REPORT_PROVIDER ?? 'graph'
if (provider !== 'graph' && provider !== 'workiq') {
  throw new Error('VITE_REPORT_PROVIDER must be graph or workiq')
}

export const reportProvider: 'graph' | 'workiq' = provider

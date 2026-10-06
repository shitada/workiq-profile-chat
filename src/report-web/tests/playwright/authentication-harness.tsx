import { createRoot } from 'react-dom/client'
import { MsalContext } from '@azure/msal-react'
import { BrowserAuthError, InteractionRequiredAuthError, InteractionStatus, Logger } from '@azure/msal-browser'
import type { AccountInfo, IPublicClientApplication } from '@azure/msal-browser'
import App from '../../src/App'
import '../../src/index.css'

const account: AccountInfo = {
  homeAccountId: 'test-account', tenantId: 'test-tenant', localAccountId: 'test-local',
  username: 'test@example.invalid', environment: 'login.microsoftonline.com', name: 'Test User',
}
const mode = new URLSearchParams(window.location.search).get('mode')
const metrics = { silent: 0, popup: 0, userActivation: false }
Object.assign(window, { authTestMetrics: metrics })
let recovered = false
const instance = {
  getActiveAccount: () => account,
  acquireTokenSilent: async () => {
    metrics.silent++
    if (recovered) return { account, accessToken: 'test-only' }
    if (mode === 'interaction') throw new InteractionRequiredAuthError('interaction_required', 'test')
    throw new BrowserAuthError('timed_out', 'test-only raw diagnostic')
  },
  acquireTokenPopup: () => {
    metrics.popup++
    metrics.userActivation = navigator.userActivation.isActive
    recovered = true
    return Promise.resolve({ account, accessToken: 'test-only' })
  },
} as unknown as IPublicClientApplication

createRoot(document.getElementById('root')!).render(
  <MsalContext.Provider value={{ instance, accounts: [account], inProgress: InteractionStatus.None, logger: new Logger({}) }}>
    <App />
  </MsalContext.Provider>,
)

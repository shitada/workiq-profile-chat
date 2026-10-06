import {
  BrowserCacheLocation,
  PublicClientApplication,
  type Configuration,
} from '@azure/msal-browser'
import { runtimeConfig } from './config'

const msalConfiguration: Configuration = {
  auth: {
    clientId: runtimeConfig.spaClientId,
    authority: `https://login.microsoftonline.com/${runtimeConfig.tenantId}`,
    redirectUri: `${window.location.origin}/redirect.html`,
    postLogoutRedirectUri: window.location.origin,
  },
  cache: {
    cacheLocation: BrowserCacheLocation.SessionStorage,
  },
  system: {
    allowPlatformBroker: false,
  },
}

export const loginRequest = {
  scopes: [`api://${runtimeConfig.apiClientId}/access_as_user`],
}

export const msalInstance = new PublicClientApplication(msalConfiguration)

import { describe, expect, it, vi } from 'vitest'
import { BrowserAuthError, InteractionRequiredAuthError } from '@azure/msal-browser'
import type { AccountInfo } from '@azure/msal-browser'
import {
  acquireSilentApiToken, AuthenticationFailure, classifyAuthenticationFailure,
  configFailureMessage, continueAuthentication, sameAccount,
} from './authentication'
import { createReportApi } from './api'

const account = { homeAccountId: 'test-account', tenantId: 'test-tenant' } as AccountInfo
const request = { scopes: ['api://test/access_as_user'], account }

describe('silent authentication boundary', () => {
  it('returns a silent token without invoking popup', async () => {
    const instance = { acquireTokenSilent: vi.fn().mockResolvedValue({ accessToken: 'test-token' }), acquireTokenPopup: vi.fn() }
    expect(await acquireSilentApiToken(instance, request)).toBe('test-token')
    expect(instance.acquireTokenPopup).not.toHaveBeenCalled()
  })

  it.each([
    [new InteractionRequiredAuthError('interaction_required', 'test'), 'interaction_required'],
    [new BrowserAuthError('timed_out', 'sensitive upstream detail'), 'timeout'],
    [new BrowserAuthError('monitor_window_timeout', 'test'), 'timeout'],
    [new Error('sensitive upstream detail'), 'failed'],
  ])('classifies %s and never starts interactive auth asynchronously', async (problem, expected) => {
    const instance = { acquireTokenSilent: vi.fn().mockRejectedValue(problem), acquireTokenPopup: vi.fn() }
    await expect(acquireSilentApiToken(instance, request)).rejects.toMatchObject({ kind: expected })
    expect(instance.acquireTokenPopup).not.toHaveBeenCalled()
    expect(classifyAuthenticationFailure(problem).message).not.toContain('sensitive upstream detail')
  })

  it('does not infer interaction required from a spoofed errorCode alone', () => {
    expect(classifyAuthenticationFailure({ errorCode: 'interaction_required' }).kind).toBe('failed')
  })

  it('calls popup synchronously on explicit continuation only for InteractionRequiredAuthError', async () => {
    const instance = { acquireTokenPopup: vi.fn().mockResolvedValue({ accessToken: 'test-token', account }) }
    const failure = classifyAuthenticationFailure(new InteractionRequiredAuthError('consent_required', 'test'))
    const result = continueAuthentication(instance, request, failure)
    expect(instance.acquireTokenPopup).toHaveBeenCalledWith(request)
    await expect(result).resolves.toMatchObject({ account })
    for (const issue of [null, new AuthenticationFailure('timeout'), new AuthenticationFailure('failed')]) {
      await expect(continueAuthentication(instance, request, issue)).rejects.toThrow()
    }
    expect(instance.acquireTokenPopup).toHaveBeenCalledTimes(1)
  })

  it('reports timeout as pre-API authentication failure without issuing fetch', async () => {
    const fetch = vi.spyOn(globalThis, 'fetch')
    try {
      const instance = { acquireTokenSilent: vi.fn().mockRejectedValue(new BrowserAuthError('timed_out', 'test')) }
      const api = createReportApi('https://test.invalid', () => acquireSilentApiToken(instance, request))
      await expect(api.config(new AbortController().signal)).rejects.toMatchObject({ kind: 'timeout' })
      expect(fetch).not.toHaveBeenCalled()
      const message = configFailureMessage(new AuthenticationFailure('timeout'))
      expect(message).toContain('API を呼び出す前')
      expect(message).toContain('MSAL timed_out')
      expect(message).toContain('保存は無効')
    } finally { fetch.mockRestore() }
  })

  it('separates API failures from authentication and rejects mismatched recovery accounts', () => {
    expect(configFailureMessage(new Error('network'))).toContain('API の接続')
    expect(sameAccount(account, { ...account })).toBe(true)
    expect(sameAccount(account, { ...account, tenantId: 'other-tenant' })).toBe(false)
    expect(sameAccount(account, { ...account, homeAccountId: 'other-account' })).toBe(false)
    expect(sameAccount(account, null)).toBe(false)
  })
})

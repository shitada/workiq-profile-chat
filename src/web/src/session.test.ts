import { beforeEach, describe, expect, it } from 'vitest'
import {
  clearSession,
  loadMessages,
  loadPendingContinuation,
  saveMessages,
  savePendingContinuation,
} from './session'

describe('browser session state', () => {
  beforeEach(() => {
    sessionStorage.clear()
  })

  it('stores chat messages only in session storage', () => {
    const messages = [{ id: '1', role: 'user' as const, text: 'hello' }]
    saveMessages(messages)
    expect(loadMessages()).toEqual(messages)
  })

  it('removes malformed session data', () => {
    sessionStorage.setItem('workiq-profile-chat.messages.v1', '{broken')
    expect(loadMessages()).toEqual([])
    expect(sessionStorage.getItem('workiq-profile-chat.messages.v1')).toBeNull()
  })

  it('clears messages and pending continuation on sign-out', () => {
    saveMessages([{ id: '1', role: 'assistant', text: 'result' }])
    savePendingContinuation({
      kind: 'oauth',
      responseId: 'resp',
      originalMessage: 'profile',
      consentLink: 'https://example.test',
    })

    clearSession()

    expect(loadMessages()).toEqual([])
    expect(loadPendingContinuation()).toBeNull()
  })

  it('drops consent state from the previous schema version', () => {
    sessionStorage.setItem(
      'workiq-profile-chat.continuation.v1',
      JSON.stringify({
        kind: 'oauth',
        responseId: 'old',
        originalMessage: 'old request',
        consentLink: 'https://example.test',
      }),
    )

    expect(loadPendingContinuation()).toBeNull()
    expect(
      sessionStorage.getItem('workiq-profile-chat.continuation.v1'),
    ).toBeNull()
  })
})

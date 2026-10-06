import { beforeEach, describe, expect, it } from 'vitest'
import { clearEditor, emptyEditor, loadEditor, saveEditor, sessionKey } from './session'

beforeEach(() => sessionStorage.clear())
describe('versioned identity/provider storage', () => {
  it('isolates tenant, account and provider', () => {
    const key = sessionKey('tenant1', 'account1', 'graph')
    saveEditor(key, { ...emptyEditor(), input: '8/1の日報作って', draft: 'edited' })
    expect(loadEditor(key).draft).toBe('edited')
    expect(loadEditor(sessionKey('tenant2', 'account1', 'graph')).draft).toBe('')
    expect(loadEditor(sessionKey('tenant1', 'account2', 'graph')).draft).toBe('')
    expect(loadEditor(sessionKey('tenant1', 'account1', 'workiq')).draft).toBe('')
    clearEditor(key)
    expect(loadEditor(key)).toEqual(emptyEditor())
  })
  it('ignores malformed and old sessions', () => {
    for (const raw of ['bad-json', '{"version":0}', '{"version":1,"period":{}}']) {
      sessionStorage.setItem('key', raw)
      expect(loadEditor('key')).toEqual(emptyEditor())
    }
  })
  it('never serializes continuation/credentials from excess fields', () => {
    saveEditor('key', { ...emptyEditor(), ...{ accessToken: 'secret', continuationToken: 'secret' } })
    expect(sessionStorage.getItem('key')).not.toContain('secret')
    expect(JSON.parse(sessionStorage.getItem('key')!).version).toBe(1)
  })
  it('handles browser storage exceptions', () => {
    const original = sessionStorage.setItem
    sessionStorage.setItem = () => { throw new Error('QuotaExceeded') }
    expect(saveEditor('key', emptyEditor())).toBe(false)
    sessionStorage.setItem = original
  })
})

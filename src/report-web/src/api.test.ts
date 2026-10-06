import { afterEach, describe, expect, it, vi } from 'vitest'
import { createReportApi, parseRetryAfter, ReportApiError } from './api'

afterEach(() => vi.unstubAllGlobals())
describe('report API boundary', () => {
  it.each([true, false, 'true', undefined])('only accepts a boolean restartRequired (%s)', async (restartRequired) => {
    vi.stubGlobal('fetch', vi.fn().mockResolvedValue(new Response(JSON.stringify({
      code: 'foundry_response_incomplete', message: '取得Agentの結果が未完了です',
      details: { restartRequired },
    }), { status: 502 })))
    await expect(createReportApi('', async () => 'test').generate({
      message: '8/1の日報', period: { kind: 'daily', reportDate: '2026-08-01' },
    }, new AbortController().signal)).rejects.toMatchObject({ restartRequired: restartRequired === true })
  })
  it('does not load private tool responses until explicitly requested and authenticates diagnostics', async () => {
    const runId = 'a'.repeat(64)
    const fetch = vi.fn()
      .mockResolvedValueOnce(new Response(JSON.stringify({
        code: 'collector_no_evidence', message: '根拠なし',
        details: { diagnosticRunId: runId, coverage: [] },
      }), { status: 422 }))
      .mockResolvedValueOnce(new Response(JSON.stringify({ kind: 'workiq_tool_diagnostics', replies: [] })))
    vi.stubGlobal('fetch', fetch)
    const acquire = vi.fn().mockResolvedValue('test')
    const api = createReportApi('https://api.example', acquire)
    await expect(api.generate({ message: '8/1の日報', period: { kind: 'daily', reportDate: '2026-08-01' } },
      new AbortController().signal)).rejects.toMatchObject({ diagnosticRunId: runId })
    expect(fetch).toHaveBeenCalledTimes(1)
    await api.diagnostics(runId, new AbortController().signal)
    expect(fetch).toHaveBeenLastCalledWith(`https://api.example/api/reports/diagnostics/${runId}`, expect.objectContaining({ method: 'GET' }))
    expect(acquire).toHaveBeenCalledTimes(2)
  })
  it('rejects invalid diagnostic handles without sending a request', async () => {
    const fetch = vi.fn()
    vi.stubGlobal('fetch', fetch)
    await expect(createReportApi('', async () => 'test').diagnostics('../another-user', new AbortController().signal)).rejects.toThrow()
    expect(fetch).not.toHaveBeenCalled()
  })
  it('authenticates each report request and never sends provider overrides', async () => {
    const fetch = vi.fn().mockResolvedValue(new Response(JSON.stringify({ kind: 'resolved' })))
    vi.stubGlobal('fetch', fetch)
    const acquire = vi.fn().mockResolvedValue('test-only')
    const api = createReportApi('https://api.example/', acquire)
    const signal = new AbortController().signal
    await api.resolve({ message: '8/1の日報作って' }, signal)
    expect(fetch).toHaveBeenCalledWith('https://api.example/api/reports/resolve', expect.objectContaining({
      method: 'POST', headers: { Authorization: 'Bearer test-only', 'Content-Type': 'application/json' },
      body: '{"message":"8/1の日報作って"}', signal,
    }))
    expect(acquire).toHaveBeenCalledTimes(1)
  })
  it('uses GET config without generating or fetching dates', async () => {
    const fetch = vi.fn().mockResolvedValue(new Response('{}'))
    vi.stubGlobal('fetch', fetch)
    await createReportApi('', async () => 'test-only').config(new AbortController().signal)
    expect(fetch).toHaveBeenCalledWith('/api/reports/config', expect.objectContaining({ method: 'GET', body: undefined }))
  })
  it('preserves typed source/folder errors and correlation ID', async () => {
    vi.stubGlobal('fetch', vi.fn().mockResolvedValue(new Response(JSON.stringify({
      code: 'sharepoint_not_configured', message: '保存先未設定', correlationId: 'test-correlation',
    }), { status: 503 })))
    const api = createReportApi('', async () => 'test')
    await expect(api.config(new AbortController().signal)).rejects.toMatchObject({
      name: 'ReportApiError', code: 'sharepoint_not_configured', status: 503,
      message: '保存先未設定 (correlation: test-correlation)',
    } satisfies Partial<ReportApiError>)
  })
  it('never calls fetch after identity/request cancellation during token acquisition', async () => {
    const controller = new AbortController()
    const fetch = vi.fn()
    vi.stubGlobal('fetch', fetch)
    const api = createReportApi('', async () => { controller.abort(); return 'old-account-token' })
    await expect(api.config(controller.signal)).rejects.toThrow()
    expect(fetch).not.toHaveBeenCalled()
  })
  it('does not display raw gateway error bodies', async () => {
    vi.stubGlobal('fetch', vi.fn().mockResolvedValue(new Response('unsafe gateway detail', { status: 502 })))
    await expect(createReportApi('', async () => 'test').config(new AbortController().signal)).rejects.toThrow('HTTP 502')
  })
  it('preserves source diagnostics on a failed generation without turning it into a completed report', async () => {
    const coverage = [{ sourceType: 'chat', status: 'unavailable', count: 0,
      reason: 'collector_returned_no_evidence', diagnostics: { 'collector.askCallsObserved': 1 } }]
    vi.stubGlobal('fetch', vi.fn().mockResolvedValue(new Response(JSON.stringify({
      code: 'collector_no_evidence', message: '根拠が返りませんでした', correlationId: 'diagnostic-test',
      details: { coverage, evidence: [{ text: 'not exposed' }] },
    }), { status: 422 })))
    await expect(createReportApi('', async () => 'test').generate({
      message: '8/1の日報', period: { kind: 'daily', reportDate: '2026-08-01' },
    }, new AbortController().signal)).rejects.toMatchObject({
      code: 'collector_no_evidence', status: 422, coverage,
    })
  })
  it('ignores malformed diagnostic collections', async () => {
    vi.stubGlobal('fetch', vi.fn().mockResolvedValue(new Response(JSON.stringify({
      message: 'failed', details: { coverage: 'invalid' },
    }), { status: 422 })))
    await expect(createReportApi('', async () => 'test').config(new AbortController().signal))
      .rejects.toMatchObject({ coverage: undefined })
  })
  it('preserves only safe nested service failure diagnostics', async () => {
    vi.stubGlobal('fetch', vi.fn().mockResolvedValue(new Response(JSON.stringify({
      message: '通信に失敗しました', code: 'collector_request_transport',
      diagnostic: { stage: 'collector_request', category: 'transport', token: 'private',
        causes: [{ type: 'AggregateException', message: 'private' },
          { type: 'HttpRequestException', httpStatus: 0, errorCode: 'invalid code with token' }] },
    }), { status: 502 })))
    try {
      await createReportApi('', async () => 'test').config(new AbortController().signal)
      throw new Error('Expected rejection')
    } catch (error) {
      expect(error).toBeInstanceOf(ReportApiError)
      expect((error as ReportApiError).diagnostic).toEqual({
        stage: 'collector_request', category: 'transport',
        causes: [{ type: 'AggregateException' }, { type: 'HttpRequestException', httpStatus: 0 }],
      })
    }
  })
  it('honors the longer JSON or HTTP Retry-After delay', async () => {
    vi.stubGlobal('fetch', vi.fn().mockResolvedValue(new Response(JSON.stringify({
      kind: 'progress', continuationToken: 'next', message: 'throttled', retryAfterSeconds: 2,
    }), { headers: { 'Retry-After': '5' } })))
    const result = await createReportApi('', async () => 'test').generate({
      message: 'daily', period: { kind: 'daily', reportDate: '2026-08-01' },
    }, new AbortController().signal)
    expect(result).toMatchObject({ retryAfterSeconds: 5 })
    expect(parseRetryAfter('invalid')).toBeUndefined()
    expect(parseRetryAfter('')).toBeUndefined()
  })
})

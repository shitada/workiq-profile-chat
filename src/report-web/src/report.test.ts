import { afterEach, describe, expect, it, vi } from 'vitest'
import {
  comparisonExport, comparisonMetrics, generateBounded, MAX_PROGRESS_STEPS, MAX_AUTOMATIC_POLLS, parseMembers,
  periodError, safeConsentUrl, safeReferenceUrl, waitForRetry,
} from './report'
import type { CompletedReport, GenerateRequest } from './types'

const request: GenerateRequest = {
  message: '8/1の日報作って', period: { kind: 'daily', reportDate: '2026-08-01' },
}
const completed: CompletedReport = {
  kind: 'completed', runId: 'run-1', text: '日報', period: request.period,
  coverage: [], evidence: [], metrics: {},
}
afterEach(() => vi.useRealTimers())

describe('safe native links', () => {
  it.each(['javascript:alert(1)', 'data:text/html,test', 'http://example.com', '//example.com', 'https://user:pass@example.com', 'not a url'])('rejects %s', (value) => {
    expect(safeReferenceUrl(value)).toBeNull()
  })
  it('allows HTTPS source references', () => {
    expect(safeReferenceUrl('https://example.sharepoint.com/sites/reports')).toBe('https://example.sharepoint.com/sites/reports')
  })
  it.each([
    'https://fake.cons ent.azure-apim.net/login',
    'https://consent.azure-apim.net/login',
    'https://x.consent.azure-apim.net.evil.example/login',
    'https://evil.example/login',
    'https://x.consent.azure-apim.net:444/login',
    'https://user:pass@x.consent.azure-apim.net/login',
    'https://x.consent.azure-apim.net/not-login',
  ])('blocks unexpected consent host/path %s', (value) => expect(safeConsentUrl(value)).toBeNull())
  it('preserves consent query exactly via native anchor URL', () => {
    expect(safeConsentUrl('https://sample.consent.azure-apim.net/login?state=a%2Fb')).toBe('https://sample.consent.azure-apim.net/login?state=a%2Fb')
    expect(safeConsentUrl('https://sample.consent.azure-apihub.net/login')).not.toBeNull()
  })
})

describe('explicit periods and members', () => {
  it('does not substitute today or move weekend dates', () => {
    expect(periodError({ kind: 'daily' })).toBeTruthy()
    expect(periodError(request.period)).toBeNull()
    expect(periodError({ kind: 'daily', reportDate: '2026-02-30' })).toBeTruthy()
  })
  it('rejects missing/reversed weekly range', () => {
    expect(periodError({ kind: 'personalWeekly' })).toBeTruthy()
    expect(periodError({ kind: 'personalWeekly', startDate: '2026-08-07', endDate: '2026-08-03' })).toBeTruthy()
    expect(periodError({ kind: 'managerWeekly', startDate: '2026-08-03', endDate: '2026-08-07' })).toBeNull()
  })
  it('uses only explicit unique member input', () => {
    expect(parseMembers('a@example.com, b@example.com\na@example.com; user-id')).toEqual(['a@example.com', 'b@example.com', 'user-id'])
  })
})

describe('bounded continuation', () => {
  it('automatically waits out a persisted token-rate window and resumes the same run', async () => {
    vi.useFakeTimers()
    const send = vi.fn()
      .mockResolvedValueOnce({ kind: 'progress', continuationToken: 'owned-run', automaticPolling: true, retryAfterSeconds: 65, message: 'レート制限のため自動待機中' })
      .mockResolvedValueOnce(completed)
    const result = generateBounded(send, request, new AbortController().signal, vi.fn())
    await vi.advanceTimersByTimeAsync(64999)
    expect(send).toHaveBeenCalledTimes(1)
    await vi.advanceTimersByTimeAsync(1)
    expect(await result).toEqual(completed)
    expect(send).toHaveBeenCalledTimes(2)
    expect(send.mock.calls[1][0]).toMatchObject({ continuationToken: 'owned-run', period: request.period })
  })
  it('automatically follows a background response beyond the normal page batch without replaying approval', async () => {
    const send = vi.fn().mockImplementation(async () => send.mock.calls.length <= 12
      ? { kind: 'progress', continuationToken: 'same-run', message: '処理中', retryAfterSeconds: 0, automaticPolling: true }
      : completed)
    const result = await generateBounded(send, { ...request, approval: { requestId: 'once', approved: true } },
      new AbortController().signal, vi.fn())
    expect(result).toEqual(completed)
    expect(send).toHaveBeenCalledTimes(13)
    for (const [body] of send.mock.calls.slice(1)) {
      expect(body.continuationToken).toBe('same-run')
      expect(body.approval).toBeUndefined()
    }
  })
  it('bounds background polling without silently starting another run', async () => {
    const send = vi.fn().mockResolvedValue({
      kind: 'progress', continuationToken: 'same-run', message: '処理中', retryAfterSeconds: 0, automaticPolling: true,
    })
    const result = await generateBounded(send, request, new AbortController().signal, vi.fn())
    expect(send).toHaveBeenCalledTimes(MAX_AUTOMATIC_POLLS)
    expect(result).toMatchObject({ continuationToken: 'same-run' })
    expect(send.mock.calls.slice(1).every(([body]) => body.continuationToken === 'same-run')).toBe(true)
  })
  it('stops after a bounded page batch and leaves continuation for a user button', async () => {
    const send = vi.fn().mockImplementation(async () => ({ kind: 'progress', continuationToken: `c${send.mock.calls.length}`, message: 'progress', retryAfterSeconds: 0 }))
    const onProgress = vi.fn()
    const response = await generateBounded(send, request, new AbortController().signal, onProgress)
    expect(send).toHaveBeenCalledTimes(MAX_PROGRESS_STEPS)
    expect(onProgress).toHaveBeenCalledTimes(MAX_PROGRESS_STEPS)
    expect(response).toMatchObject({ continuationToken: `c${MAX_PROGRESS_STEPS}` })
    expect(send.mock.calls[1][0]).toMatchObject({ ...request, continuationToken: 'c1' })
  })
  it('carries draft but consumes approval once', async () => {
    const send = vi.fn().mockResolvedValueOnce({ kind: 'progress', continuationToken: 'next', message: 'page', retryAfterSeconds: 0 }).mockResolvedValueOnce(completed)
    await generateBounded(send, { ...request, draft: 'edited', approval: { requestId: 'a', approved: true } }, new AbortController().signal, vi.fn())
    expect(send.mock.calls[0][0].approval.approved).toBe(true)
    expect(send.mock.calls[1][0]).toMatchObject({ draft: 'edited', continuationToken: 'next' })
    expect(send.mock.calls[1][0]).not.toHaveProperty('approval')
  })
  it.each(['oauth_consent_required', 'tool_approval_required', 'partial_confirmation'])('never auto-approves %s', async (kind) => {
    const send = vi.fn().mockResolvedValue({ kind, continuationToken: 'pending' })
    expect(await generateBounded(send, request, new AbortController().signal, vi.fn())).toMatchObject({ kind })
    expect(send).toHaveBeenCalledTimes(1)
  })
  it('aborts before another page and discards late results', async () => {
    const controller = new AbortController()
    const send = vi.fn().mockImplementation(async () => {
      controller.abort()
      return completed
    })
    await expect(generateBounded(send, request, controller.signal, vi.fn())).rejects.toThrow()
    expect(send).toHaveBeenCalledTimes(1)
  })
  it('honors progress retry delay before the next request', async () => {
    vi.useFakeTimers()
    const send = vi.fn().mockResolvedValueOnce({ kind: 'progress', continuationToken: 'next', message: 'throttled', retryAfterSeconds: 5 }).mockResolvedValueOnce(completed)
    const result = generateBounded(send, request, new AbortController().signal, vi.fn())
    await vi.advanceTimersByTimeAsync(4999)
    expect(send).toHaveBeenCalledTimes(1)
    await vi.advanceTimersByTimeAsync(1)
    expect(await result).toEqual(completed)
    expect(send).toHaveBeenCalledTimes(2)
  })
  it('defaults to one second and aborts the wait without retrying', async () => {
    vi.useFakeTimers()
    const controller = new AbortController()
    const wait = waitForRetry(undefined, controller.signal)
    const rejected = expect(wait).rejects.toThrow()
    await vi.advanceTimersByTimeAsync(999)
    controller.abort()
    await rejected
    expect(vi.getTimerCount()).toBe(0)
  })
})

describe('comparison metadata', () => {
  it('never exports the original reply diagnostic handle or added response bodies', () => {
    const reportWithPrivateReplies = {
      ...completed, diagnosticRunId: 'private-diagnostic-handle',
      workIqReplies: [{ rawText: 'private-raw-response', text: 'private-answer' }],
    }
    const result = comparisonExport(reportWithPrivateReplies, 'workiq', null)
    expect(JSON.stringify(result)).not.toContain('private-')
    expect(result).not.toHaveProperty('diagnosticRunId')
    expect(result).not.toHaveProperty('workIqReplies')
  })
  it('exports all exact backend hash/version fields and dynamic source status/count maps', () => {
    const metrics = {
      promptHash: 'prompt-hash', generationConfigHash: 'generation-config-hash',
      schemaHash: 'schema-hash', evidenceSchemaHash: 'evidence-schema-hash',
      generationSpecVersion: 'report-generation-v1', modelDeployment: 'test-model',
      modelVersion: 'test-version', agentVersion: '3', collectorAgentVersion: '2',
      sourceStatus: { calendar: 'complete', chat: 'partial', channel: 'unavailable', transcript: 'partial', sharePoint: 'complete' },
      sourceCounts: { calendar: 3, chat: 2, channel: 0, transcript: 1, sharePoint: 4 },
      inputTokens: 30, outputTokens: 10, tokenUsageAvailable: true, edited: false,
    }
    expect(comparisonMetrics(metrics)).toEqual(metrics)
    expect(comparisonExport({ ...completed, metrics }, 'workiq', null).metrics).toEqual(metrics)
  })

  it('accepts only sourceStatus enum values without widening arbitrary string export', () => {
    expect(comparisonMetrics({
      sourceStatus: {
        calendar: 'complete', chat: 'partial', channel: 'unavailable',
        futureSource: 'complete', bad: 'Bearer secret', unrecognized: 'success',
        numeric: 1, nested: { accessToken: 'secret' }, authorization: 'complete',
      },
      arbitrary: 'complete',
    })).toEqual({
      sourceStatus: { calendar: 'complete', chat: 'partial', channel: 'unavailable', futureSource: 'complete' },
    })
    for (const sourceStatus of ['complete', ['complete'], 1, null]) {
      expect(comparisonMetrics({ sourceStatus })).toEqual({})
    }
  })

  it('does not allow credential-looking values through the new metadata fields', () => {
    expect(comparisonMetrics({
      schemaHash: 'Bearer private-token', evidenceSchemaHash: 'eyJhbGciOi.test.signature',
      generationSpecVersion: 'Bearer private-token', generationConfigHash: 'safe-hash',
    })).toEqual({ generationConfigHash: 'safe-hash' })
  })

  it('keeps token usage numbers, known hashes and model version but no token values or draft', () => {
    const metrics = comparisonMetrics({
      promptHash: 'hash', modelVersion: '2025-04-14', inputTokens: 20, outputTokens: 5,
      accessToken: 'secret', continuationToken: 'continuation', arbitrary: 'private data',
      nested: { refreshToken: 'secret', retrievalMs: 200 }, modelDeployment: 'Bearer secret',
    })
    expect(metrics).toEqual({
      promptHash: 'hash', modelVersion: '2025-04-14', inputTokens: 20, outputTokens: 5, nested: { retrievalMs: 200 },
    })
    const exported = comparisonExport({ ...completed, metrics, text: 'private draft' }, 'graph', null)
    expect(JSON.stringify(exported)).not.toContain('private draft')
    expect(exported.humanQualityReview).toBe('not_performed')
  })
})

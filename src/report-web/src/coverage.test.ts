import { describe, expect, it } from 'vitest'
import { acquisitionPathLabel, collectionLabel, counterLabel, explainCoverage, exportCoverage, inputLabel, safeCoverageErrors, safeDiagnostics, workIqAcquisitionCounters, workIqToolLabel } from './coverage'
import { comparisonExport, comparisonMetrics } from './report'
import type { CompletedReport, Coverage } from './types'

describe('coverage semantics', () => {
  it('labels structured tools without exposing unrecognized tool metadata', () => {
    expect(workIqToolLabel()).toBe('ask')
    expect(workIqToolLabel('fetch')).toBe('fetch')
    expect(workIqToolLabel('call_function')).toBe('call_function')
    expect(workIqToolLabel('https://private.example/secret')).toBe('Work IQツール')
  })
  it('shows reported acquisition and exclusion counters, without inventing legacy zero values', () => {
    const source: Coverage = { sourceType: 'chat', status: 'partial', count: 3, diagnostics: {
      'workiq.ask': 1, 'workiq.fetch': 2, 'workiq.excludedSystem': 1,
      'workiq.outOfPeriod': 0, 'workiq.fallback': -1,
    } }
    expect(workIqAcquisitionCounters(source)).toEqual([
      { label: 'ask承認予約', count: 1 }, { label: 'fetch承認予約', count: 2 },
      { label: 'システムイベント除外', count: 1 }, { label: '期間外の根拠除外', count: 0 },
    ])
    expect(workIqAcquisitionCounters({ sourceType: 'chat', status: 'complete', count: 0 })).toEqual([])
  })
  it('distinguishes structured verification from an ask-only result without exporting unknown paths', () => {
    expect(acquisitionPathLabel('ask→fetch (structured fallback; ask match unverified)')).toContain('askによる取得成功ではありません')
    expect(acquisitionPathLabel('https://private.example/secret')).toBe('取得経路の詳細は未確認')
    expect(acquisitionPathLabel()).toBeNull()
  })
  it.each([undefined, null, -1, NaN, Infinity, 0.5, '0'])('shows unknown counter %s as unknown, never zero', (value) => {
    expect(counterLabel(value)).toBe('不明')
  })
  it('shows explicitly reported zero and distinguishes fetched/adopted', () => {
    expect(counterLabel(0)).toBe('0')
    const source: Coverage = { sourceType: 'chat', status: 'complete', count: 0, fetchedCount: 0, pages: 1 }
    expect(explainCoverage(source)[0].category).toBe('取得成功・0件')
    expect(exportCoverage(source)).toMatchObject({ sourceType: 'chat', status: 'complete', count: 0, fetchedCount: 0, pages: 1 })
  })
  it('does not infer successful zero or denial from adopted zero and unknown fetch count', () => {
    const source: Coverage = { sourceType: 'chat', status: 'complete', count: 0 }
    expect(explainCoverage(source).map((item) => item.category)).toEqual(['取得件数不明'])
    expect(exportCoverage(source).fetchedCount).toBeNull()
    expect(exportCoverage(source).pages).toBeNull()
  })
  it('keeps input limit and unexplored channel reasons separate from access denial', () => {
    const source: Coverage = {
      sourceType: 'channel', status: 'partial', count: 17, fetchedCount: 23, pages: 4,
      reason: 'shared_channels_not_discovered;input_budget_exhausted',
    }
    expect(explainCoverage(source).map((item) => item.category)).toEqual(['未探索', '入力上限'])
  })
  it('does not turn historical transcript ambiguity into missing data or forbidden', () => {
    const source: Coverage = { sourceType: 'transcript', status: 'unavailable', count: 0, reason: 'historical_transcript_missing_or_no_accessible_recordings' }
    const explanations = explainCoverage(source)
    expect(explanations.map((item) => item.category)).toEqual(['取得件数不明', '原因未確定'])
    expect(explanations.at(-1)?.description).toContain('区別できません')
  })
  it('does not report successful zero for an incomplete source', () => {
    expect(explainCoverage({ sourceType: 'chat', status: 'partial', count: 0, fetchedCount: 0 }).map((item) => item.category)).not.toContain('取得成功・0件')
  })
  it('handles unknown reasons without inventing failure categories', () => {
    expect(explainCoverage({ sourceType: 'chat', status: 'unavailable', count: 0, reason: 'new_reason_code' }).at(-1)?.category).toBe('診断あり')
  })
  it('exports adopted/fetched/pages independently and retains unknown as null', () => {
    const report: CompletedReport = {
      kind: 'completed', runId: 'test', text: 'test', period: { kind: 'daily', reportDate: '2026-08-01' },
      evidence: [], metrics: {}, coverage: [{ sourceType: 'channel', status: 'partial', count: 17, fetchedCount: 23 }],
    }
    expect(comparisonExport(report, 'graph', null).coverage[0]).toMatchObject({
      sourceType: 'channel', status: 'partial', count: 17, fetchedCount: 23, pages: null,
    })
  })
  describe('additive diagnostics', () => {
    it('preserves actual structured denied errors and per-stage numeric diagnostics', () => {
      const source: Coverage = {
        sourceType: 'transcript', status: 'unavailable', count: 0, fetchedCount: 0, pages: 2,
        collectionStatus: 'unavailable', inputStatus: 'complete', retrievedCount: 0, sentCount: 0,
        truncatedCount: 0, inputDroppedCount: 0, reason: 'meetings:graph_http_403',
        diagnostics: { 'meetings.requests': 1, 'meetings.http_403': 1, 'activityCalendar.joinUrlsFound': 2 },
        errors: [{ stage: 'meetings', httpStatus: 403, graphCode: 'Forbidden', count: 1 }],
      }
      expect(explainCoverage(source).some((item) => item.category === 'アクセス拒否')).toBe(true)
      expect(exportCoverage(source)).toMatchObject({
        collectionStatus: 'unavailable', inputStatus: 'complete', retrievedCount: 0, sentCount: 0,
        diagnostics: source.diagnostics, errors: source.errors,
      })
      expect(collectionLabel(source.collectionStatus)).toBe('取得不可')
      expect(inputLabel(source.inputStatus)).toBe('削減なし')
    })
    it('does not invent new counts or split statuses from legacy coverage', () => {
      const result = exportCoverage({ sourceType: 'channel', status: 'partial', count: 17 })
      expect(result).toMatchObject({
        collectionStatus: null, inputStatus: null, retrievedCount: null, sentCount: null,
        truncatedCount: null, inputDroppedCount: null,
      })
      expect(collectionLabel(undefined)).toBe('不明')
      expect(inputLabel(undefined)).toBe('不明')
    })
    it.each([
      ['no_accessible_chats_returned', '一覧取得・0件'],
      ['no_messages_returned_for_query', '照会結果・0件'],
      ['no_messages_in_activity_period', '対象期間・0件'],
      ['no_usable_message_content', '利用可能な本文・0件'],
      ['no_online_meeting_join_urls_in_activity_calendar', '会議URL未検出'],
      ['calendar_incomplete_no_online_meeting_join_urls', '予定取得未完了'],
      ['online_meeting_lookup_empty', '会議照会・0件'],
      ['transcript_list_empty_recording_or_retention_unknown', '文字起こし一覧・0件'],
      ['transcript_content_empty', '文字起こし本文・空'],
      ['no_transcripts_in_activity_period', '対象期間・0件'],
    ])('explains %s without guessing access denial', (reason, category) => {
      const result = explainCoverage({ sourceType: 'test', status: 'complete', count: 0, fetchedCount: 0, reason })
      expect(result.some((item) => item.category === category)).toBe(true)
      expect(result.some((item) => item.category === 'アクセス拒否')).toBe(false)
    })
    it('drops unsafe diagnostic strings, URLs and raw response content from export', () => {
      expect(safeDiagnostics({ 'messages.items': 3, password: 'secret', invalid: -1, 'https://example.com': 1 })).toEqual({ 'messages.items': 3 })
      expect(safeCoverageErrors([
        { stage: 'meetings', httpStatus: 403, graphCode: 'Forbidden', count: 1, accessToken: 'secret', body: 'sensitive' },
        { stage: 'https://example.com/private', httpStatus: 403, count: 1 },
        { stage: 'transcripts', httpStatus: 404, graphCode: 'Bearer secret', count: 2 },
        { stage: 'meetings', httpStatus: 200, count: 1 },
      ])).toEqual([
        { stage: 'meetings', httpStatus: 403, graphCode: 'Forbidden', count: 1 },
        { stage: 'transcripts', httpStatus: 404, count: 2 },
      ])
    })
    it('exports the exact new input-selection version and counters', () => {
      const metrics = { inputSelectionVersion: 'balanced-excerpts-v2-o200k', inputTokenEstimate: 456, stagedTokens: 800, truncatedEvidence: 3 }
      expect(comparisonMetrics(metrics)).toEqual(metrics)
    })
  })
})

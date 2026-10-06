import type { CompletedReport, GenerateRequest, GenerateResponse, Period, Provider, ReportConfig } from './types'
import { exportCoverage } from './coverage'

export function safeReferenceUrl(value?: string): string | null {
  if (!value) return null
  try {
    const url = new URL(value)
    return url.protocol === 'https:' && !url.username && !url.password ? url.href : null
  } catch { return null }
}

export function safeConsentUrl(value: string): string | null {
  const safe = safeReferenceUrl(value)
  if (!safe) return null
  const url = new URL(safe)
  return url.pathname === '/login' && !url.port &&
    ['.consent.azure-apim.net', '.consent.azure-apihub.net'].some((suffix) => url.hostname.endsWith(suffix))
    ? safe : null
}

function validDate(value?: string): boolean {
  if (!value || !/^\d{4}-\d{2}-\d{2}$/.test(value)) return false
  const parsed = new Date(`${value}T00:00:00Z`)
  return !Number.isNaN(parsed.valueOf()) && parsed.toISOString().slice(0, 10) === value
}

export function periodError(period: Period): string | null {
  if (period.kind === 'daily') return validDate(period.reportDate) ? null : '日報日を指定してください。'
  if (!validDate(period.startDate) || !validDate(period.endDate)) return '開始日・終了日を指定してください。'
  return period.startDate! > period.endDate! ? '終了日は開始日以降にしてください。' : null
}

export function periodLabel(period: Period): string {
  return period.kind === 'daily'
    ? `日報日 ${period.reportDate ?? '未指定'}${period.previousBusinessDate ? ` ／ 実績 ${period.previousBusinessDate}` : ''}`
    : `${period.kind === 'managerWeekly' ? '上長週報' : '本人週報'} ${period.startDate ?? '未指定'} 〜 ${period.endDate ?? '未指定'}`
}

export function parseMembers(value: string): string[] {
  return [...new Set(value.split(/[\s,;、]+/).map((member) => member.trim()).filter(Boolean))]
}

export const MAX_PROGRESS_STEPS = 8
export const MAX_AUTOMATIC_POLLS = 220
export const MAX_AUTOMATIC_WAIT_MS = 12 * 60 * 1000

export function waitForRetry(seconds: number | undefined, signal: AbortSignal): Promise<void> {
  signal.throwIfAborted()
  const delay = typeof seconds === 'number' && Number.isFinite(seconds) && seconds >= 0 ? seconds : 1
  if (delay * 1000 > 2_147_483_647) throw new Error('Retry-After が長すぎるため自動再開できません。時間をおいて新しい依頼から開始してください。')
  return new Promise((resolve, reject) => {
    const timer = setTimeout(() => {
      signal.removeEventListener('abort', abort)
      resolve()
    }, delay * 1000)
    function abort() {
      clearTimeout(timer)
      signal.removeEventListener('abort', abort)
      reject(signal.reason)
    }
    signal.addEventListener('abort', abort, { once: true })
  })
}

export async function generateBounded(
  send: (body: GenerateRequest, signal: AbortSignal) => Promise<GenerateResponse>,
  initial: GenerateRequest,
  signal: AbortSignal,
  onProgress: (response: Extract<GenerateResponse, { kind: 'progress' }>) => void,
): Promise<GenerateResponse> {
  let request = initial
  let result: GenerateResponse
  let count = 0
  let polls = 0
  const started = Date.now()
  do {
    signal.throwIfAborted()
    result = await send(request, signal)
    signal.throwIfAborted()
    if (result.kind !== 'progress') return result
    onProgress(result)
    // Approval is a one-time decision; never replay it on later continuation pages.
    request = {
      message: initial.message,
      period: initial.period,
      members: initial.members,
      draft: initial.draft,
      continuationToken: result.continuationToken,
      ...(initial.allowPartial ? { allowPartial: true } : {}),
    }
    if (result.automaticPolling === true) polls++
    else count++
    if (count >= MAX_PROGRESS_STEPS || polls >= MAX_AUTOMATIC_POLLS ||
        Date.now() - started >= MAX_AUTOMATIC_WAIT_MS) return result
    await waitForRetry(result.retryAfterSeconds, signal)
  } while (true)
}

const metadataStrings = new Set([
  'provider', 'method', 'runId', 'promptHash', 'instructionsHash', 'schemaVersion',
  'generationConfigHash', 'schemaHash', 'evidenceSchemaHash', 'generationSpecVersion',
  'generationConfigVersion', 'inputSelectionVersion', 'modelDeployment', 'modelVersion',
  'agentName', 'agentVersion', 'collectorAgentName', 'collectorAgentVersion',
  'startedAt', 'completedAt', 'sourceType', 'status', 'timeZone', 'benchmarkProfile',
  'benchmarkProfileVersion', 'retrievalMode', 'generationMode',
  'acquisitionProfile', 'taskProfileHash', 'collectorInstructionsHash',
])
const secretKeys = /^(access.?token|refresh.?token|id.?token|continuation.?token|authorization|cookie|secret|password|credential|consentLink|toolArguments)$/i
const sourceStatuses = new Set(['complete', 'partial', 'unavailable'])

// Export metric counters and known comparison metadata, never arbitrary response strings or credentials.
export function comparisonMetrics(input: Record<string, unknown>): Record<string, unknown> {
  const result: Record<string, unknown> = {}
  for (const [key, value] of Object.entries(input)) {
    if (secretKeys.test(key)) continue
    if (key === 'sourceStatus') {
      if (value && typeof value === 'object' && !Array.isArray(value)) {
        result[key] = Object.fromEntries(Object.entries(value).filter(([source, status]) =>
          !secretKeys.test(source) && typeof status === 'string' && sourceStatuses.has(status),
        ))
      }
      continue
    }
    if (typeof value === 'number' && Number.isFinite(value) || typeof value === 'boolean') result[key] = value
    else if (typeof value === 'string' && metadataStrings.has(key) && !/Bearer\s|eyJ[\w-]+\.[\w-]+\./i.test(value)) result[key] = value
    else if (value && typeof value === 'object' && !Array.isArray(value)) result[key] = comparisonMetrics(value as Record<string, unknown>)
    else if (Array.isArray(value)) result[key] = value
      .filter((item) => item && typeof item === 'object' && !Array.isArray(item))
      .map((item) => comparisonMetrics(item as Record<string, unknown>))
  }
  return result
}

export function comparisonExport(report: CompletedReport, provider: Provider, config: ReportConfig | null) {
  return {
    schemaVersion: 1,
    provider,
    runId: report.runId,
    period: report.period,
    configuration: config ? {
      provider: config.provider, testYear: config.testYear, timeZone: config.timeZone,
      promptHash: config.promptHash, modelDeployment: config.modelDeployment, modelVersion: config.modelVersion,
      acquisitionProfile: config.acquisitionProfile === 'verified-workiq-v1' || config.acquisitionProfile === 'legacy-ask-v1'
        ? config.acquisitionProfile : null,
    } : null,
    coverage: report.coverage.map(exportCoverage),
    metrics: comparisonMetrics(report.metrics),
    humanQualityReview: 'not_performed',
  }
}

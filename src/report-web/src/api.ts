import type { Coverage, ReportApi } from './types'

export interface ServiceDiagnostic {
  stage: string
  category: string
  causes: { type: string; httpStatus?: number; errorCode?: string }[]
}

const diagnosticIdentifier = /^[A-Za-z][A-Za-z0-9_.-]{0,79}$/
function serviceDiagnostic(value: unknown): ServiceDiagnostic | undefined {
  if (!value || typeof value !== 'object' || !('stage' in value) || !('category' in value) || !('causes' in value)) return undefined
  if (typeof value.stage !== 'string' || !diagnosticIdentifier.test(value.stage) ||
      typeof value.category !== 'string' || !diagnosticIdentifier.test(value.category) || !Array.isArray(value.causes)) return undefined
  return {
    stage: value.stage, category: value.category,
    causes: value.causes.slice(0, 12).filter((cause) => cause && typeof cause.type === 'string' && diagnosticIdentifier.test(cause.type)).map((cause) => ({
      type: cause.type,
      ...(Number.isInteger(cause.httpStatus) && cause.httpStatus >= 0 && cause.httpStatus <= 599 ? { httpStatus: cause.httpStatus } : {}),
      ...(typeof cause.errorCode === 'string' && diagnosticIdentifier.test(cause.errorCode) ? { errorCode: cause.errorCode } : {}),
    })),
  }
}

function failureCoverage(value: unknown): Coverage[] | undefined {
  if (!Array.isArray(value) || value.length > 16) return undefined
  return value.filter((source): source is Coverage =>
    source !== null && typeof source === 'object' &&
    typeof source.sourceType === 'string' &&
    ['complete', 'partial', 'unavailable'].includes(source.status) &&
    Number.isSafeInteger(source.count) && source.count >= 0,
  )
}

export function parseRetryAfter(value: string | null): number | undefined {
  if (!value?.trim()) return undefined
  if (/^\d+$/.test(value.trim())) return Number(value)
  const date = Date.parse(value)
  return Number.isFinite(date) ? Math.max(0, Math.ceil((date - Date.now()) / 1000)) : undefined
}

export class ReportApiError extends Error {
  readonly code: string
  readonly status: number
  readonly coverage?: Coverage[]
  readonly diagnostic?: ServiceDiagnostic
  readonly diagnosticRunId?: string
  readonly restartRequired: boolean
  constructor(status: number, code: string, message: string, correlationId?: string, coverage?: Coverage[], diagnostic?: ServiceDiagnostic, diagnosticRunId?: string, restartRequired = false) {
    super(`${message}${correlationId ? ` (correlation: ${correlationId})` : ''}`)
    this.name = 'ReportApiError'
    this.code = code
    this.status = status
    this.coverage = coverage
    this.diagnostic = diagnostic
    this.diagnosticRunId = diagnosticRunId
    this.restartRequired = restartRequired
  }
}

export function createReportApi(
  apiUrl: string,
  acquireToken: () => Promise<string>,
): ReportApi {
  async function request<T>(path: string, signal: AbortSignal, body?: unknown): Promise<T> {
    signal.throwIfAborted()
    const token = await acquireToken()
    signal.throwIfAborted()
    const response = await fetch(`${apiUrl.replace(/\/$/, '')}/api/reports/${path}`, {
      method: body === undefined ? 'GET' : 'POST',
      headers: {
        Authorization: `Bearer ${token}`,
        ...(body === undefined ? {} : { 'Content-Type': 'application/json' }),
      },
      body: body === undefined ? undefined : JSON.stringify(body),
      signal,
    })
    if (!response.ok) {
      let detail: { code?: string; message?: string; correlationId?: string; details?: { coverage?: unknown; diagnosticRunId?: unknown; restartRequired?: unknown }; diagnostic?: unknown } = {}
      try { detail = await response.json() } catch { /* Non-JSON gateway errors have no safe detail. */ }
      throw new ReportApiError(
        response.status,
        detail.code ?? 'request_failed',
        detail.message ?? `API request failed (HTTP ${response.status}).`,
        detail.correlationId,
        failureCoverage(detail.details?.coverage),
        serviceDiagnostic(detail.diagnostic),
        typeof detail.details?.diagnosticRunId === 'string' && /^[a-f0-9]{64}$/.test(detail.details.diagnosticRunId)
          ? detail.details.diagnosticRunId : undefined,
        detail.details?.restartRequired === true,
      )
    }
    const data = await response.json()
    if (data?.kind === 'progress') {
      const headerDelay = parseRetryAfter(response.headers.get('Retry-After'))
      if (headerDelay !== undefined) {
        const bodyDelay = typeof data.retryAfterSeconds === 'number' && Number.isFinite(data.retryAfterSeconds)
          ? Math.max(0, data.retryAfterSeconds) : 0
        data.retryAfterSeconds = Math.max(bodyDelay, headerDelay)
      }
    }
    return data as T
  }
  return {
    config: (signal) => request('config', signal),
    resolve: (body, signal) => request('resolve', signal, body),
    generate: (body, signal) => request('generate', signal, body),
    save: (body, signal) => request('save', signal, body),
    diagnostics: (runId, signal) => {
      if (!/^[a-f0-9]{64}$/.test(runId)) return Promise.reject(new Error('診断の実行IDが不正です。'))
      return request(`diagnostics/${encodeURIComponent(runId)}`, signal)
    },
  }
}

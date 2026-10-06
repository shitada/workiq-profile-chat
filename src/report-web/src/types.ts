export type Provider = 'graph' | 'workiq'
export type ReportKind = 'daily' | 'personalWeekly' | 'managerWeekly'

export interface Period {
  kind: ReportKind
  reportDate?: string
  startDate?: string
  endDate?: string
  previousBusinessDate?: string
}

export interface ReportConfig {
  provider: Provider
  testYear: number
  timeZone: string
  promptHash: string
  modelDeployment: string
  modelVersion: string
  sharePointConfigured: boolean
  acquisitionProfile?: string
}

export interface ResolveRequest {
  message: string
  period?: Period
  members?: string[]
}

export interface ResolveResponse {
  kind: 'resolved' | 'needs_confirmation'
  message: string
  period?: Period
  choices?: { label: string; period: Period }[]
}

export interface GenerateRequest extends ResolveRequest {
  period: Period
  continuationToken?: string
  approval?: { requestId: string; approved: boolean }
  draft?: string
  allowPartial?: boolean
}

export interface Coverage {
  sourceType: string
  status: 'complete' | 'partial' | 'unavailable'
  count: number
  fetchedCount?: number | null
  pages?: number | null
  reason?: string
  collectionStatus?: 'complete' | 'partial' | 'unavailable'
  inputStatus?: 'complete' | 'partial'
  retrievedCount?: number | null
  sentCount?: number | null
  truncatedCount?: number | null
  inputDroppedCount?: number | null
  diagnostics?: Record<string, number>
  acquisitionPath?: string
  errors?: { stage: string; httpStatus: number; graphCode?: string; count: number }[]
}

export interface CompletedReport {
  kind: 'completed'
  runId: string
  text: string
  period: Period
  evidence: {
    id: string; sourceType: string; text: string; url?: string
    acquisitionTool?: string; verificationStatus?: string; timestampPrecision?: string
    attribution?: string; activityStatus?: string; messageKind?: string
  }[]
  coverage: Coverage[]
  metrics: Record<string, unknown>
  diagnosticRunId?: string
}

export type PendingResponse =
  | { kind: 'progress'; continuationToken: string; message: string; retryAfterSeconds?: number; automaticPolling?: boolean }
  | { kind: 'oauth_consent_required'; continuationToken: string; consentLink: string; message: string }
  | {
      kind: 'tool_approval_required'
      continuationToken: string
      approvalRequestId: string
      toolName: string
      toolArguments: string
      message: string
    }
  | { kind: 'partial_confirmation'; continuationToken: string; message: string; coverage: Coverage[]; diagnosticRunId?: string }

export type GenerateResponse = CompletedReport | PendingResponse
export type SaveStatus = 'confirmed' | 'test-generated'
export interface SavedReport { kind: 'saved'; webUrl: string; fileName: string }
export interface WorkIqDiagnostics {
  kind: 'workiq_tool_diagnostics'
  period: Period
  acquisitionProfile?: string
  coverage?: Coverage[]
  replies: {
    sequence: number
    toolName?: string
    taskId?: string
    format: string
    isError: boolean
    statusCode?: number
    errorCode?: string
    text: string
    truncated: boolean
    rawText?: string
    rawTruncated?: boolean
  }[]
  note: string
  latestResponseReplies?: WorkIqDiagnostics['replies']
}

export interface ReportApi {
  config(signal: AbortSignal): Promise<ReportConfig>
  resolve(body: ResolveRequest, signal: AbortSignal): Promise<ResolveResponse>
  generate(body: GenerateRequest, signal: AbortSignal): Promise<GenerateResponse>
  save(body: { runId: string; text: string; status: SaveStatus }, signal: AbortSignal): Promise<SavedReport>
  diagnostics(runId: string, signal: AbortSignal): Promise<WorkIqDiagnostics>
}

export interface EditorState {
  input: string
  period: Period
  members: string
  draft: string
}

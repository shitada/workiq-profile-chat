export interface ChatMessage {
  id: string
  role: 'user' | 'assistant'
  text: string
}

export interface ChatRequest {
  message: string
  previousResponseId?: string
  continueAfterConsent?: boolean
  approval?: {
    requestId: string
    approved: boolean
  }
}

export interface ChatCompletedResponse {
  kind: 'completed'
  responseId: string
  text: string
}

export interface OAuthConsentRequiredResponse {
  kind: 'oauth_consent_required'
  responseId: string
  consentLink: string
}

export interface ToolApprovalRequiredResponse {
  kind: 'tool_approval_required'
  responseId: string
  approvalRequestId: string
  serverLabel: string
  toolName: string
  toolArguments: string
}

export type ChatResponse =
  | ChatCompletedResponse
  | OAuthConsentRequiredResponse
  | ToolApprovalRequiredResponse

export interface ApiError {
  code: string
  message: string
  correlationId: string
}

export interface ServiceStatus {
  secretRotationRequired: boolean
  secretDaysRemaining: number | null
}

export interface PendingContinuation {
  kind: 'oauth' | 'approval'
  responseId: string
  originalMessage: string
  consentLink?: string
  consentCreatedAt?: number
  approvalRequestId?: string
  toolName?: string
  toolArguments?: string
}

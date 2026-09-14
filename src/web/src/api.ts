import { runtimeConfig } from './config'
import type {
  ApiError,
  ChatRequest,
  ChatResponse,
  ServiceStatus,
} from './types'

export async function getServiceStatus(
  accessToken: string,
  signal?: AbortSignal,
): Promise<ServiceStatus | null> {
  const response = await fetch(`${runtimeConfig.apiUrl}/api/status`, {
    headers: {
      Authorization: `Bearer ${accessToken}`,
    },
    signal,
  })
  return response.ok ? ((await response.json()) as ServiceStatus) : null
}

export async function sendChat(
  accessToken: string,
  body: ChatRequest,
  signal?: AbortSignal,
): Promise<ChatResponse> {
  const response = await fetch(`${runtimeConfig.apiUrl}/api/chat`, {
    method: 'POST',
    headers: {
      Authorization: `Bearer ${accessToken}`,
      'Content-Type': 'application/json',
    },
    body: JSON.stringify(body),
    signal,
  })

  if (!response.ok) {
    let detail: ApiError | null = null
    try {
      detail = (await response.json()) as ApiError
    } catch {
      // The API can fail before the Function starts, so a JSON body isn't guaranteed.
    }

    const suffix = detail?.correlationId
      ? ` (correlation: ${detail.correlationId})`
      : ''
    throw new Error(
      `${detail?.message ?? `API request failed with HTTP ${response.status}.`}${suffix}`,
    )
  }

  return (await response.json()) as ChatResponse
}

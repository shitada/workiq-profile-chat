import type { ChatMessage, PendingContinuation } from './types'

const messagesKey = 'workiq-profile-chat.messages.v1'
const continuationKey = 'workiq-profile-chat.continuation.v2'
const legacyContinuationKey = 'workiq-profile-chat.continuation.v1'

export function loadMessages(): ChatMessage[] {
  return readSessionValue<ChatMessage[]>(messagesKey) ?? []
}

export function saveMessages(messages: ChatMessage[]): void {
  sessionStorage.setItem(messagesKey, JSON.stringify(messages))
}

export function loadPendingContinuation(): PendingContinuation | null {
  sessionStorage.removeItem(legacyContinuationKey)
  return readSessionValue<PendingContinuation>(continuationKey)
}

export function savePendingContinuation(
  continuation: PendingContinuation | null,
): void {
  if (continuation === null) {
    sessionStorage.removeItem(continuationKey)
    return
  }
  sessionStorage.setItem(continuationKey, JSON.stringify(continuation))
}

export function clearSession(): void {
  sessionStorage.removeItem(messagesKey)
  sessionStorage.removeItem(continuationKey)
  sessionStorage.removeItem(legacyContinuationKey)
}

function readSessionValue<T>(key: string): T | null {
  const raw = sessionStorage.getItem(key)
  if (!raw) return null

  try {
    return JSON.parse(raw) as T
  } catch {
    sessionStorage.removeItem(key)
    return null
  }
}

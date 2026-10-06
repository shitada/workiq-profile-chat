import type { EditorState, Provider, ReportKind } from './types'

const PREFIX = 'report-chat:v1:'
export function sessionKey(tenant: string, account: string, provider: Provider): string {
  return `${PREFIX}${encodeURIComponent(tenant)}:${encodeURIComponent(account)}:${provider}`
}

export function emptyEditor(): EditorState {
  return { input: '', period: { kind: 'daily' }, members: '', draft: '' }
}

export function loadEditor(key: string): EditorState {
  try {
    const data = JSON.parse(sessionStorage.getItem(key) ?? 'null')
    const kinds: ReportKind[] = ['daily', 'personalWeekly', 'managerWeekly']
    if (data?.version !== 1 || !kinds.includes(data.period?.kind) ||
      !['input', 'members', 'draft'].every((field) => typeof data[field] === 'string')) return emptyEditor()
    const period = { kind: data.period.kind } as EditorState['period']
    for (const field of ['reportDate', 'startDate', 'endDate', 'previousBusinessDate'] as const) {
      if (typeof data.period[field] === 'string' && /^\d{4}-\d{2}-\d{2}$/.test(data.period[field])) period[field] = data.period[field]
    }
    return { input: data.input, members: data.members, draft: data.draft, period }
  } catch { return emptyEditor() }
}

export function saveEditor(key: string, state: EditorState): boolean {
  try {
    // Persist only edits. Tokens, source content, consent URLs and continuation state are deliberately not stored.
    sessionStorage.setItem(key, JSON.stringify({
      version: 1, input: state.input, period: state.period, members: state.members, draft: state.draft,
    }))
    return true
  } catch { return false }
}

export function clearEditor(key: string): void {
  try { sessionStorage.removeItem(key) } catch { /* Storage may be disabled by browser policy. */ }
}

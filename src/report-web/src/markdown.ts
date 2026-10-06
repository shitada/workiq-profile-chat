import { safeReferenceUrl } from './report'

export function markdownUrl(value: string, key: string): string {
  if (key !== 'href') return ''
  if (/^#[a-zA-Z0-9_.:%-]+$/.test(value)) return value
  return safeReferenceUrl(value) ?? ''
}

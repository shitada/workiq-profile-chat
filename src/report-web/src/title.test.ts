import { describe, expect, it } from 'vitest'
import { transformReportTitle } from '../build-title'

describe('provider-specific initial document title', () => {
  const html = '<html><head><title>Report Chat · テスト環境</title></head></html>'
  it.each([
    ['graph', 'Graph API | Report Chat'],
    ['workiq', 'Work IQ | Report Chat'],
  ])('embeds %s title in initial index HTML', (provider, title) => {
    expect(transformReportTitle(html, 'C:\\web\\index.html', provider)).toContain(`<title>${title}</title>`)
    expect(transformReportTitle(html, '/web/index.html', provider)).toContain(`<title>${title}</title>`)
  })
  it('preserves the separate redirect bridge title', () => {
    const bridge = '<html><head><title>認証処理中</title></head></html>'
    expect(transformReportTitle(bridge, 'C:\\web\\redirect.html', 'graph')).toBe(bridge)
    expect(transformReportTitle(bridge, 'C:\\web\\redirect.html', 'workiq')).toBe(bridge)
  })
  it('rejects unknown provider configuration', () => {
    expect(() => transformReportTitle(html, 'index.html', 'other')).toThrow('VITE_REPORT_PROVIDER')
  })
})

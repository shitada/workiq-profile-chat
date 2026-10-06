import { createElement } from 'react'
import { renderToStaticMarkup } from 'react-dom/server'
import { describe, expect, it } from 'vitest'
import ReportMarkdown from './ReportMarkdown'
import { markdownUrl } from './markdown'

function render(text: string): string {
  return renderToStaticMarkup(createElement(ReportMarkdown, { text }))
}

describe('safe report Markdown', () => {
  it('renders Unicode headings, lists, emphasis, GFM tables and reference citations', () => {
    const html = render('# 日報 📘\n\n## 成果\n\n- **完了**：設計\n- 次の作業\n\n|担当|状態|\n|---|---|\n|本人|完了|\n\n[根拠①][source]\n\n[source]: https://example.com/evidence')
    expect(html).toContain('<h1>日報 📘</h1>')
    expect(html).toContain('<h2>成果</h2>')
    expect(html).toContain('<ul>')
    expect(html).toContain('<strong>完了</strong>')
    expect(html).toContain('<table>')
    expect(html).toContain('<th>担当</th>')
    expect(html).toContain('href="https://example.com/evidence"')
    expect(html).toContain('target="_blank"')
    expect(html).toContain('rel="noopener noreferrer"')
  })
  it('drops raw HTML, script, iframe and event attributes without an HTML parser plugin', () => {
    const html = render('<script>window.pwned=true</script>\n\n<img src="https://track.invalid/pixel" onerror="alert(1)">\n\n<iframe src="https://track.invalid"></iframe>\n\n本文')
    expect(html).not.toContain('<script')
    expect(html).not.toContain('<img')
    expect(html).not.toContain('<iframe')
    expect(html).not.toContain('onerror')
    expect(html).toContain('本文')
  })
  it.each(['javascript:alert(1)', 'vbscript:msgbox(1)', 'data:text/html,test', '//tracking.invalid/pixel', 'http://example.com', 'https://user:pass@example.com'])('blocks unsafe link %s', (url) => {
    expect(markdownUrl(url, 'href')).toBe('')
  })
  it('does not render executable URLs even when entity-obfuscated in Markdown', () => {
    const html = render('[危険](javascript:alert%281%29) [難読化](java&#x73;cript:alert%281%29) [data](data:text/html,test)')
    expect(html).not.toContain('href=')
    expect(html).toContain('危険')
    expect(html).toContain('難読化')
  })
  it('replaces all images with alt text and emits no image/preload resource requests', () => {
    const html = render('![図解](https://track.invalid/pixel?id=private) ![inline](data:image/png;base64,AAAA)')
    expect(html).toContain('画像は読み込みません：図解')
    expect(html).not.toContain('<img')
    expect(html).not.toContain('<link')
    expect(html).not.toContain('track.invalid')
    expect(markdownUrl('https://example.com/image.png', 'src')).toBe('')
  })
  it('supports local footnote citations without external navigation', () => {
    const html = render('根拠[^1]\n\n[^1]: 注記の本文。')
    expect(html).toContain('href="#user-content-fn-1"')
    expect(html).toContain('注記')
    expect(html).toContain('本文に戻る')
    expect(markdownUrl('#user-content-fnref-1', 'href')).toBe('#user-content-fnref-1')
  })
})

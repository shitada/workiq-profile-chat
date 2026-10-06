import { memo } from 'react'
import Markdown from 'react-markdown'
import type { Components } from 'react-markdown'
import remarkGfm from 'remark-gfm'
import { markdownUrl } from './markdown'

const plugins = [remarkGfm]
const components: Components = {
  a({ href, children, id, title, 'aria-label': label, 'aria-describedby': describedBy }) {
    const safe = markdownUrl(href ?? '', 'href')
    if (!safe) return <span>{children}</span>
    return <a href={safe} id={id} title={title} aria-label={label} aria-describedby={describedBy}
      target={safe.startsWith('#') ? undefined : '_blank'} rel="noopener noreferrer">{children}</a>
  },
  img({ alt }) {
    return <span className="markdown-image-placeholder">［画像は読み込みません{alt ? `：${alt}` : ''}］</span>
  },
  table({ children }) {
    return <div className="markdown-table-scroll" tabIndex={0} role="region" aria-label="レポート内の表"><table>{children}</table></div>
  },
}

function ReportMarkdown({ text }: { text: string }) {
  return <div className="report-markdown">
    <Markdown skipHtml remarkPlugins={plugins} components={components} urlTransform={markdownUrl}
      remarkRehypeOptions={{ footnoteLabel: '注記', footnoteBackLabel: '本文に戻る' }}>
      {text}
    </Markdown>
  </div>
}

export default memo(ReportMarkdown)

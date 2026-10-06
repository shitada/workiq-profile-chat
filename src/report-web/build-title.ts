export function transformReportTitle(html: string, filename: string, provider: string): string {
  if (provider !== 'graph' && provider !== 'workiq') throw new Error('VITE_REPORT_PROVIDER must be graph or workiq')
  if (!/(?:^|[\\/])index\.html$/.test(filename)) return html
  const title = provider === 'workiq' ? 'Work IQ | Report Chat' : 'Graph API | Report Chat'
  return html.replace(/<title>[^<]*<\/title>/i, `<title>${title}</title>`)
}

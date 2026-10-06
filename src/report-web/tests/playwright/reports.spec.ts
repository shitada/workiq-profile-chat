import { expect, test } from '@playwright/test'
import type { Page } from '@playwright/test'

const period = { kind: 'daily', reportDate: '2026-08-01', startDate: '2026-07-31', endDate: '2026-08-01', previousBusinessDate: '2026-07-31' }
const weekly = { kind: 'personalWeekly', startDate: '2026-08-03', endDate: '2026-08-07' }
const completion = {
  kind: 'completed', runId: 'test-run', text: '実績：テスト会議\n予定：確認',
  period, evidence: [{ id: 'source-1', sourceType: 'calendar', text: '<script>window.pwned=true</script>', url: 'https://example.com/source' }],
  coverage: [{ sourceType: 'calendar', status: 'complete', count: 1 }, { sourceType: 'transcripts', status: 'unavailable', count: 0, reason: '権限または保持期限' }],
  metrics: { promptHash: 'test-hash', inputTokens: 21, retrievalMs: 13, accessToken: 'should-never-export' },
}

async function setup(page: Page, options: { configured?: boolean; generate?: unknown[]; resolve?: unknown } = {}) {
  const requests: { endpoint: string; body: Record<string, unknown> }[] = []
  const responses = [...(options.generate ?? [completion])]
  await page.route('**/test-api/api/reports/*', async (route) => {
    const endpoint = route.request().url().split('/').pop()!
    const body = route.request().postDataJSON() ?? {}
    requests.push({ endpoint, body })
    expect(route.request().headers().authorization).toBe('Bearer test-dummy-access-token')
    const result = endpoint === 'config' ? {
      provider: 'graph', testYear: 2026, timeZone: 'Asia/Tokyo', promptHash: 'test-hash',
      modelDeployment: 'test-model', modelVersion: 'test-version', sharePointConfigured: options.configured ?? true,
    } : endpoint === 'resolve' ? options.resolve ?? { kind: 'resolved', message: '2026年8月1日の日報', period }
      : endpoint === 'save' ? { kind: 'saved', fileName: 'test-report.md', webUrl: 'https://example.sharepoint.com/test-report.md' }
        : responses.shift() ?? completion
    await route.fulfill({ json: result })
  })
  await page.goto('/tests/playwright/report-harness.html')
  return requests
}

async function start(page: Page, prompt = '8/1の日報作って') {
  await page.getByLabel('日報・週報の依頼').fill(prompt)
  await page.getByRole('button', { name: '対象期間を確認', exact: true }).click()
  await page.getByRole('button', { name: 'この期間で生成', exact: true }).click()
}

test('failed evidence collection displays diagnostics instead of a stuck generating status or invented report', async ({ page }) => {
  await setup(page)
  await page.route('**/test-api/api/reports/generate', async (route) => {
    await route.fulfill({ status: 422, json: {
      code: 'collector_evidence_rejected', message: '取得結果の日時を検証できません。', correlationId: 'test-correlation',
      details: { coverage: [{ sourceType: 'chat', status: 'partial', collectionStatus: 'partial',
        count: 0, fetchedCount: 2, reason: 'collector_evidence_metadata_missing',
        diagnostics: { 'collector.itemsReturned': 2, 'collector.metadataRejected': 2 } }] },
    } })
  })
  await start(page)
  await expect(page.getByRole('alert')).toContainText('test-correlation')
  await expect(page.getByRole('heading', { name: '生成できなかった理由' })).toBeVisible()
  await expect(page.getByText('処理を完了できませんでした。', { exact: false })).toBeVisible()
  await expect(page.getByText('根拠の形式不足', { exact: false })).toBeVisible()
  await expect(page.getByLabel('レポートプレビュー', { exact: true })).toHaveCount(0)
  await expect(page.getByLabel('失敗した取得の診断').locator('details.coverage-details')).toHaveAttribute('open', '')
  await expect(page.getByLabel('失敗した取得の診断')).toContainText('collector_evidence_metadata_missing')
  await page.getByText('取得診断 JSON（本文・認証情報なし）', { exact: true }).click()
  await expect(page.getByLabel('失敗した取得の診断')).toContainText('collector.metadataRejected')
  await page.getByLabel('日報日', { exact: true }).fill('2026-08-04')
  await expect(page.getByLabel('失敗した取得の診断')).toHaveCount(0)
})

test('failed Work IQ replies load automatically and display as untrusted text', async ({ page }) => {
  await setup(page)
  const runId = 'a'.repeat(64)
  let diagnosticRequests = 0
  await page.route('**/test-api/api/reports/generate', async (route) => {
    await route.fulfill({ status: 422, json: {
      code: 'collector_no_evidence', message: '根拠なし',
      details: { diagnosticRunId: runId, coverage: [] },
    } })
  })

  await page.route(`**/test-api/api/reports/diagnostics/${runId}`, async (route) => {
    diagnosticRequests++
    await route.fulfill({ json: {
      kind: 'workiq_tool_diagnostics', period,
      note: '本人だけの診断',
      replies: [{ sequence: 1, format: 'workiq_response', isError: false, truncated: false,
        text: '<img src="https://tracking.invalid/image"><script>window.pwned=true</script>Work IQ response',
        rawText: '{"unknownAnswer":"raw original <script>window.pwned=true</script>"}', rawTruncated: false }],
    } })
  })
  await start(page)
  const section = page.getByLabel('Work IQ実応答の診断')
  await expect(section).toContainText('Work IQ response')
  await expect(page.getByRole('button', { name: 'Work IQの実応答を確認', exact: true })).toHaveCount(0)
  await section.getByText('受信した応答（認証情報等を除去・JSON形式）', { exact: true }).click()
  await expect(section).toContainText('raw original')
  await expect(section.locator('img,script')).toHaveCount(0)
  expect(await page.evaluate(() => (window as unknown as { pwned?: boolean }).pwned)).toBeUndefined()
  expect(diagnosticRequests).toBe(1)
  await page.getByLabel('日報日', { exact: true }).fill('2026-08-04')
  await expect(section).toHaveCount(0)
})

test('successful Work IQ result automatically shows original replies separately and does not persist them in draft storage', async ({ page }) => {
  const runId = 'b'.repeat(64)
  await setup(page, { generate: [{ ...completion, diagnosticRunId: runId }] })
  await page.route(`**/test-api/api/reports/diagnostics/${runId}`, async (route) => {
    await route.fulfill({ json: {
      kind: 'workiq_tool_diagnostics', period, note: 'Original response',
      replies: [{ sequence: 1, format: 'mcp_text', isError: false, truncated: false, text: 'UNIQUE_ORIGINAL_TOOL_TEXT',
        rawText: '{"rawAnswer":"UNIQUE_RAW_OUTPUT"}', rawTruncated: false }],
    } })
  })
  await start(page)
  await expect(page.getByLabel('レポートプレビュー', { exact: true })).toBeVisible()
  await expect(page.getByLabel('Work IQ実応答の診断')).toContainText('UNIQUE_ORIGINAL_TOOL_TEXT')
  expect(await page.evaluate(() => JSON.stringify(sessionStorage))).not.toContain('UNIQUE_')
  expect(await page.evaluate(() => JSON.stringify(localStorage))).not.toContain('UNIQUE_')
  await page.getByRole('button', { name: '確認して SharePoint に保存', exact: true }).click()
  await expect(page.getByText('保存成功：', { exact: false })).toBeVisible()
  await expect(page.getByLabel('Work IQ実応答の診断')).toContainText('UNIQUE_ORIGINAL_TOOL_TEXT')
  await page.getByLabel('日報日', { exact: true }).fill('2026-08-04')
  await expect(page.getByLabel('Work IQ実応答の診断')).toHaveCount(0)
})

test('original failed-response tool error is visible even when old saved reply was empty', async ({ page }) => {
  const runId = 'f'.repeat(64)
  await setup(page)
  await page.route('**/test-api/api/reports/generate', async (route) => {
    await route.fulfill({ status: 502, json: {
      message: 'Tool failed', details: { restartRequired: true, diagnosticRunId: runId, coverage: [] },
    } })
  })
  await page.route(`**/test-api/api/reports/diagnostics/${runId}`, async (route) => {
    await route.fulfill({ json: {
      kind: 'workiq_tool_diagnostics', period, note: 'Saved response',
      replies: [{ sequence: 4, format: 'empty', isError: true, truncated: false, text: '' }],
      latestResponseReplies: [{ sequence: 4, format: 'mcp_error', isError: true, truncated: false,
        text: 'Original tool error', rawText: '{"error":"<script>window.pwned=true</script>"}' }],
    } })
  })
  await start(page)
  const errors = page.getByLabel('保存済みFoundry応答のツールエラー')
  await expect(errors).toContainText('Original tool error')
  await expect(errors).toContainText('window.pwned')
  await expect(errors.locator('script')).toHaveCount(0)
  expect(await page.evaluate(() => (window as unknown as { pwned?: boolean }).pwned)).toBeUndefined()
})

test('structured Work IQ diagnostics identify the tool and keep previews separate from adopted evidence', async ({ page }) => {
  const runId = 'd'.repeat(64)
  await setup(page, { generate: [{ ...completion, diagnosticRunId: runId, coverage: [
    { sourceType: 'calendar', status: 'partial', collectionStatus: 'partial', count: 3,
      fetchedCount: 5, retrievedCount: 3, pages: 1, inputStatus: 'complete',
      diagnostics: { 'workiq.fetch': 1, 'workiq.excludedSystem': 1, 'workiq.outOfPeriod': 1 } },
  ] }] })
  await page.route(`**/test-api/api/reports/diagnostics/${runId}`, async (route) => {
    await route.fulfill({ json: {
      kind: 'workiq_tool_diagnostics', period, note: 'Structured previews are not the accepted evidence set.',
      replies: [
        { sequence: 1, toolName: 'fetch', taskId: 'calendar', format: 'structured', isError: false,
          text: '', truncated: false, rawText: '{"value":[{"subject":"UNIQUE_FETCH_PREVIEW"}', rawTruncated: true },
        { sequence: 2, toolName: 'call_function', format: 'structured', isError: false,
          text: 'UNIQUE_FUNCTION_PREVIEW', truncated: false, rawText: '{"value":[]}', rawTruncated: false },
        { sequence: 3, format: 'mcp_text', isError: false, text: 'Legacy response', truncated: false },
      ],
    } })
  })
  await start(page)
  const section = page.getByLabel('Work IQ実応答の診断')
  await expect(section.getByRole('heading', { name: 'fetch応答 1', exact: true })).toBeVisible()
  await expect(section.getByRole('heading', { name: 'call_function応答 2', exact: true })).toBeVisible()
  await expect(section.getByRole('heading', { name: 'ask応答 3', exact: true })).toBeVisible()
  await section.getByText('受信した応答（認証情報等を除去・JSON形式）', { exact: true }).first().click()
  await expect(section).toContainText('完全なJSONとは限りません')
  await expect(page.getByLabel('calendarの取得経路と除外')).toContainText('fetch承認予約')
  await expect(page.getByLabel('calendarの取得経路と除外')).toContainText('システムイベント除外')
  expect(await page.evaluate(() => JSON.stringify(sessionStorage))).not.toContain('UNIQUE_FETCH_PREVIEW')
  expect(await page.evaluate(() => JSON.stringify(localStorage))).not.toContain('UNIQUE_FUNCTION_PREVIEW')
  await expect(page.getByLabel('レポートプレビュー', { exact: true })).not.toContainText('UNIQUE_FETCH_PREVIEW')
})

test('partial result automatically shows replies before the user decides to generate', async ({ page }) => {
  const runId = 'c'.repeat(64)
  await setup(page, { generate: [{
    kind: 'partial_confirmation', continuationToken: runId, diagnosticRunId: runId,
    message: '不足あり', coverage: completion.coverage,
  }] })
  await page.route(`**/test-api/api/reports/diagnostics/${runId}`, async (route) => {
    await route.fulfill({ json: {
      kind: 'workiq_tool_diagnostics', period, note: 'Original response',
      replies: [{ sequence: 1, format: 'mcp_text', isError: false, truncated: false, text: 'Partial original answer' }],
    } })
  })
  await start(page)
  await expect(page.getByLabel('Work IQ実応答の診断')).toContainText('Partial original answer')
  await expect(page.getByRole('button', { name: '不足を確認して生成', exact: true })).toBeVisible()
  await expect(page.getByLabel('レポートプレビュー', { exact: true })).toHaveCount(0)
})

test('diagnostic fetch failure stays separate from successful generation and can be retried', async ({ page }) => {
  const runId = 'd'.repeat(64)
  await setup(page, { generate: [{ ...completion, diagnosticRunId: runId }] })
  let attempts = 0
  await page.route(`**/test-api/api/reports/diagnostics/${runId}`, async (route) => {
    if (++attempts === 1) await route.fulfill({ status: 503, json: { message: '診断読込の一時エラー' } })
    else await route.fulfill({ json: {
      kind: 'workiq_tool_diagnostics', period, replies: [], note: 'No retained reply',
    } })
  })
  await start(page)
  await expect(page.getByLabel('レポートプレビュー', { exact: true })).toBeVisible()
  await expect(page.getByLabel('Work IQ実応答の診断')).toContainText('診断読込の一時エラー')
  await page.getByRole('button', { name: 'Work IQ応答の表示を再試行', exact: true }).click()
  await expect(page.getByLabel('Work IQ実応答の診断')).toContainText('保存された実応答がありません')
  expect(attempts).toBe(2)
})

test('identity change discards a late automatic tool reply and successful response exports no raw content', async ({ page }) => {
  const runId = 'e'.repeat(64)
  await setup(page, { generate: [{ ...completion, diagnosticRunId: runId }] })
  let release: () => void = () => {}
  const waiting = new Promise<void>((resolve) => { release = resolve })
  let requested = false
  await page.route(`**/test-api/api/reports/diagnostics/${runId}`, async (route) => {
    requested = true
    await waiting
    await route.fulfill({ json: {
      kind: 'workiq_tool_diagnostics', period, note: 'OLD_PRIVATE_REPLY',
      replies: [{ sequence: 1, format: 'mcp_text', isError: false, truncated: false, text: 'OLD_PRIVATE_REPLY' }],
    } }).catch(() => {})
  })
  await start(page)
  await expect(page.getByLabel('レポートプレビュー', { exact: true })).toBeVisible()
  await expect.poll(() => requested).toBe(true)
  await page.getByRole('button', { name: 'テスト用ユーザー切替' }).click()
  release()
  await expect(page.getByTestId('period-summary')).toContainText('未指定')
  await expect(page.getByLabel('Work IQ実応答の診断')).toHaveCount(0)
  await expect(page.getByText('OLD_PRIVATE_REPLY')).toHaveCount(0)
})

test('terminal collector failure discards the old handle and starts fresh only on an explicit click', async ({ page }) => {
  await setup(page)
  const bodies: Record<string, unknown>[] = []
  await page.route('**/test-api/api/reports/generate', async (route) => {
    bodies.push(route.request().postDataJSON())
    if (bodies.length === 1) {
      await route.fulfill({ json: {
        kind: 'progress', continuationToken: 'failed-run', automaticPolling: true, retryAfterSeconds: 0, message: '取得中',
      } })
    } else if (bodies.length === 2) {
      await route.fulfill({ status: 502, json: {
        code: 'foundry_response_incomplete', message: '取得Agentが完全な結果を返しませんでした。',
        details: { restartRequired: true, diagnosticRunId: 'a'.repeat(64) },
      } })
    } else await route.fulfill({ json: completion })
  })
  await start(page)
  await expect(page.getByRole('alert')).toContainText('完全な結果')
  await expect(page.getByRole('button', { name: '続きを取得', exact: true })).toHaveCount(0)
  await expect(page.getByRole('button', { name: '同じ期間で新規取得', exact: true })).toBeVisible()
  expect(bodies).toHaveLength(2)
  expect(bodies[1].continuationToken).toBe('failed-run')
  await page.getByRole('button', { name: '同じ期間で新規取得', exact: true }).click()
  await expect(page.getByLabel('レポートプレビュー', { exact: true })).toBeVisible()
  expect(bodies).toHaveLength(3)
  expect(bodies[2]).not.toHaveProperty('continuationToken')
  expect(bodies[2]).not.toHaveProperty('approval')
  expect(bodies[2]).not.toHaveProperty('allowPartial')
  expect(bodies[2].period).toEqual(period)
})

test('aggregate service failure shows safe processing stage rather than guessing a permission problem', async ({ page }) => {
  await setup(page)
  await page.route('**/test-api/api/reports/generate', async (route) => {
    await route.fulfill({ status: 502, json: {
      code: 'collector_request_transport', message: 'Work IQ取得Agentへの要求で通信が完了しませんでした。',
      correlationId: 'safe-correlation',
      diagnostic: { stage: 'collector_request', category: 'transport',
        causes: [{ type: 'AggregateException', message: 'secret must not render' },
          { type: 'HttpRequestException', httpStatus: 0 }] },
    } })
  })
  await start(page)
  const diagnostic = page.getByLabel('サービス接続の診断')
  await expect(diagnostic).toBeVisible()
  await expect(diagnostic).toContainText('collector_request')
  await expect(diagnostic).toContainText('HttpRequestException')
  await expect(diagnostic).not.toContainText('secret must not render')
  await expect(page.getByLabel('レポートプレビュー', { exact: true })).toHaveCount(0)
  await page.getByLabel('日報日', { exact: true }).fill('2026-08-04')
  await expect(diagnostic).toHaveCount(0)
})

test('explicit date, editable result, draft regeneration, explicit save and file link', async ({ page }) => {
  const requests = await setup(page, { generate: [completion, { ...completion, runId: 'revised-run', text: '修正済み' }] })
  await expect(page.getByTestId('period-summary')).toContainText('未指定')
  expect(requests.filter((r) => r.endpoint !== 'config')).toHaveLength(0)
  await start(page)
  await expect(page.getByTestId('period-summary')).toContainText('2026-08-01')
  await expect(page.getByLabel('レポートプレビュー', { exact: true })).toContainText('実績：テスト会議')
  await expect(page.getByLabel('レポート本文（編集可能）')).toHaveCount(0)
  expect(requests.filter((r) => r.endpoint === 'save')).toHaveLength(0)
  await page.getByRole('button', { name: '編集', exact: true }).click()
  await page.getByLabel('レポート本文（編集可能）').fill('編集した下書き')
  await page.getByLabel('修正指示').fill('簡潔に')
  await page.getByRole('button', { name: '編集内容を使って再生成' }).click()
  await expect(page.getByLabel('レポートプレビュー', { exact: true })).toContainText('修正済み')
  await expect(page.getByRole('button', { name: 'プレビュー', exact: true })).toHaveAttribute('aria-pressed', 'true')
  expect(requests.filter((r) => r.endpoint === 'generate')[1].body).toMatchObject({ draft: '編集した下書き', message: '簡潔に', period, continuationToken: 'test-run' })
  await page.getByRole('button', { name: '確認して SharePoint に保存' }).click()
  await expect(page.getByRole('link', { name: 'test-report.md' })).toHaveAttribute('href', 'https://example.sharepoint.com/test-report.md')
  expect(requests.find((r) => r.endpoint === 'save')?.body).toEqual({ runId: 'revised-run', text: '修正済み', status: 'confirmed' })
})

test('ambiguous first week requires a choice before fetching', async ({ page }) => {
  const requests = await setup(page, {
    resolve: { kind: 'needs_confirmation', message: 'どの週ですか？', choices: [
      { label: '月初7日', period: { ...weekly, startDate: '2026-08-01' } }, { label: '営業週', period: weekly },
    ] }, generate: [{ ...completion, period: weekly }],
  })
  await page.getByLabel('日報・週報の依頼').fill('8月の1週目の週報作って')
  await page.getByRole('button', { name: '対象期間を確認', exact: true }).click()
  await expect(page.getByRole('heading', { name: '期間の確認が必要です' })).toBeVisible()
  expect(requests.filter((r) => r.endpoint === 'generate')).toHaveLength(0)
  await page.getByRole('button', { name: /営業週/ }).click()
  await page.getByRole('button', { name: 'この期間で生成' }).click()
  await expect(page.getByLabel('レポートプレビュー', { exact: true })).toBeVisible()
  expect(requests.find((r) => r.endpoint === 'generate')?.body.period).toEqual(weekly)
})

test('bounded progress, safe native consent, explicit tool approval and partial confirmation', async ({ page }) => {
  const requests = await setup(page, { generate: [
    ...Array.from({ length: 8 }, (_, i) => ({ kind: 'progress', continuationToken: `page-${i}`, message: `取得 ${i}`, retryAfterSeconds: 0 })),
    { kind: 'oauth_consent_required', continuationToken: 'oauth', message: '接続が必要', consentLink: 'https://sample.consent.azure-apim.net/login?state=test' },
    { kind: 'tool_approval_required', continuationToken: 'approval', message: '承認が必要', approvalRequestId: 'tool-1', toolName: 'ask', toolArguments: '{"query":"test"}' },
    { kind: 'partial_confirmation', continuationToken: 'partial', message: '一部取得不可', coverage: completion.coverage },
    completion,
  ] })
  await start(page)
  await expect(page.getByRole('button', { name: '続きを取得' })).toBeEnabled()
  expect(requests.filter((r) => r.endpoint === 'generate')).toHaveLength(8)
  await page.getByRole('button', { name: '続きを取得' }).click()
  const consent = page.getByRole('link', { name: '接続の同意画面を開く' })
  await expect(consent).toHaveAttribute('href', 'https://sample.consent.azure-apim.net/login?state=test')
  await expect(consent).toHaveAttribute('rel', 'noopener noreferrer')
  await expect(consent).toHaveAttribute('target', '_blank')
  await page.getByRole('button', { name: '同意後に再開' }).click()
  await page.getByRole('button', { name: '承認して続ける' }).click()
  await expect(page.getByRole('heading', { name: '情報が不足しています' })).toBeVisible()
  await page.getByRole('button', { name: '不足を確認して生成' }).click()
  await expect(page.getByLabel('レポートプレビュー', { exact: true })).toBeVisible()
  const generated = requests.filter((r) => r.endpoint === 'generate')
  expect(generated[10].body.approval).toEqual({ requestId: 'tool-1', approved: true })
  expect(generated[11].body.allowPartial).toBe(true)
  expect(generated[11].body).not.toHaveProperty('approval')
})

test('background collection polls automatically past eight responses without a continue click', async ({ page }) => {
  const requests = await setup(page, { generate: [
    ...Array.from({ length: 12 }, () => ({
      kind: 'progress', continuationToken: 'same-background-run', message: 'バックグラウンドで取得中',
      retryAfterSeconds: 0, automaticPolling: true,
    })),
    completion,
  ] })
  await start(page)
  await expect(page.getByLabel('レポートプレビュー', { exact: true })).toBeVisible()
  const generated = requests.filter(r => r.endpoint === 'generate')
  expect(generated).toHaveLength(13)
  expect(generated.slice(1).every(r => r.body.continuationToken === 'same-background-run')).toBe(true)
})

test('malicious consent is blocked and new date never reuses a continuation', async ({ page }) => {
  const requests = await setup(page, { generate: [{ kind: 'oauth_consent_required', message: 'bad', continuationToken: 'old-secret', consentLink: 'javascript:alert(1)' }] })
  await start(page)
  await expect(page.getByText('同意リンクを検証できません。', { exact: false })).toBeVisible()
  await expect(page.getByRole('button', { name: '同意後に再開' })).toBeDisabled()
  await expect(page.getByRole('link', { name: '接続の同意画面を開く' })).toHaveCount(0)
  await page.getByLabel('日報日', { exact: true }).fill('2026-08-04')
  await expect(page.getByRole('button', { name: '同意後に再開' })).toHaveCount(0)
  await page.getByRole('button', { name: 'この入力期間を使用' }).click()
  const resolves = requests.filter((r) => r.endpoint === 'resolve')
  expect(resolves[1].body.period).toEqual({ kind: 'daily', reportDate: '2026-08-04' })
  await page.getByRole('button', { name: 'この期間で生成' }).click()
  await expect(page.getByLabel('レポートプレビュー', { exact: true })).toBeVisible()
  expect(requests.filter((r) => r.endpoint === 'generate')[1].body).not.toHaveProperty('continuationToken')
})

test('manager requires explicit members, no-folder/source errors and safe text references', async ({ page }) => {
  const requests = await setup(page, {
    configured: false,
    resolve: { kind: 'resolved', message: '上長週報', period: { ...weekly, kind: 'managerWeekly' } },
    generate: [{ ...completion, period: { ...weekly, kind: 'managerWeekly' }, evidence: [{ id: 'unsafe', sourceType: 'chat', text: '<img src=x onerror=alert(1)>', url: 'javascript:alert(1)' }] }],
  })
  await page.getByLabel('対象メンバー ID / UPN（上長週報）').fill('alice@example.com, user-id')
  await start(page, '8/3〜8/7の上長週報作って')
  await expect(page.getByLabel('レポートプレビュー', { exact: true })).toBeVisible()
  expect(requests.find((r) => r.endpoint === 'generate')?.body.members).toEqual(['alice@example.com', 'user-id'])
  await expect(page.getByText('保存先フォルダー未設定', { exact: false })).toBeVisible()
  await expect(page.getByRole('button', { name: '確認して SharePoint に保存' })).toBeDisabled()
  await expect(page.getByRole('cell', { name: /旧総合状態：取得不可/ })).toBeVisible()
  await page.getByText('根拠・参照リンク (1)').click()
  await expect(page.getByText('<img src=x onerror=alert(1)>', { exact: true })).toBeVisible()
  await expect(page.locator('a[href^="javascript:"]')).toHaveCount(0)
  await expect(page.locator('.evidence img')).toHaveCount(0)
})

test('identity change aborts late generation and cannot display old draft', async ({ page }) => {
  await setup(page)
  let release: () => void = () => {}
  const waiting = new Promise<void>((resolve) => { release = resolve })
  await page.route('**/test-api/api/reports/generate', async (route) => {
    await waiting
    await route.fulfill({ json: { ...completion, text: 'OLD ACCOUNT SECRET' } }).catch(() => {})
  })
  await start(page)
  await expect(page.getByRole('button', { name: 'リクエストをキャンセル' })).toBeVisible()
  await page.getByRole('button', { name: 'テスト用ユーザー切替' }).click()
  release()
  await expect(page.getByTestId('period-summary')).toContainText('未指定')
  await expect(page.getByLabel('日報・週報の依頼')).toHaveValue('')
  await expect(page.getByText('OLD ACCOUNT SECRET')).toHaveCount(0)
})

test('comparison download contains reproducibility counters without token or report text', async ({ page }) => {
  await setup(page)
  await start(page)
  await expect(page.getByLabel('レポートプレビュー', { exact: true })).toBeVisible()
  const downloading = page.waitForEvent('download')
  await page.getByRole('button', { name: '比較メタデータ JSON' }).click()
  const download = await downloading
  const stream = await download.createReadStream()
  const chunks: Buffer[] = []
  for await (const chunk of stream!) chunks.push(chunk)
  const text = Buffer.concat(chunks).toString('utf8')
  expect(text).not.toContain('should-never-export')
  expect(text).not.toContain('test-dummy-access-token')
  expect(text).not.toContain(completion.text)
  expect(JSON.parse(text)).toMatchObject({ provider: 'graph', runId: 'test-run', metrics: { inputTokens: 21 }, configuration: { promptHash: 'test-hash' } })
})

test('manager missing members prevents generation and API errors show correlation', async ({ page }) => {
  const requests = await setup(page, { resolve: { kind: 'resolved', message: '上長週報', period: { ...weekly, kind: 'managerWeekly' } } })
  await start(page, '8/3〜8/7の上長週報作って')
  await expect(page.getByRole('alert')).toContainText('対象メンバー')
  expect(requests.filter((r) => r.endpoint === 'generate')).toHaveLength(0)
  await page.route('**/test-api/api/reports/resolve', async (route) => {
    await route.fulfill({ status: 503, json: { code: 'source_unavailable', message: '情報源へ接続できません', correlationId: 'test-error-id' } })
  })
  await page.getByRole('button', { name: '対象期間を確認', exact: true }).click()
  await expect(page.getByRole('alert')).toContainText('情報源へ接続できません (correlation: test-error-id)')
})

test('reload restores editable draft without tokens or continuation and regenerates explicitly', async ({ page }) => {
  const requests = await setup(page)
  await start(page)
  await page.getByRole('button', { name: '編集', exact: true }).click()
  await page.getByLabel('レポート本文（編集可能）').fill('復元する編集本文')
  await page.reload()
  await page.getByText('復元した編集内容（再取得・確認が必要です）').click()
  await expect(page.getByLabel('復元した下書き')).toHaveValue('復元する編集本文')
  expect(requests.filter((r) => r.endpoint === 'generate')).toHaveLength(1)
  const stored = await page.evaluate(() => JSON.stringify(sessionStorage))
  expect(stored).not.toContain('test-dummy-access-token')
  expect(stored).not.toContain('continuationToken')
  await page.getByLabel('下書きの修正指示').fill('再確認してください')
  await page.getByRole('button', { name: '対象期間を確認して下書きから再生成' }).click()
  await expect(page.getByLabel('レポートプレビュー', { exact: true })).toBeVisible()
  expect(requests.filter((r) => r.endpoint === 'generate')[1].body).toMatchObject({ draft: '復元する編集本文', period })
  expect(requests.filter((r) => r.endpoint === 'generate')[1].body).not.toHaveProperty('continuationToken')
})

test('new prompt aborts an older resolve and cannot restore its dates', async ({ page }) => {
  await setup(page)
  let release: () => void = () => {}
  const waiting = new Promise<void>((resolve) => { release = resolve })
  await page.route('**/test-api/api/reports/resolve', async (route) => {
    if (route.request().postDataJSON().message === '古い依頼') {
      await waiting
      await route.fulfill({ json: { kind: 'resolved', message: 'OLD PERIOD', period } }).catch(() => {})
    } else await route.fulfill({ json: { kind: 'resolved', message: '新しい週', period: weekly } })
  })
  await page.getByLabel('日報・週報の依頼').fill('古い依頼')
  await page.getByRole('button', { name: '対象期間を確認', exact: true }).click()
  await expect(page.getByRole('button', { name: 'リクエストをキャンセル' })).toBeVisible()
  await page.getByLabel('日報・週報の依頼').fill('8/3〜8/7の週報')
  await page.getByRole('button', { name: '対象期間を確認', exact: true }).click()
  await expect(page.getByTestId('period-summary')).toContainText('2026-08-03')
  release()
  await expect(page.getByTestId('period-summary')).toContainText('2026-08-07')
  await expect(page.getByText('OLD PERIOD')).toHaveCount(0)
})

test('coverage separates fetched/adopted/unknown and explains incomplete sources without guessing permissions', async ({ page }) => {
  await setup(page, { generate: [{
    ...completion,
    coverage: [
      { sourceType: 'calendar', status: 'complete', count: 6, fetchedCount: 6, pages: 1 },
      { sourceType: 'chat', status: 'complete', count: 0, fetchedCount: 0, pages: 2 },
      { sourceType: 'channel', status: 'partial', count: 17, fetchedCount: 25, pages: 5, reason: 'shared_channels_not_discovered;input_budget_exhausted' },
      { sourceType: 'transcript', status: 'unavailable', count: 0, reason: 'historical_transcript_missing_or_no_accessible_recordings' },
    ],
  }] })
  await start(page)
  const table = page.getByRole('table')
  await expect(table.getByRole('columnheader', { name: '取得', exact: true })).toBeVisible()
  await expect(table.getByRole('columnheader', { name: '採用', exact: true })).toBeVisible()
  const channel = table.getByRole('row').filter({ has: page.getByRole('rowheader', { name: 'channel', exact: true }) })
  await expect(channel.getByRole('cell', { name: '25', exact: true })).toBeVisible()
  await expect(channel.getByRole('cell', { name: '17', exact: true })).toBeVisible()
  await expect(channel.getByText('未探索', { exact: true })).toBeVisible()
  await expect(channel.getByText('入力上限', { exact: true })).toBeVisible()
  await expect(page.getByText('取得成功・0件', { exact: true })).toBeVisible()
  const transcript = table.getByRole('row').filter({ has: page.getByRole('rowheader', { name: 'transcript', exact: true }) })
  await expect(transcript.getByRole('cell', { name: '不明', exact: true })).toHaveCount(3)
  await expect(transcript.getByText('原因未確定', { exact: true })).toBeVisible()
  await expect(transcript.locator('details')).not.toHaveAttribute('open', '')
  await transcript.getByText('技術詳細（元の理由・取得段階）', { exact: true }).click()
  await expect(transcript.locator('pre')).toHaveText('historical_transcript_missing_or_no_accessible_recordings')
  expect(await page.locator('.chat-panel.report-panel').evaluate((node) => getComputedStyle(node).display)).toBe('flex')
})

test('coverage distinguishes completed collection with input reduction from actual forbidden failure', async ({ page }) => {
  await setup(page, { generate: [{
    ...completion,
    coverage: [
      { sourceType: 'channel', status: 'partial', collectionStatus: 'complete', inputStatus: 'partial',
        count: 17, fetchedCount: 40, retrievedCount: 25, sentCount: 17, truncatedCount: 4, inputDroppedCount: 8, pages: 3,
        reason: 'input_budget_exhausted', diagnostics: { 'allChannels.items': 5, 'messages.periodMatched': 25 }, errors: [] },
      { sourceType: 'transcript', status: 'unavailable', collectionStatus: 'unavailable', inputStatus: 'complete',
        count: 0, fetchedCount: 0, retrievedCount: 0, sentCount: 0, truncatedCount: 0, inputDroppedCount: 0, pages: 1,
        reason: 'meetings:graph_http_403', diagnostics: { 'meetings.requests': 1 },
        errors: [{ stage: 'meetings', httpStatus: 403, graphCode: 'Forbidden', count: 1 }] },
    ],
  }] })
  await start(page)
  const rows = page.getByRole('table').getByRole('row')
  const channel = rows.filter({ has: page.getByRole('rowheader', { name: 'channel', exact: true }) })
  await expect(channel.getByText('取得完了', { exact: true })).toBeVisible()
  await expect(channel.getByText('入力削減あり', { exact: true })).toBeVisible()
  await expect(channel.getByRole('cell', { name: '25', exact: true })).toBeVisible()
  const transcript = rows.filter({ has: page.getByRole('rowheader', { name: 'transcript', exact: true }) })
  await expect(transcript.getByText('アクセス拒否', { exact: true })).toBeVisible()
  await expect(transcript.getByText('削減なし', { exact: true })).toBeVisible()
  await transcript.getByText('技術詳細（元の理由・取得段階）', { exact: true }).click()
  await expect(transcript.getByText('meetings：HTTP 403 / Forbidden / 1 回', { exact: true })).toBeVisible()
  await expect(transcript.locator('pre').last()).toContainText('"meetings.requests": 1')
})

test('report defaults to safe Markdown preview; editing preserves exact text for preview and save', async ({ page }) => {
  const text = '# 日報 📘\n\n## 成果\n\n- **設計完了**\n- 次の作業\n\n|担当|状態|\n|---|---|\n|本人|完了|\n\n[根拠①](https://example.com/evidence)\n\n注記[^1]\n\n[^1]: 検証用の根拠。'
  const requests = await setup(page, { generate: [{ ...completion, text }] })
  await start(page)
  const preview = page.getByLabel('レポートプレビュー', { exact: true })
  await expect(preview.getByRole('heading', { name: '日報 📘', exact: true })).toBeVisible()
  await expect(preview.getByRole('listitem').filter({ hasText: '設計完了' })).toBeVisible()
  await expect(preview.getByRole('columnheader', { name: '担当', exact: true })).toBeVisible()
  await expect(preview.getByRole('link', { name: '根拠①', exact: true })).toHaveAttribute('rel', 'noopener noreferrer')
  await expect(preview.getByRole('link', { name: '根拠①', exact: true })).toHaveAttribute('target', '_blank')
  await expect(preview.getByRole('heading', { name: '注記', exact: true })).toBeVisible()
  await expect(page.getByLabel('レポート本文（編集可能）')).toHaveCount(0)
  await page.getByRole('button', { name: '編集', exact: true }).click()
  await expect(page.getByLabel('レポート本文（編集可能）')).toHaveValue(text)
  const edited = `${text}\n\n## 相談事項\n\n- **確認済み**`
  await page.getByLabel('レポート本文（編集可能）').fill(edited)
  await page.getByRole('button', { name: 'プレビュー', exact: true }).click()
  await expect(preview.getByRole('heading', { name: '相談事項', exact: true })).toBeVisible()
  await page.getByRole('button', { name: '編集', exact: true }).click()
  await expect(page.getByLabel('レポート本文（編集可能）')).toHaveValue(edited)
  await page.getByRole('button', { name: 'プレビュー', exact: true }).click()
  await page.getByRole('button', { name: '確認して SharePoint に保存' }).click()
  await expect(page.getByRole('link', { name: 'test-report.md' })).toBeVisible()
  expect(requests.find((request) => request.endpoint === 'save')?.body.text).toBe(edited)
})

test('Markdown does not execute HTML/unsafe links or fetch image resources', async ({ page }) => {
  const fetched: string[] = []
  page.on('request', (request) => {
    if (request.url().includes('track.invalid')) fetched.push(request.url())
  })
  await setup(page, { generate: [{
    ...completion,
    text: '# 安全な本文\n\n<script>window.markdownExecuted=true</script>\n\n<img src="https://track.invalid/html" onerror="window.markdownExecuted=true">\n\n![図の説明](https://track.invalid/markdown)\n\n[実行](javascript:alert%281%29) [難読化](java&#x73;cript:alert%281%29) [危険data](data:text/html,test)\n\n[正常](https://example.com/source)',
  }] })
  await start(page)
  const preview = page.getByLabel('レポートプレビュー', { exact: true })
  await expect(preview.getByRole('heading', { name: '安全な本文' })).toBeVisible()
  await expect(preview.getByText('［画像は読み込みません：図の説明］', { exact: true })).toBeVisible()
  await expect(preview.locator('script,img,iframe,object,embed')).toHaveCount(0)
  await expect(preview.getByRole('link')).toHaveCount(1)
  await expect(preview.getByRole('link', { name: '正常' })).toHaveAttribute('href', 'https://example.com/source')
  expect(await page.evaluate(() => (window as unknown as { markdownExecuted?: boolean }).markdownExecuted)).toBeUndefined()
  expect(fetched).toEqual([])
})

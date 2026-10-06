import { lazy, Suspense, useEffect, useRef, useState } from 'react'
import type { FormEvent } from 'react'
import { clearEditor, emptyEditor, loadEditor, saveEditor } from './session'
import { configFailureMessage } from './authentication'
import CoverageTable from './CoverageTable'
import { ReportApiError } from './api'
import type { ServiceDiagnostic } from './api'
import { exportCoverage, workIqToolLabel } from './coverage'
import {
  comparisonExport, comparisonMetrics, generateBounded, parseMembers, periodError,
  periodLabel, safeConsentUrl, safeReferenceUrl, waitForRetry,
} from './report'
import type {
  CompletedReport, Coverage, EditorState, GenerateRequest, PendingResponse, Period,
  Provider, ReportApi, ReportConfig, ResolveResponse, SavedReport, SaveStatus, WorkIqDiagnostics,
} from './types'

interface Props {
  identity: string
  provider: Provider
  api: ReportApi
  accountName: string
  onSignOut: () => void
  authenticationRevision?: number
}

const starters = ['8/1の日報作って', '8月の1週目の週報作って', '8/3〜8/7の上長週報作って']
const ReportMarkdown = lazy(() => import('./ReportMarkdown'))
function ReferenceLink({ url, children }: { url?: string; children: React.ReactNode }) {
  const safe = safeReferenceUrl(url)
  return safe ? <a href={safe} target="_blank" rel="noopener noreferrer">{children}</a> : <span>{children}（安全なリンクなし）</span>
}

export default function ReportWorkspace({ identity, provider, api, accountName, onSignOut, authenticationRevision = 0 }: Props) {
  const [editor, setEditor] = useState<EditorState>(() => loadEditor(identity))
  const [config, setConfig] = useState<ReportConfig | null>(null)
  const [configError, setConfigError] = useState('')
  const [configRevision, setConfigRevision] = useState(0)
  const [error, setError] = useState('')
  const [restartRequired, setRestartRequired] = useState(false)
  const [failedCoverage, setFailedCoverage] = useState<Coverage[] | null>(null)
  const [serviceFailure, setServiceFailure] = useState<ServiceDiagnostic | null>(null)
  const [diagnosticRunId, setDiagnosticRunId] = useState<string | null>(null)
  const [toolDiagnostics, setToolDiagnostics] = useState<WorkIqDiagnostics | null>(null)
  const [diagnosticError, setDiagnosticError] = useState('')
  const [diagnosticBusy, setDiagnosticBusy] = useState(false)
  const [diagnosticRevision, setDiagnosticRevision] = useState(0)
  const [storageWarning, setStorageWarning] = useState(false)
  const [busy, setBusy] = useState(false)
  const [status, setStatus] = useState('')
  const [resolution, setResolution] = useState<ResolveResponse | null>(null)
  const [request, setRequest] = useState<GenerateRequest | null>(null)
  const [pending, setPending] = useState<PendingResponse | null>(null)
  const [completed, setCompleted] = useState<CompletedReport | null>(null)
  const [reportMode, setReportMode] = useState<'preview' | 'edit'>('preview')
  const [saved, setSaved] = useState<SavedReport | null>(null)
  const [saveStatus, setSaveStatus] = useState<SaveStatus>('confirmed')
  const [revision, setRevision] = useState('')
  const [lastMessage, setLastMessage] = useState('')
  const active = useRef<AbortController | null>(null)
  const diagnosticRequest = useRef<AbortController | null>(null)
  const replyRunId = diagnosticRunId ?? completed?.diagnosticRunId ??
    (pending?.kind === 'partial_confirmation' ? pending.diagnosticRunId : undefined)

  useEffect(() => {
    if (!replyRunId) return
    const controller = new AbortController()
    diagnosticRequest.current = controller
    setDiagnosticBusy(true)
    setDiagnosticError('')
    setToolDiagnostics(null)
    void api.diagnostics(replyRunId, controller.signal).then((response) => {
      if (!controller.signal.aborted) setToolDiagnostics(response)
    }).catch((problem: unknown) => {
      if (!controller.signal.aborted)
        setDiagnosticError(problem instanceof Error ? problem.message : 'Work IQの応答を読み込めませんでした。')
    }).finally(() => {
      if (!controller.signal.aborted) setDiagnosticBusy(false)
    })
    return () => {
      controller.abort()
      if (diagnosticRequest.current === controller) diagnosticRequest.current = null
    }
  }, [api, replyRunId, identity, diagnosticRevision])

  useEffect(() => {
    setStorageWarning(!saveEditor(identity, editor))
  }, [identity, editor])

  useEffect(() => {
    const controller = new AbortController()
    setConfig(null)
    setConfigError('')
    void api.config(controller.signal).then((value) => {
      if (controller.signal.aborted) return
      setConfig(value)
      setConfigError('')
    }).catch((problem) => {
      if (!controller.signal.aborted) setConfigError(configFailureMessage(problem))
    })
    return () => controller.abort()
  }, [api, configRevision, authenticationRevision])

  useEffect(() => () => { active.current?.abort(); diagnosticRequest.current?.abort() }, [])

  function begin(preserveToolReplies = false) {
    active.current?.abort()
    const controller = new AbortController()
    active.current = controller
    setBusy(true)
    setError('')
    setRestartRequired(false)
    setFailedCoverage(null)
    setServiceFailure(null)
    if (!preserveToolReplies) clearToolDiagnostics()
    return controller
  }

  function isCurrent(controller: AbortController) {
    return active.current === controller && !controller.signal.aborted
  }

  function finish(controller: AbortController) {
    if (isCurrent(controller)) {
      active.current = null
      setBusy(false)
    }
  }

  function fail(controller: AbortController, problem: unknown) {
    if (!isCurrent(controller)) return
    setError(problem instanceof Error ? problem.message : 'リクエストに失敗しました。')
    setStatus('処理を完了できませんでした。下のエラーと取得状態を確認してください。')
    if (problem instanceof ReportApiError) {
      setServiceFailure(problem.diagnostic ?? null)
      setDiagnosticRunId(problem.diagnosticRunId ?? null)
      if (problem.restartRequired) {
        setRestartRequired(true)
        setPending(null)
        setRequest((current) => current ? {
          message: current.message, period: current.period, members: current.members, draft: current.draft,
        } : null)
      }
    }
    if (problem instanceof ReportApiError && problem.coverage) {
      setFailedCoverage(problem.coverage)
      setPending(null)
    }
  }

  function resetFlow() {
    active.current?.abort()
    active.current = null
    setBusy(false)
    setPending(null)
    setRequest(null)
    setResolution(null)
    setRestartRequired(false)
    setCompleted(null)
    setReportMode('preview')
    setSaved(null)
    setRevision('')
    setStatus('')
    setError('')
    setFailedCoverage(null)
    setServiceFailure(null)
    clearToolDiagnostics()
  }

  function clearToolDiagnostics() {
    diagnosticRequest.current?.abort()
    diagnosticRequest.current = null
    setDiagnosticRunId(null)
    setToolDiagnostics(null)
    setDiagnosticError('')
    setDiagnosticBusy(false)
  }

  function changePeriod(period: Period) {
    resetFlow()
    setEditor((current) => ({ ...current, period, draft: '' }))
  }

  async function resolve(message: string, explicit = false) {
    if (!message.trim()) return
    if (explicit) {
      const problem = periodError(editor.period)
      if (problem) { setError(problem); return }
    }
    const members = parseMembers(editor.members)
    const period = editor.period
    resetFlow()
    setEditor((current) => ({ ...current, draft: '' }))
    setLastMessage(message)
    const controller = begin()
    setStatus('対象期間を確認しています。まだ情報を取得していません。')
    try {
      const result = await api.resolve({ message, period, members }, controller.signal)
      if (!isCurrent(controller)) return
      setResolution(result)
      setStatus(result.message)
      setRequest({ message, period: result.period ?? period, members })
      if (result.period) setEditor((current) => ({ ...current, period: result.period!, draft: '' }))
    } catch (problem) { fail(controller, problem) }
    finally { finish(controller) }
  }

  function selectPeriod(period: Period) {
    if (!request) return
    const problem = periodError(period)
    if (problem) { setError(problem); return }
    setEditor((current) => ({ ...current, period, draft: '' }))
    setRequest({ ...request, period })
    setResolution({ kind: 'resolved', message: '選択した期間を確認してください。', period })
    setStatus('候補を選択しました。取得前に対象期間を確認してください。')
  }

  async function generate(body: GenerateRequest) {
    const problem = periodError(body.period)
    if (problem) { setError(problem); return }
    if (body.period.kind === 'managerWeekly' && !body.members?.length) {
      setError('上長週報には対象メンバーの ID / UPN を明示してください。')
      return
    }
    const controller = begin()
    setSaved(null)
    setCompleted(null)
    setPending(null)
    setStatus('指定期間の情報を取得・生成しています。')
    setRequest({
      message: body.message, period: body.period, members: body.members, draft: body.draft,
      ...(body.allowPartial ? { allowPartial: true } : {}),
    })
    try {
      if (pending?.kind === 'progress' && body.continuationToken === pending.continuationToken) {
        setStatus(`再開前に ${pending.retryAfterSeconds ?? 1} 秒待機しています。キャンセルできます。`)
        await waitForRetry(pending.retryAfterSeconds, controller.signal)
      }
      const result = await generateBounded(api.generate, body, controller.signal, (progress) => {
        if (isCurrent(controller)) {
          setStatus(`${progress.message}（次の取得まで ${progress.retryAfterSeconds ?? 1} 秒）`)
          setPending(progress)
        }
      })
      if (!isCurrent(controller)) return
      if (result.kind === 'completed') {
        setCompleted(result)
        setReportMode('preview')
        setEditor((current) => ({ ...current, period: result.period, draft: result.text }))
        setPending(null)
        setStatus('生成しました。まだ SharePoint へ保存していません。内容を確認・編集してください。')
      } else {
        setPending(result)
        setStatus(result.kind === 'progress'
          ? `${result.message} 自動継続を一時停止しました。「続きを取得」で再開できます。`
          : result.message)
      }
    } catch (problem) { fail(controller, problem) }
    finally { finish(controller) }
  }

  function continueRun(decision?: { approved?: boolean; allowPartial?: boolean }) {
    if (!pending || !request) return
    void generate({
      ...request,
      continuationToken: pending.continuationToken,
      ...(pending.kind === 'tool_approval_required' && decision?.approved !== undefined
        ? { approval: { requestId: pending.approvalRequestId, approved: decision.approved } } : {}),
      ...(decision?.allowPartial ? { allowPartial: true } : {}),
    })
  }

  async function save() {
    if (!completed || !config?.sharePointConfigured || busy || !editor.draft.trim()) return
    const controller = begin(true)
    setSaved(null)
    try {
      const result = await api.save({ runId: completed.runId, text: editor.draft, status: saveStatus }, controller.signal)
      if (isCurrent(controller)) {
        setSaved(result)
        setStatus('SharePoint に保存しました。')
      }
    } catch (problem) { fail(controller, problem) }
    finally { finish(controller) }
  }

  function exportRun() {
    if (!completed) return
    const blob = new Blob([JSON.stringify(comparisonExport(completed, provider, config), null, 2)], { type: 'application/json' })
    const url = URL.createObjectURL(blob)
    const anchor = document.createElement('a')
    anchor.href = url
    anchor.download = `report-comparison-${provider}.json`
    anchor.click()
    setTimeout(() => URL.revokeObjectURL(url), 0)
  }

  function submit(event: FormEvent) {
    event.preventDefault()
    void resolve(editor.input.trim())
  }

  const consentLink = pending?.kind === 'oauth_consent_required' ? safeConsentUrl(pending.consentLink) : null
  const mismatchedProvider = config !== null && config.provider !== provider

  return (
    <div className="app-shell">
      <header className="topbar">
        <div><span className="eyebrow">{provider === 'graph' ? 'Graph API' : 'Work IQ'} · テスト環境</span><h1>Report Chat</h1></div>
        <div className="account"><span>{accountName}</span><button className="ghost-button" onClick={onSignOut}>サインアウト</button></div>
      </header>
      <main className="workspace">
        <aside className="context-panel">
          <h2>対象期間</h2>
          <p className="period-summary" data-testid="period-summary">{periodLabel(editor.period)}</p>
          <p>基準年 {config?.testYear ?? 2026} ／ {config?.timeZone ?? 'Asia/Tokyo'}<br />未指定の日付を今日として取得しません。プロンプトの明示日付が以前の選択より優先されます。</p>
          <label className="field">レポート種別
            <select value={editor.period.kind} onChange={(event) => changePeriod({ kind: event.target.value as Period['kind'] })}>
              <option value="daily">本人日報</option><option value="personalWeekly">本人週報</option><option value="managerWeekly">上長週報</option>
            </select>
          </label>
          {editor.period.kind === 'daily' ? (
            <label className="field">日報日<input type="date" value={editor.period.reportDate ?? ''} onChange={(event) => changePeriod({ kind: 'daily', reportDate: event.target.value })} /></label>
          ) : (
            <>
              <label className="field">開始日<input type="date" value={editor.period.startDate ?? ''} onChange={(event) => changePeriod({ ...editor.period, startDate: event.target.value, previousBusinessDate: undefined })} /></label>
              <label className="field">終了日<input type="date" value={editor.period.endDate ?? ''} onChange={(event) => changePeriod({ ...editor.period, endDate: event.target.value, previousBusinessDate: undefined })} /></label>
            </>
          )}
          <label className="field">対象メンバー ID / UPN（上長週報）
            <textarea rows={3} value={editor.members} placeholder="member@example.com&#10;またはユーザー ID" onChange={(event) => {
              resetFlow()
              setEditor((current) => ({ ...current, members: event.target.value, draft: '' }))
            }} />
          </label>
          <small>空白・改行・カンマ区切りで明示します。Directory の自動探索は行いません。</small>
          <button className="ghost-button period-button" onClick={() => void resolve(
            editor.period.kind === 'daily' ? '指定した期間の日報を作成してください。'
              : editor.period.kind === 'managerWeekly' ? '指定した期間とメンバーの上長週報を作成してください。' : '指定した期間の本人週報を作成してください。', true,
          )}>この入力期間を使用</button>
          {editor.period.previousBusinessDate ? <p>実績対象は前営業日 {editor.period.previousBusinessDate}、予定対象は日報日です。月をまたぐ場合もこの日付で取得します。</p> : null}
          <p>週報は設定されたフォルダー内の確定日報を集約します。保存は生成とは別の明示操作です。</p>
          <button className="ghost-button" onClick={() => {
            resetFlow(); clearEditor(identity); setEditor(emptyEditor()); setLastMessage('')
          }}>会話と下書きをリセット</button>
        </aside>
        <section className="chat-panel report-panel" aria-label="レポートチャット">
          <div className="starters">{starters.map((starter) => <button key={starter} onClick={() => setEditor((current) => ({ ...current, input: starter }))}>{starter}</button>)}</div>
          {configError ? <div role="alert" className="warning-banner"><p>{configError}</p>
            <button className="ghost-button" onClick={() => setConfigRevision((value) => value + 1)}>構成の取得を再試行</button>
          </div> : null}
          {config && !config.sharePointConfigured ? <p role="status" className="warning-banner">保存先フォルダー未設定：SharePoint 保存と保存済み日報による週報の取得は利用できません。</p> : null}
          {mismatchedProvider ? <p role="alert" className="error-banner">画面と API の provider が一致しません。比較を中止し、接続先の設定を確認してください。</p> : null}
          {storageWarning ? <p role="status" className="warning-banner">ブラウザーへの下書き保存が利用できません。この画面を閉じると編集内容は失われます。</p> : null}
          <div className="messages">
            {!lastMessage && !completed ? <div className="empty-state"><h2>いつの日報・週報を作りますか？</h2><p>日付を含む依頼、または左の対象期間から開始してください。</p></div> : null}
            {lastMessage ? <article className="message user"><span>あなた</span><p>{lastMessage}</p></article> : null}
            {status ? <p className="message assistant" role="status" aria-live="polite">{status}</p> : null}
          </div>
          {resolution?.kind === 'needs_confirmation' ? <section className="action-card">
            <h2>期間の確認が必要です</h2><p>{resolution.message}</p>
            <div className="button-row">{resolution.choices?.map((choice, index) => <button className="ghost-button" key={index} disabled={busy} onClick={() => selectPeriod(choice.period)}>{choice.label}：{periodLabel(choice.period)}</button>)}</div>
            {!resolution.choices?.length ? <p>対象日を左側へ入力するか、日付を明示した依頼を送信してください。</p> : null}
          </section> : null}
          {resolution?.kind === 'resolved' && request && !completed && !pending ? <section className="action-card">
            <h2>取得対象を確認</h2><p>{periodLabel(request.period)}</p>
            {request.period.kind === 'managerWeekly' ? <p>メンバー：{request.members?.join(', ') || '未指定（入力が必要です）'}</p> : null}
            <button className="primary-button" disabled={busy || mismatchedProvider} onClick={() => void generate(request)}>
              {restartRequired ? '同じ期間で新規取得' : 'この期間で生成'}
            </button>
          </section> : null}
          {pending ? <section className="action-card">
            {pending.kind === 'progress' ? <>
              <h2>取得を継続できます</h2><p>{pending.message}</p>
              <button className="primary-button" disabled={busy || mismatchedProvider} onClick={() => continueRun()}>続きを取得</button>
            </> : null}
            {pending.kind === 'oauth_consent_required' ? <>
              <h2>Work IQ の接続同意</h2><p>{pending.message}</p>
              <p>別タブで同意を完了してから、この画面で再開してください。</p>
              {consentLink ? <a className="secondary-link" href={consentLink} target="_blank" rel="noopener noreferrer">接続の同意画面を開く</a>
                : <p role="alert" className="consent-warning">同意リンクを検証できません。処理を中止し、設定を確認してください。</p>}
              <button className="primary-button" disabled={busy || !consentLink || mismatchedProvider} onClick={() => continueRun()}>同意後に再開</button>
            </> : null}
            {pending.kind === 'tool_approval_required' ? <>
              <h2>ツール実行の承認</h2><p>{pending.message}</p><p>{pending.toolName}</p><pre className="tool-arguments">{pending.toolArguments}</pre>
              <div className="button-row"><button className="ghost-button" disabled={busy} onClick={() => continueRun({ approved: false })}>拒否する</button>
                <button className="primary-button" disabled={busy || mismatchedProvider} onClick={() => continueRun({ approved: true })}>承認して続ける</button></div>
            </> : null}
            {pending.kind === 'partial_confirmation' ? <>
              <h2>情報が不足しています</h2><p>{pending.message}</p><CoverageTable coverage={pending.coverage} />
              <button className="primary-button" disabled={busy || mismatchedProvider} onClick={() => continueRun({ allowPartial: true })}>不足を確認して生成</button>
            </> : null}
            <button className="ghost-button" onClick={() => { resetFlow(); setStatus('継続状態を破棄しました。新しい依頼から開始してください。') }}>この処理を中止</button>
          </section> : null}
          {completed ? <section className="report-result" aria-label="生成結果">
            <h2>レポート案</h2><p>{periodLabel(completed.period)}</p>
            <div className="report-mode-switch" role="group" aria-label="レポートの表示切替">
              <button type="button" className="ghost-button" aria-pressed={reportMode === 'preview'}
                onClick={() => setReportMode('preview')}>プレビュー</button>
              <button type="button" className="ghost-button" aria-pressed={reportMode === 'edit'}
                onClick={() => setReportMode('edit')}>編集</button>
            </div>
            {reportMode === 'preview' ? <section className="report-preview" aria-label="レポートプレビュー">
              <Suspense fallback={<p role="status">プレビューを準備しています。</p>}>
                <ReportMarkdown text={editor.draft} />
              </Suspense>
            </section> : <label className="field">レポート本文（編集可能）
              <textarea className="report-editor" value={editor.draft} disabled={busy} onChange={(event) => {
                setEditor((current) => ({ ...current, draft: event.target.value })); setSaved(null)
              }} />
            </label>}
            <label className="field">修正指示<input value={revision} disabled={busy} onChange={(event) => setRevision(event.target.value)} placeholder="課題とネクストアクションを簡潔に" /></label>
            <button className="ghost-button" disabled={busy || !revision.trim() || mismatchedProvider} onClick={() => {
              void generate({ message: revision.trim(), period: completed.period, members: request?.members, draft: editor.draft, continuationToken: completed.runId })
            }}>編集内容を使って再生成</button>
            <p>再生成はこの対象期間を維持します。別の日付は新しい依頼または対象期間から開始してください。</p>
            <CoverageTable coverage={completed.coverage} />
            <details><summary>根拠・参照リンク ({completed.evidence.length})</summary>
              <ul className="evidence">{completed.evidence.map((item, index) => <li key={`${item.id}-${index}`}>
                <ReferenceLink url={item.url}>{item.sourceType} · {item.id}</ReferenceLink><p>{item.text}</p>
                {item.verificationStatus === 'verified' ? <p>
                  {workIqToolLabel(item.acquisitionTool)}の原文・メタデータを確認済み（成果や実施完了を認定するものではありません）。
                  {item.attribution === 'self' ? ' 発言者：本人。' : item.attribution === 'other' ? ' 発言者：他者。' : ''}
                  {item.activityStatus === 'planned' ? ' 予定情報（参加・完了は未確認）。' : item.activityStatus === 'unknown' ? ' 業務状態は未分類。' : ''}
                  {item.timestampPrecision === 'minute' ? ' 日時は分精度。' : item.timestampPrecision === 'day' ? ' 日時は日付精度。' : ''}
                </p> : null}
              </li>)}</ul>
            </details>
            <details><summary>比較メトリクス・生成条件</summary>
              <p>Run: {completed.runId}</p>
              <pre className="metric-json">{JSON.stringify(comparisonMetrics(completed.metrics), null, 2)}</pre>
              <p>指示 hash: {config?.promptHash ?? '未確認'} ／ モデル: {config?.modelDeployment ?? '未確認'} ({config?.modelVersion ?? '未確認'})</p>
              <p>人手による品質評価は未実施です。方式全体と最終生成の指標を分けて比較してください。</p>
            </details>
            <div className="button-row">
              <button className="ghost-button" onClick={exportRun}>比較メタデータ JSON</button>
              <label className="field">保存状態<select value={saveStatus} disabled={busy} onChange={(event) => setSaveStatus(event.target.value as SaveStatus)}>
                <option value="confirmed">本人確認済み</option><option value="test-generated">テスト生成（未確認）</option>
              </select></label>
              <button className="primary-button" disabled={busy || !config?.sharePointConfigured || !editor.draft.trim() || !!saved || mismatchedProvider} onClick={() => void save()}>確認して SharePoint に保存</button>
            </div>
            {saved ? <p role="status">保存成功：<ReferenceLink url={saved.webUrl}>{saved.fileName}</ReferenceLink></p> : null}
          </section> : null}
          {!completed && !busy && editor.draft ? <details><summary>復元した編集内容（再取得・確認が必要です）</summary>
            <label className="field">復元した下書き<textarea className="report-editor" value={editor.draft} onChange={(event) => setEditor((current) => ({ ...current, draft: event.target.value }))} /></label>
            <p>{periodLabel(editor.period)}。元の取得状態は保存していません。この期間で再取得し、下書きを修正指示と共に送信します。</p>
            <label className="field">下書きの修正指示<input value={revision} onChange={(event) => setRevision(event.target.value)} /></label>
            <button className="ghost-button" disabled={!revision.trim() || !!periodError(editor.period) || mismatchedProvider} onClick={() => {
              void generate({ message: revision.trim(), period: editor.period, members: parseMembers(editor.members), draft: editor.draft })
            }}>対象期間を確認して下書きから再生成</button>
          </details> : null}
          {error ? <p role="alert" className="error-banner">{error}</p> : null}
          {replyRunId ? <section className="action-card" aria-label="Work IQ実応答の診断">
            <h2>Work IQが実際に返した回答</h2>
            <p>取得Agentによる要約・期間除外の前のWork IQ応答です。成功・失敗にかかわらず本人の結果を自動表示します。認証情報等は除去しています。業務情報を含むため公開しないでください。</p>
            <p>原応答には期間外の参考情報・未確認の日時・システムイベントが含まれる場合があります。日報に採用した根拠とは区別してください。askの文章と、fetch／call_functionの構造化データを取得ツール別に表示します。</p>
            {diagnosticBusy ? <p role="status">本人の実行結果を確認しています。</p> : null}
            {diagnosticError ? <><p role="alert">{diagnosticError}</p>
              <button className="ghost-button" type="button" disabled={diagnosticBusy}
                onClick={() => setDiagnosticRevision((value) => value + 1)}>Work IQ応答の表示を再試行</button>
            </> : null}
            {toolDiagnostics ? <div>
              <p>{toolDiagnostics.note}</p>
              {toolDiagnostics.acquisitionProfile === 'verified-workiq-v1' ? <p>検証付き取得：予定は構造化データ、Chatは検索候補と原文確認を分離しています。</p> : null}
              {toolDiagnostics.replies.length === 0 ? <p>保存された実応答がありません。更新前の成功runでは応答を保持していなかったため、過去の返答はこの表示から復元できません。</p> : null}
              {toolDiagnostics.replies.map((reply) => <section key={reply.sequence}>
                <h3>{workIqToolLabel(reply.toolName)}応答 {reply.sequence}</h3>
                <p>形式：{reply.format} ／ 構造化エラー：{reply.isError ? 'あり' : '未検出'}
                  {reply.statusCode ? ` ／ statusCode：${reply.statusCode}` : ''}
                  {reply.errorCode ? ` ／ code：${reply.errorCode}` : ''}</p>
                <pre className="tool-arguments">{reply.text || '表示可能な回答本文を取得できませんでした。'}</pre>
                {reply.truncated ? <p>回答本文の表示上限に達しています。下の受信形式も確認してください。</p> : null}
                {reply.rawText != null ? <details>
                  <summary>受信した応答（認証情報等を除去・JSON形式）</summary>
                  <pre className="tool-arguments">{reply.rawText}</pre>
                  {reply.rawTruncated ? <p>診断プレビューの保存上限により一部を省略しています。表示用の抜粋は完全なJSONとは限りません。</p> : null}
                </details> : <p>この旧実行には受信形式の記録がありません。上記は保存済みの回答本文です。</p>}
              </section>)}
              {toolDiagnostics.latestResponseReplies?.some((reply) => reply.isError) ? <section aria-label="保存済みFoundry応答のツールエラー">
                <h3>保存済みFoundry応答で確認したツールエラー</h3>
                <p>直近の取得応答を読み直した結果です。新しいWork IQ検索は実行していません。上の旧記録が空でも、こちらに元のエラーが残っている場合があります。</p>
                {toolDiagnostics.latestResponseReplies.filter((reply) => reply.isError).map((reply) => <div key={reply.sequence}>
                  <p>この取得応答内の{workIqToolLabel(reply.toolName)} {reply.sequence} ／ code：{reply.errorCode ?? '未報告'} ／ HTTP：{reply.statusCode ?? '未報告'}</p>
                  <pre className="tool-arguments">{reply.text || '応答本文なし。受信形式を確認してください。'}</pre>
                  <details open><summary>受信エラー（認証情報等を除去）</summary>
                    <pre className="tool-arguments">{reply.rawText ?? '記録なし'}</pre>
                  </details>
                </div>)}
              </section> : null}
            </div> : null}
          </section> : null}
          {serviceFailure ? <section className="action-card" aria-label="サービス接続の診断">
            <h2>失敗した処理段階</h2>
            <p>本文や認証情報を含まない診断です。HTTPステータスがない通信失敗を、権限不足やデータ0件とは扱いません。</p>
            <pre className="metric-json">{JSON.stringify(serviceFailure, null, 2)}</pre>
          </section> : null}
          {failedCoverage ? <section className="action-card" aria-label="失敗した取得の診断">
            <h2>生成できなかった理由</h2>
            <p>取得結果が空だった場合と、取得後に形式・期間の検査で除外した場合を区別します。根拠なしの日報は生成していません。</p>
            <CoverageTable coverage={failedCoverage} expandDetails />
            <details><summary>取得診断 JSON（本文・認証情報なし）</summary>
              <pre className="metric-json">{JSON.stringify(failedCoverage.map(exportCoverage), null, 2)}</pre>
            </details>
          </section> : null}
          {busy ? <button className="ghost-button" onClick={() => { resetFlow(); setStatus('キャンセルしました。継続状態は破棄しました。') }}>リクエストをキャンセル</button> : null}
          <form className="composer" onSubmit={submit}>
            <label htmlFor="report-prompt">日報・週報の依頼</label>
            <textarea id="report-prompt" value={editor.input} onChange={(event) => setEditor((current) => ({ ...current, input: event.target.value }))} placeholder="例：8/1の日報作って" />
            <div className="composer-actions"><span>新しい依頼は前の継続状態・根拠・下書きを破棄します。</span>
              <button className="primary-button" disabled={!editor.input.trim() || mismatchedProvider} type="submit">対象期間を確認</button>
            </div>
          </form>
        </section>
      </main>
    </div>
  )
}

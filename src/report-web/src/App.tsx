import { useEffect, useMemo, useRef, useState } from 'react'
import { InteractionStatus } from '@azure/msal-browser'
import { useIsAuthenticated, useMsal } from '@azure/msal-react'
import { loginRequest } from './auth'
import { reportProvider, runtimeConfig } from './config'
import { createReportApi } from './api'
import { clearEditor, sessionKey } from './session'
import ReportWorkspace from './ReportWorkspace'
import {
  acquireSilentApiToken, AuthenticationFailure, classifyAuthenticationFailure,
  continueAuthentication, sameAccount,
} from './authentication'
import './App.css'

export default function App() {
  const { instance, accounts, inProgress } = useMsal()
  const isAuthenticated = useIsAuthenticated()
  const account = useMemo(() => instance.getActiveAccount() ?? accounts[0], [instance, accounts])
  const identity = isAuthenticated && account
    ? sessionKey(account.tenantId || runtimeConfig.tenantId, account.homeAccountId, reportProvider) : ''
  const previousIdentity = useRef(identity)
  const [error, setError] = useState('')
  const [signingOut, setSigningOut] = useState(false)
  const [authIssue, setAuthIssue] = useState<{ identity: string; failure: AuthenticationFailure } | null>(null)
  const [authRevision, setAuthRevision] = useState(0)
  const [recovering, setRecovering] = useState(false)
  const currentIssue = authIssue?.identity === identity ? authIssue.failure : null
  const api = useMemo(() => createReportApi(runtimeConfig.apiUrl, async () => {
    if (!account) throw new Error('サインインが必要です。')
    try {
      return await acquireSilentApiToken(instance, { ...loginRequest, account })
    } catch (error) {
      const failure = classifyAuthenticationFailure(error)
      setAuthIssue({ identity, failure })
      throw failure
    }
  }), [instance, account, identity])

  useEffect(() => {
    if (previousIdentity.current && previousIdentity.current !== identity) clearEditor(previousIdentity.current)
    previousIdentity.current = identity
  }, [identity])

  async function recoverAuthentication() {
    if (!account || recovering || inProgress !== InteractionStatus.None || currentIssue?.kind !== 'interaction_required') return
    setRecovering(true)
    setError('')
    try {
      const result = await continueAuthentication(instance, { ...loginRequest, account }, currentIssue)
      if (!sameAccount(account, result.account)) throw new Error('認証したアカウントが変更されています。サインアウトしてやり直してください。')
      setAuthIssue(null)
      setAuthRevision((value) => value + 1)
    } catch (problem) {
      setError(problem instanceof AuthenticationFailure ? problem.message : '追加認証を完了できませんでした。アカウントを確認して再試行してください。')
    } finally { setRecovering(false) }
  }

  async function signIn() {
    setError('')
    try {
      const result = await instance.loginPopup(loginRequest)
      instance.setActiveAccount(result.account)
    } catch { setError('サインインに失敗しました。もう一度お試しください。') }
  }

  async function signOut() {
    clearEditor(identity)
    setSigningOut(true)
    try {
      await instance.logoutPopup({ account, mainWindowRedirectUri: window.location.origin })
    } catch { setError('サインアウトに失敗しました。もう一度お試しください。') }
    finally { setSigningOut(false) }
  }

  if (!identity || signingOut) return (
    <main className="signin-shell">
      <section className="signin-card">
        <span className="eyebrow">{reportProvider === 'graph' ? 'Graph API' : 'Work IQ'} · テスト環境</span>
        <h1>Report Chat</h1>
        <p>対象日を指定して、日報・本人週報・上長週報を作成します。生成した案を編集し、確認後に明示的に保存できます。</p>
        <button className="primary-button" disabled={inProgress !== InteractionStatus.None || signingOut} onClick={() => void signIn()}>Microsoft でサインイン</button>
        {error ? <p role="alert" className="error-banner">{error}</p> : null}
      </section>
    </main>
  )

  return <>
    {error ? <p role="alert" className="error-banner">{error}</p> : null}
    {currentIssue?.kind === 'interaction_required' ? <div className="warning-banner" role="alert">
      <span>{currentIssue.message}</span>
      <button className="primary-button" disabled={recovering || inProgress !== InteractionStatus.None} onClick={() => void recoverAuthentication()}>認証を続ける</button>
    </div> : null}
    <ReportWorkspace key={identity} identity={identity} provider={reportProvider} api={api} authenticationRevision={authRevision}
      accountName={account?.name ?? account?.username ?? ''} onSignOut={() => void signOut()} />
  </>
}

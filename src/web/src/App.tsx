import { useEffect, useRef, useState } from 'react'
import type { FormEvent } from 'react'
import { InteractionStatus } from '@azure/msal-browser'
import { useIsAuthenticated, useMsal } from '@azure/msal-react'
import { getServiceStatus, sendChat } from './api'
import ConsentActionCard from './ConsentActionCard'
import type {
  ChatMessage,
  OAuthConsentRequiredResponse,
  PendingContinuation,
  ToolApprovalRequiredResponse,
} from './types'
import {
  clearSession,
  loadMessages,
  loadPendingContinuation,
  saveMessages,
  savePendingContinuation,
} from './session'
import { loginRequest } from './auth'
import './App.css'

const promptStarters = [
  '私のプロフィールと、過去90日の主な活動をまとめてください。',
  '過去90日の仕事から、根拠のある行動傾向を整理してください。',
  '私がアクセスできる情報だけを使って、指定した人物のプロフィールをまとめてください。',
]

function App() {
  const { instance, accounts, inProgress } = useMsal()
  const isAuthenticated = useIsAuthenticated()
  const [messages, setMessages] = useState<ChatMessage[]>(loadMessages)
  const [pending, setPending] = useState<PendingContinuation | null>(
    loadPendingContinuation,
  )
  const [input, setInput] = useState('')
  const [error, setError] = useState<string | null>(null)
  const [secretWarning, setSecretWarning] = useState<string | null>(null)
  const [isSending, setIsSending] = useState(false)
  const abortController = useRef<AbortController | null>(null)

  useEffect(() => {
    saveMessages(messages)
  }, [messages])

  useEffect(() => {
    savePendingContinuation(pending)
  }, [pending])

  useEffect(() => {
    if (!isAuthenticated || !accounts[0]) return

    const controller = new AbortController()
    void instance
      .acquireTokenSilent({ ...loginRequest, account: accounts[0] })
      .then((token) => getServiceStatus(token.accessToken, controller.signal))
      .then((status) => {
        if (status?.secretRotationRequired) {
          setSecretWarning(
            status.secretDaysRemaining === null
              ? 'Work IQ OAuth credential の有効期限を確認してください。'
              : `Work IQ OAuth credential は残り ${status.secretDaysRemaining} 日です。rotation script を実行してください。`,
          )
        }
      })
      .catch(() => {
        // Status is advisory and must not block chat.
      })

    return () => controller.abort()
  }, [accounts, instance, isAuthenticated])

  const signIn = async () => {
    setError(null)
    try {
      await instance.loginPopup(loginRequest)
    } catch (signInError) {
      setError(
        signInError instanceof Error
          ? `サインインに失敗しました: ${signInError.message}`
          : 'サインインに失敗しました。',
      )
    }
  }

  const signOut = async () => {
    abortController.current?.abort()
    clearSession()
    setMessages([])
    setPending(null)
    await instance.logoutPopup({
      account: accounts[0],
      mainWindowRedirectUri: window.location.origin,
    })
  }

  const acquireApiToken = async () => {
    const account = accounts[0]
    if (!account) {
      throw new Error('サインインが必要です。')
    }

    try {
      return (
        await instance.acquireTokenSilent({
          ...loginRequest,
          account,
        })
      ).accessToken
    } catch {
      return (await instance.acquireTokenPopup(loginRequest)).accessToken
    }
  }

  const runRequest = async (
    message: string,
    continuation?: PendingContinuation,
    approval?: { requestId: string; approved: boolean },
  ) => {
    setError(null)
    setIsSending(true)
    abortController.current = new AbortController()

    try {
      const token = await acquireApiToken()
      const result = await sendChat(
        token,
        {
          message,
          previousResponseId: continuation?.responseId,
          continueAfterConsent: continuation?.kind === 'oauth',
          approval,
        },
        abortController.current.signal,
      )

      if (result.kind === 'oauth_consent_required') {
        const consent = result as OAuthConsentRequiredResponse
        const next: PendingContinuation = {
          kind: 'oauth',
          responseId: consent.responseId,
          originalMessage: message,
          consentLink: consent.consentLink,
          consentCreatedAt: Date.now(),
        }
        setPending(next)
        return
      }

      if (result.kind === 'tool_approval_required') {
        const tool = result as ToolApprovalRequiredResponse
        setPending({
          kind: 'approval',
          responseId: tool.responseId,
          originalMessage: message,
          approvalRequestId: tool.approvalRequestId,
          toolName: tool.toolName,
          toolArguments: tool.toolArguments,
        })
        return
      }

      setMessages((current) => [
        ...current,
        {
          id: crypto.randomUUID(),
          role: 'assistant',
          text: result.text,
        },
      ])
      setPending(null)
    } catch (requestError) {
      if (requestError instanceof DOMException && requestError.name === 'AbortError') {
        setError('リクエストをキャンセルしました。')
      } else {
        setError(
          requestError instanceof Error
            ? requestError.message
            : 'リクエストに失敗しました。',
        )
      }
    } finally {
      abortController.current = null
      setIsSending(false)
    }
  }

  const submit = async (event: FormEvent) => {
    event.preventDefault()
    const message = input.trim()
    if (!message || isSending) return

    setMessages((current) => [
      ...current,
      { id: crypto.randomUUID(), role: 'user', text: message },
    ])
    setInput('')
    await runRequest(message)
  }

  const continueAfterConsent = async () => {
    if (pending?.kind !== 'oauth') return
    await runRequest(pending.originalMessage, pending)
  }

  const refreshConsentLink = async () => {
    if (pending?.kind !== 'oauth') return
    const originalMessage = pending.originalMessage
    setPending(null)
    await runRequest(originalMessage)
  }

  const copyConsentLink = async () => {
    if (pending?.kind !== 'oauth' || !pending.consentLink) return

    try {
      await navigator.clipboard.writeText(pending.consentLink)
      setError('認証URLをコピーしました。新しいタブのアドレスバーへ貼り付けてください。')
    } catch {
      setError(
        '認証URLをコピーできませんでした。「同じタブで開く」を使用してください。',
      )
    }
  }

  const answerApproval = async (approved: boolean) => {
    if (pending?.kind !== 'approval' || !pending.approvalRequestId) return
    await runRequest(pending.originalMessage, pending, {
      requestId: pending.approvalRequestId,
      approved,
    })
  }

  if (!isAuthenticated) {
    return (
      <main className="signin-shell">
        <section className="signin-card" aria-labelledby="signin-title">
          <span className="eyebrow">Microsoft 365 grounded profile</span>
          <h1 id="signin-title">Work IQ Profile Chat</h1>
          <p>
            あなたがアクセスできるメール、会議、Teams、ファイルの範囲だけを使い、
            プロフィールと最近の仕事を整理します。
          </p>
          {error ? <p className="error-banner" role="alert">{error}</p> : null}
          <button
            className="primary-button"
            type="button"
            onClick={signIn}
            disabled={inProgress !== InteractionStatus.None}
          >
            Microsoft Entra ID でサインイン
          </button>
          <p className="privacy-note">
            他者の private mailbox、private chat、private file にはアクセスしません。
          </p>
        </section>
      </main>
    )
  }

  return (
    <div className="app-shell">
      <header className="topbar">
        <div>
          <span className="eyebrow">Work IQ grounded</span>
          <h1>Profile Chat</h1>
        </div>
        <div className="account">
          <span>{accounts[0]?.username}</span>
          <button className="ghost-button" type="button" onClick={signOut}>
            サインアウト
          </button>
        </div>
      </header>

      <main className="workspace">
        <aside className="context-panel">
          <h2>分析の境界</h2>
          <dl>
            <div>
              <dt>既定期間</dt>
              <dd>過去90日</dd>
            </div>
            <div>
              <dt>データ範囲</dt>
              <dd>サインインユーザーが閲覧できる Microsoft 365 情報</dd>
            </div>
            <div>
              <dt>特性</dt>
              <dd>仕事上の観察可能な行動傾向のみ</dd>
            </div>
          </dl>
          <p>
            結果は不完全な場合があります。人物評価や、健康・私生活・保護対象属性の
            推定には使用しないでください。
          </p>
        </aside>

        <section className="chat-panel" aria-label="プロフィールチャット">
          {secretWarning ? (
            <p className="warning-banner" role="status">{secretWarning}</p>
          ) : null}
          <div className="starters" aria-label="質問例">
            {promptStarters.map((starter) => (
              <button
                type="button"
                key={starter}
                onClick={() => setInput(starter)}
                disabled={isSending}
              >
                {starter}
              </button>
            ))}
          </div>

          <div className="messages" aria-live="polite">
            {messages.length === 0 ? (
              <div className="empty-state">
                <h2>Microsoft 365 の仕事情報を、根拠と限界付きで整理します。</h2>
                <p>質問例を選ぶか、人物名と確認したい期間を入力してください。</p>
              </div>
            ) : (
              messages.map((message) => (
                <article
                  className={`message ${message.role}`}
                  key={message.id}
                >
                  <span>{message.role === 'user' ? 'You' : 'Work IQ'}</span>
                  <p>{message.text}</p>
                </article>
              ))
            )}
          </div>

          {pending?.kind === 'oauth' ? (
            <ConsentActionCard
              pending={pending}
              isSending={isSending}
              onContinue={continueAfterConsent}
              onRefresh={refreshConsentLink}
              onCopy={copyConsentLink}
            />
          ) : null}

          {pending?.kind === 'approval' ? (
            <div className="action-card" role="status">
              <h2>ツール実行の確認</h2>
              <p>
                Work IQ の read-only tool「{pending.toolName}」を実行しますか？
              </p>
              <pre className="tool-arguments">{pending.toolArguments}</pre>
              <div className="button-row">
                <button
                  className="ghost-button"
                  type="button"
                  onClick={() => answerApproval(false)}
                  disabled={isSending}
                >
                  拒否
                </button>
                <button
                  className="primary-button"
                  type="button"
                  onClick={() => answerApproval(true)}
                  disabled={isSending}
                >
                  許可
                </button>
              </div>
            </div>
          ) : null}

          {error ? <p className="error-banner" role="alert">{error}</p> : null}

          <form className="composer" onSubmit={submit}>
            <label htmlFor="message">質問</label>
            <textarea
              id="message"
              value={input}
              onChange={(event) => setInput(event.target.value)}
              placeholder="例: 過去90日の顧客対応から、私の主な活動と観察できる傾向を根拠付きでまとめて"
              maxLength={8000}
              rows={3}
              disabled={isSending || pending !== null}
            />
            <div className="composer-actions">
              <span>{input.length.toLocaleString()} / 8,000</span>
              {isSending ? (
                <button
                  className="ghost-button"
                  type="button"
                  onClick={() => abortController.current?.abort()}
                >
                  キャンセル
                </button>
              ) : null}
              <button
                className="primary-button"
                type="submit"
                disabled={!input.trim() || isSending || pending !== null}
              >
                {isSending ? '処理中…' : '送信'}
              </button>
            </div>
          </form>
        </section>
      </main>
    </div>
  )
}

export default App

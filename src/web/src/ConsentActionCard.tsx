import { isAllowedConsentUrl } from './consent'
import type { PendingContinuation } from './types'

interface ConsentActionCardProps {
  pending: PendingContinuation
  isSending: boolean
  onContinue: () => void
  onRefresh: () => void
  onCopy: () => void
}

function ConsentActionCard({
  pending,
  isSending,
  onContinue,
  onRefresh,
  onCopy,
}: ConsentActionCardProps) {
  const hasFreshLink =
    Boolean(pending.consentCreatedAt) &&
    Boolean(pending.consentLink) &&
    isAllowedConsentUrl(pending.consentLink ?? '')

  return (
    <div className="action-card" role="status">
      <h2>Work IQ の認証が必要です</h2>
      <p>
        「認証ページを開く」を押して、新しいタブで認証してください。
        認証後に空白または完了ページが表示された場合は、そのタブを閉じて
        この画面へ戻り、処理を続行してください。
      </p>
      {!pending.consentCreatedAt ? (
        <p className="consent-warning">
          この認証リンクは旧バージョンで発行されています。先にリンクを再発行してください。
        </p>
      ) : null}
      {pending.consentLink && !isAllowedConsentUrl(pending.consentLink) ? (
        <p className="consent-warning">
          認証リンクの形式を検証できません。リンクを再発行してください。
        </p>
      ) : null}
      {pending.consentCreatedAt ? (
        <p className="consent-timestamp">
          リンク発行: {new Date(pending.consentCreatedAt).toLocaleTimeString()}
        </p>
      ) : null}
      <div className="button-row">
        {hasFreshLink ? (
          <>
            <a
              className="secondary-link"
              href={pending.consentLink}
              target="_blank"
              rel="noopener noreferrer"
            >
              認証ページを開く
            </a>
            <a className="ghost-link" href={pending.consentLink}>
              同じタブで開く
            </a>
            <button className="ghost-button" type="button" onClick={onCopy}>
              認証URLをコピー
            </button>
          </>
        ) : null}
        <button
          className="primary-button"
          type="button"
          onClick={onContinue}
          disabled={isSending}
        >
          認証完了後に続行
        </button>
        <button
          className="ghost-button"
          type="button"
          onClick={onRefresh}
          disabled={isSending}
        >
          {isSending ? '再発行中…' : '認証リンクを再発行'}
        </button>
      </div>
    </div>
  )
}

export default ConsentActionCard

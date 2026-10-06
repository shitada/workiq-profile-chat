import { InteractionRequiredAuthError } from '@azure/msal-browser'
import type { AccountInfo, IPublicClientApplication, PopupRequest, SilentRequest } from '@azure/msal-browser'

export type AuthenticationFailureKind = 'interaction_required' | 'timeout' | 'failed'

export class AuthenticationFailure extends Error {
  readonly kind: AuthenticationFailureKind
  constructor(kind: AuthenticationFailureKind) {
    super(kind === 'interaction_required'
      ? 'API を呼び出す前に追加の認証操作が必要です。「認証を続ける」を押してください。'
      : kind === 'timeout'
        ? 'API を呼び出す前の認証がタイムアウトしました（MSAL timed_out）。認証用 iframe / redirect.html の通信またはブラウザーの制限を確認してください。'
        : 'API を呼び出す前の認証に失敗しました。サインイン状態とアプリの認証設定を確認してください。')
    this.name = 'AuthenticationFailure'
    this.kind = kind
  }
}

export function classifyAuthenticationFailure(error: unknown): AuthenticationFailure {
  if (error instanceof AuthenticationFailure) return error
  if (error instanceof InteractionRequiredAuthError) return new AuthenticationFailure('interaction_required')
  const code = error && typeof error === 'object' && 'errorCode' in error ? error.errorCode : undefined
  return new AuthenticationFailure(code === 'timed_out' || code === 'monitor_window_timeout' ? 'timeout' : 'failed')
}

export async function acquireSilentApiToken(
  instance: Pick<IPublicClientApplication, 'acquireTokenSilent'>,
  request: SilentRequest,
): Promise<string> {
  try {
    return (await instance.acquireTokenSilent(request)).accessToken
  } catch (error) {
    // Never open a popup from a config effect or after an asynchronous silent request.
    throw classifyAuthenticationFailure(error)
  }
}

export function continueAuthentication(
  instance: Pick<IPublicClientApplication, 'acquireTokenPopup'>,
  request: PopupRequest,
  failure: AuthenticationFailure | null,
): ReturnType<IPublicClientApplication['acquireTokenPopup']> {
  if (failure?.kind !== 'interaction_required') {
    return Promise.reject(new Error('追加認証が必要な場合のみ、明示操作で認証を続行できます。'))
  }
  // Called directly in a click handler, before any await, preserving user activation.
  return instance.acquireTokenPopup(request)
}

export function sameAccount(expected: AccountInfo, actual: AccountInfo | null): boolean {
  return !!actual && expected.homeAccountId === actual.homeAccountId && expected.tenantId === actual.tenantId
}

export function configFailureMessage(error: unknown): string {
  if (error instanceof AuthenticationFailure) return `構成未取得：${error.message} 保存は無効です。`
  return '構成を確認できません。API の接続・アクセス権を確認して再試行してください。保存は無効です。'
}

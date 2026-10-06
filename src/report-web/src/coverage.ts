import type { Coverage } from './types'

interface ReasonExplanation { category: string; description: string }
const reasonExplanations: Record<string, ReasonExplanation> = {
  collector_returned_no_evidence: {
    category: 'Work IQ根拠なし', description: '取得Agentからこのカテゴリの根拠が返りませんでした。元データの不存在やアクセス拒否までは判定できません。',
  },
  collector_evidence_metadata_missing: {
    category: '根拠の形式不足', description: '取得結果の日時・出典・区分が不足し、検証できない根拠を除外しました。元データがないという意味ではありません。',
  },
  collector_category_invalid: {
    category: '根拠の区分不正', description: '取得結果の情報源と実績・予定の区分が一致せず除外しました。',
  },
  collector_out_of_period_discarded: {
    category: '期間外の根拠除外', description: 'Work IQが返した日時が指定の対象期間外だったため除外しました。対象日を自動で変更していません。',
  },
  collector_empty_content: {
    category: '根拠本文が空', description: '取得Agentが返した根拠に、生成へ渡せる本文がありませんでした。',
  },
  collector_tool_execution_failed: {
    category: 'Work IQツールエラー', description: 'Work IQツールの実行エラーを観測しました。アクセス拒否・認証・通信等のどれかは、これだけでは断定できません。',
  },
  collector_tool_not_called: {
    category: 'Work IQ未実行', description: '取得Agentによるaskの実行を確認できませんでした。データを照会できたとは扱いません。',
  },
  no_accessible_chats_returned: {
    category: '一覧取得・0件', description: 'アクセス可能なチャット一覧は0件でした。組織全体のチャット不存在やアクセス拒否を意味しません。',
  },
  no_messages_returned_for_query: {
    category: '照会結果・0件', description: '実行したメッセージ照会では0件でした。照会範囲外までデータがないとは判定していません。',
  },
  no_messages_in_activity_period: {
    category: '対象期間・0件', description: '取得したメッセージのうち、実績対象期間に一致するものが0件でした。',
  },
  no_usable_message_content: {
    category: '利用可能な本文・0件', description: '取得したメッセージに生成へ利用できる本文がありませんでした。メッセージ自体の不存在とは異なります。',
  },
  no_online_meeting_join_urls_in_activity_calendar: {
    category: '会議URL未検出', description: '実績対象の予定からオンライン会議URLを検出できませんでした。他の範囲の会議や文字起こしの不存在は判定できません。',
  },
  calendar_incomplete_no_online_meeting_join_urls: {
    category: '予定取得未完了', description: '予定の取得が未完了で会議URLを検出できませんでした。URLが存在しないとは断定できません。',
  },
  online_meeting_lookup_empty: {
    category: '会議照会・0件', description: 'オンライン会議の照会結果は0件でした。この結果だけでは元の会議が存在しない理由を判定できません。',
  },
  transcript_list_empty_recording_or_retention_unknown: {
    category: '文字起こし一覧・0件', description: '文字起こし一覧は0件でした。未作成・録画設定・保持期限などの原因は不明です。',
  },
  transcript_content_empty: {
    category: '文字起こし本文・空', description: '取得した文字起こし本文が空でした。',
  },
  no_transcripts_in_activity_period: {
    category: '対象期間・0件', description: '取得した文字起こしのうち、実績対象期間に一致するものが0件でした。',
  },
  shared_channels_not_discovered: {
    category: '未探索',
    description: '共有チャネルは探索していません。共有チャネルのデータがないという意味ではありません。',
  },
  input_budget_exhausted: {
    category: '入力上限',
    description: '生成入力の上限に達したため、取得情報の一部を採用していません。',
  },
  historical_transcript_missing_or_no_accessible_recordings: {
    category: '原因未確定',
    description: '過去の文字起こしを取得できませんでした。この結果だけでは未作成・保持期限・アクセス権などを区別できません。',
  },
  verified_profile_transcript_unavailable: {
    category: '文字起こし未取得', description: 'この取得profileでは文字起こしを取得していません。存在しないとは判定していません。',
  },
  bounded_discovery_shared_channels_not_explored: {
    category: '探索範囲の制限', description: 'Team・Channel探索には上限があり、共有Channelは未探索です。全件取得とは扱いません。',
  },
  bounded_page_incomplete: {
    category: '続きのページあり', description: '返された続きのページを取得していないため、取得結果は部分的です。',
  },
  bounded_discovery_incomplete: {
    category: '探索上限', description: '取得対象の探索上限に達しました。未探索の範囲が残っています。',
  },
  bounded_message_page_at_limit: {
    category: 'ページ上限の可能性', description: '取得件数がページ上限に達しています。継続リンクがなくても全件取得とは扱いません。',
  },
  bounded_discovery_at_limit: {
    category: '探索上限に到達', description: '探索件数が設定上限に達しています。未探索範囲がないことまでは確認できません。',
  },
  bounded_ask_candidates: {
    category: '検索候補の原文確認', description: 'askの出典から候補を選び、fetchの原文を確認しています。検索・取得の網羅性は保証していません。',
  },
  message_time_unverified: {
    category: '日時未確認', description: '元データの日時・タイムゾーンを確認できず、この発言を採用していません。',
  },
  message_author_or_content_unverified: {
    category: '作者・本文未確認', description: '元データの発言者または本文を確認できず、この発言を採用していません。',
  },
  message_kind_unverified: {
    category: '種別未確認', description: '通常の業務発言か判定できないレコードを、推測で採用していません。',
  },
  calendar_time_unverified: {
    category: '予定日時未確認', description: '予定の開始・終了日時を確認できず、招待メール等から補完せずに除外しました。',
  },
  budget_exhausted: {
    category: '取得上限', description: '安全な呼び出し上限に達したため取得を打ち切りました。取得済み情報と未取得範囲を分けて扱います。',
  },
  collector_submission_uncertain: {
    category: '実行結果未確定', description: '取得要求の受付結果を確認できません。同じ要求を自動で再送していません。',
  },
}

export function workIqToolLabel(value?: string): string {
  if (value == null) return 'ask'
  return value === 'ask' || value === 'fetch' || value === 'call_function' ? value : 'Work IQツール'
}

export function workIqAcquisitionCounters(source: Coverage): { label: string; count: number }[] {
  const diagnostics = safeDiagnostics(source.diagnostics)
  const counters: [string, string][] = [
    ['workiq.ask', 'ask承認予約'],
    ['workiq.fetch', 'fetch承認予約'],
    ['workiq.call_function', 'call_function承認予約'],
    ['workiq.fallback', '構造化取得への切替'],
    ['workiq.excludedSystem', 'システムイベント除外'],
    ['workiq.outOfPeriod', '期間外の根拠除外'],
    ['workiq.metadataRejected', '日時・出典等の確認不足'],
    ['workiq.unknownKind', 'メッセージ種別不明'],
  ]
  return counters.flatMap(([key, label]) => diagnostics[key] === undefined ? [] : [{ label, count: diagnostics[key] }])
}

export function acquisitionPathLabel(value?: string): string | null {
  const paths: Record<string, string> = {
    'calendar.fetch': 'fetch：予定表の元データ',
    ask: 'ask：自然言語検索',
    'fetch.discovery': 'fetch：対象を探索して取得',
    'ask→fetch (source verification; ask claims unverified)': 'askの候補 → fetchで原文確認（askの主張との意味一致は未検証）',
    'ask→fetch (structured fallback; ask match unverified)': 'ask → 構造化取得に切替（askによる取得成功ではありません）',
  }
  return value === undefined ? null : paths[value] ?? '取得経路の詳細は未確認'
}

export function knownCounter(value: unknown): number | null {
  return typeof value === 'number' && Number.isSafeInteger(value) && value >= 0 ? value : null
}

export function counterLabel(value: unknown): string {
  const count = knownCounter(value)
  return count === null ? '不明' : count.toLocaleString('ja-JP')
}

export function explainCoverage(source: Coverage): ReasonExplanation[] {
  const explanations: ReasonExplanation[] = []
  const reasons = [...new Set((source.reason ?? '').split(';').map((value) => value.trim()).filter(Boolean))]
  if ((source.collectionStatus ?? source.status) === 'complete' && knownCounter(source.fetchedCount) === 0 && reasons.length === 0) {
    explanations.push({
      category: '取得成功・0件',
      description: '探索した範囲の取得は成功し、取得件数は0件でした。未探索範囲までデータがないと断定するものではありません。',
    })
  } else if (knownCounter(source.count) === 0 && knownCounter(source.fetchedCount) === null) {
    explanations.push({
      category: '取得件数不明',
      description: '生成への採用は0件です。取得件数が報告されていないため、取得成功0件やアクセス拒否とは判定できません。',
    })
  } else if (knownCounter(source.count) === 0 && (knownCounter(source.fetchedCount) ?? 0) > 0) {
    explanations.push({
      category: '採用0件',
      description: '情報は取得していますが、生成への採用は0件です。',
    })
  }
  for (const reason of reasons) {
    explanations.push(reasonExplanations[reason] ?? {
      category: '診断あり',
      description: '追加の診断コードがあります。コードだけからデータの不存在やアクセス拒否は断定していません。',
    })
  }
  const errors = safeCoverageErrors(source.errors)
  if (errors.some((error) => error.httpStatus === 403)) explanations.unshift({
    category: 'アクセス拒否', description: '取得処理で HTTP 403 を観測しました。対象データがないという意味ではありません。具体的な権限・ポリシーは技術詳細と環境設定で確認してください。',
  })
  if (errors.some((error) => error.httpStatus === 401)) explanations.unshift({
    category: '認証エラー', description: '取得処理で HTTP 401 を観測しました。認証に失敗した結果をデータ0件とは扱いません。',
  })
  if (errors.some((error) => error.httpStatus === 404)) explanations.push({
    category: '照会先未検出', description: '取得処理で HTTP 404 を観測しました。不存在・期限・アクセス条件のどれかは、この応答だけでは確定できません。',
  })
  if (!explanations.length && source.status !== 'complete') explanations.push({
    category: '原因未確定',
    description: '取得範囲が未完了です。取得失敗とデータの不存在は区別してください。',
  })
  return explanations
}

const identifier = /^[a-zA-Z][a-zA-Z0-9_.:-]{0,100}$/
const credentialLike = /Bearer\s|eyJ[\w-]+\.[\w-]+\./i

export function safeDiagnostics(value: unknown): Record<string, number> {
  if (!value || typeof value !== 'object' || Array.isArray(value)) return {}
  return Object.fromEntries(Object.entries(value).filter(([key, count]) =>
    identifier.test(key) && !credentialLike.test(key) && knownCounter(count) !== null,
  )) as Record<string, number>
}

export function safeCoverageErrors(value: unknown): NonNullable<Coverage['errors']> {
  if (!Array.isArray(value)) return []
  return value.filter((item) =>
    item && typeof item.stage === 'string' && identifier.test(item.stage) && !credentialLike.test(item.stage) &&
    Number.isInteger(item.httpStatus) && item.httpStatus >= 400 && item.httpStatus <= 599 && knownCounter(item.count) !== null,
  ).map((item) => ({
    stage: item.stage, httpStatus: item.httpStatus, count: item.count,
    ...(typeof item.graphCode === 'string' && identifier.test(item.graphCode) && !credentialLike.test(item.graphCode) ? { graphCode: item.graphCode } : {}),
  }))
}

export function collectionLabel(value: Coverage['collectionStatus']): string {
  return value === 'complete' ? '取得完了' : value === 'partial' ? '一部取得' : value === 'unavailable' ? '取得不可' : '不明'
}

export function inputLabel(value: Coverage['inputStatus']): string {
  return value === 'complete' ? '削減なし' : value === 'partial' ? '入力削減あり' : '不明'
}

export function exportCoverage(source: Coverage) {
  return {
    sourceType: source.sourceType, status: source.status, count: knownCounter(source.count),
    fetchedCount: knownCounter(source.fetchedCount), pages: knownCounter(source.pages),
    collectionStatus: ['complete', 'partial', 'unavailable'].includes(source.collectionStatus ?? '') ? source.collectionStatus : null,
    inputStatus: ['complete', 'partial'].includes(source.inputStatus ?? '') ? source.inputStatus : null,
    retrievedCount: knownCounter(source.retrievedCount), sentCount: knownCounter(source.sentCount),
    truncatedCount: knownCounter(source.truncatedCount), inputDroppedCount: knownCounter(source.inputDroppedCount),
    diagnostics: safeDiagnostics(source.diagnostics), errors: safeCoverageErrors(source.errors),
  }
}

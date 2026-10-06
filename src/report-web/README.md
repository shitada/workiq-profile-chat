# Report Chat frontend

Graph / Work IQ 共通の React UI。`VITE_REPORT_PROVIDER` は表示・ブラウザー内状態分離にのみ使用し、API の provider は変更しない。旧 `workiq-profile-chat\src\web` は変更していない。

タブのタイトルは build 時に `index.html` へ埋め込む。`graph` は **Graph API | Report Chat**、`workiq` は **Work IQ | Report Chat** となり、サインイン前や JavaScript の起動前も区別できる。`redirect.html` のタイトル **認証処理中** は別に維持する。

## ローカル実行

Node.js 22.12 以降（検証時 22.23.3）と npm を使用する。既存依存関係は旧 UI の lockfile に存在するバージョンへ完全固定している。Markdown プレビュー用に `react-markdown` 10.1.0 と `remark-gfm` 4.0.1 を追加し、lockfile に固定している。

```powershell
npm ci
$env:VITE_TENANT_ID = '00000000-0000-0000-0000-000000000001'
$env:VITE_SPA_CLIENT_ID = '00000000-0000-0000-0000-000000000002'
$env:VITE_API_CLIENT_ID = '00000000-0000-0000-0000-000000000003'
$env:VITE_API_URL = 'https://report-test.invalid'
$env:VITE_REPORT_PROVIDER = 'graph' # または workiq
npm run build
npm run test
npm run lint
npm run test:e2e -- --workers=1
```

上記の値はビルド検証用ダミーで、実際のサインイン・API 通信には使用できない。実環境の値はデプロイ時に注入し、`.env` や生成物を Git へ追加しない。`VITE_API_URL` は `/api` を含まない API origin。MSAL は既存の SessionStorage、`api://<API_CLIENT_ID>/access_as_user`、popup、`redirect.html` の redirect bridge を維持する。bridge のブラウザーテストは `dist` 内の実モジュールを使用するため、E2E の前に上記のダミー設定で build を実行する。

現在の OneDrive 配下 workspace では、固定版 oxlint 1.77.0 が `No files found to lint` で終了する（`--no-ignore` と明示ファイル指定でも同様）。ソースだけを OneDrive 外のセッション検証フォルダーへコピーし、同じインストール済み oxlint と `.oxlintrc.json` を絶対パス指定して検証する。Markdown 対応後は src と build helper / Vite config を合わせて **25 ファイル・104 ルール・診断 0 件**を確認。TypeScript build、Vitest、Playwright は元の workspace でも実行可能。

`npm run dev` でローカル開発できる。E2E は専用ポート 4179 の Vite とインストール済み Microsoft Edge を使い、API をすべて模擬する。`tests\playwright\report-harness.html` は認証サービスを使わないテスト専用 entry であり、本番の build input には含まれない。実際の Entra / M365 / SharePoint 接続はこのテストでは未検証。

## 操作と状態

- 初回は config のみ取得し、日付未指定のレポートは取得しない。
- 日付を含む依頼を resolve し、解釈した対象期間を表示する。曖昧な週は候補を選択し、「この期間で生成」で取得開始する。
- 左の期間入力を優先したい場合は「この入力期間を使用」を押す。新しいプロンプトを送る場合は、その明示日付が以前の期間より優先される。
- 上長週報のメンバーは ID / UPN を空白・改行・カンマ等で明示する。
- 自動継続は最大 8 API 呼び出しで停止し、「続きを取得」で次のバッチを開始する。progress の `retryAfterSeconds` と HTTP `Retry-After` の長い方を待機する（指定なしは 1 秒）。待機中もキャンセル可能。OAuth 同意、tool approval、partial confirmation は自動承認しない。
- OAuth は既存の `*.consent.azure-apim.net/login` / `*.consent.azure-apihub.net/login` の HTTPS URL のみ、native anchor で開く。source / 保存リンクも HTTPS・資格情報なし URL のみ表示する。
- 新しい依頼・日付変更・メンバー変更・キャンセルで旧継続状態を破棄する。identity 単位の key でコンポーネントを再作成し、旧リクエストの abort と遅延応答の無効化を行う。
- sessionStorage は version 1、tenant + account + provider で分離する。入力・期間・メンバー・編集本文だけ保存し、access token、continuation、同意リンク、根拠データは保存しない。再読み込み後は下書きを参照・編集し、対象期間を確認して再取得・再生成できるが、取得状態と保存可能な run は復元しない。
- 生成完了後は Markdown プレビューを既定表示する。「プレビュー／編集」で切り替え、同じ下書き文字列を保持する。新しい生成・再生成が完了するとプレビューへ戻る。見出し・リスト・表・Unicode・参照リンク・脚注を表示し、HTML は解釈しない。画像は alt テキストに置き換え、外部画像を自動取得しない。リンクは HTTPS（資格情報なし）または文書内参照のみ。外部リンクは別タブ・`noopener noreferrer` とする。
- 本文の編集と、`draft` を付けた同一対象期間の再生成ができる。保存・再生成はレンダリング後の HTML ではなく、編集した Markdown 原文をそのまま使用する。完了した `runId` を `continuationToken` として送り、既存の根拠を再利用する（backend の追加契約）。リロード後の復元下書きは run を保持しないため再取得する。日付を変更する依頼は新しいプロンプトで開始する。
- SharePoint 保存は明示ボタンのみ。「本人確認済み」または「テスト生成（未確認）」を選択できる。保存先未設定・構成取得失敗時は保存を無効化する。
- 比較 JSON は run / period / coverage / 生成条件と計測値を出力する。本文・根拠本文・任意の文字列・トークン値は出力しない。`inputTokens` / `outputTokens` 等の数値カウンターは保持する。backend の `promptHash` / `generationConfigHash` / `schemaHash` / `evidenceSchemaHash` / `generationSpecVersion` と model / agent version を許可し、`sourceStatus` の動的な情報源名には `complete` / `partial` / `unavailable` の値だけを許可する。その他の未定義の文字列 metadata は `report.ts` の許可リスト追加が必要。

## 契約

`/api/reports/config`, `/resolve`, `/generate`, `/save` の契約に対応。旧 `/api/chat` は呼び出さない。API が返す source errors は coverage の `complete` / `partial` / `unavailable` と理由で表示する。HTTP エラーは message / correlation ID を表示し、非 JSON の gateway 本文は表示しない。

### Coverage の解釈

- `fetchedCount`（取得）・`retrievedCount`（期間内の有効な重複除外済み情報）・`count`（生成への採用）・`pages` を別々に表示し、未報告の値は「不明」とする。比較 JSON でも未報告は `null` とし、0へ置き換えない。旧レスポンスから `retrievedCount` / `sentCount` を逆算しない。
- `collectionStatus`（取得処理）と `inputStatus`（入力削減）を分離する。取得完了でも入力削減で総合 `status` が partial になる場合を、取得失敗と混同しない。新しい状態がない旧レスポンスは不明と表示し、旧総合状態を併記する。
- `complete` かつ明示的な `fetchedCount: 0` だけを「取得成功・0件」と表示する。それでも未探索範囲までデータが存在しないとは断定しない。
- 採用0件だけでは、元データ0件やアクセス拒否を判定しない。
- `shared_channels_not_discovered` は「未探索」、`input_budget_exhausted` は「入力上限」と表示する。
- `historical_transcript_missing_or_no_accessible_recordings` は「原因未確定」とし、未作成・保持期限・アクセス権のいずれかへ推測で固定しない。
- 元の `reason` は折りたたみの技術詳細にそのままテキスト表示する。未知の理由もデータ不存在・アクセス拒否へ決めつけない。
- 確定した0件理由（例：`no_messages_in_activity_period`）は「対象期間・0件」、`transcript_list_empty_recording_or_retention_unknown` は「文字起こし一覧・0件／原因不明」とし、取得段階ごとの意味を表示する。
- `errors: [{stage,httpStatus,graphCode?,count}]` の実際の HTTP 403 だけを「アクセス拒否」と分類する。401 は認証エラー、404 は照会先未検出とし、404だけで不存在を断定しない。
- 技術詳細は `diagnostics` の段階別数値 map、エラー、`sentCount` / `truncatedCount` / `inputDroppedCount` をコンパクトに表示する。比較 JSON にも安全な数値 map と構造化エラーを保存するが、任意の本文・URL・token フィールドは取り込まない。比較 metrics の `inputSelectionVersion` と数値 `inputTokenEstimate` / `stagedTokens` / `truncatedEvidence` に対応する。

coverage table は横スクロール可能で、レポート画面のレイアウトは `.chat-panel.report-panel` に限定した flex とする。後方の既存 `.chat-panel` の grid 指定が warning を `1fr` 行へ伸ばしていた問題を解消し、警告は内容の高さに収める。

各 API 要求でアクセストークンを silent 取得する。identity 変更やキャンセル中に token 取得が完了しても、古い要求は送信しない。`InteractionRequiredAuthError` の場合だけ「認証を続ける」を表示し、ユーザーのクリックから直接 popup を開く。config effect や silent 取得の非同期失敗から popup は開かない。timeout は認証要求への自動切替をせず、API 呼び出し前の問題として表示する。構成の再試行は入力・下書きを保持する。

### MSAL `timed_out` の修正根拠

従来のヘッダーでは親画面の `frame-src` に同一 origin がなく、`/redirect.html` にも `frame-ancestors 'none'` / `X-Frame-Options: DENY` が適用されていた。popup のログインは成功しても、silent token renewal が hidden iframe を使用する場合、同じ bridge に戻れず `BroadcastChannel` 応答が届かない。元のプロフィール UI は config 失敗を advisory として扱い、token 取得失敗をすべて popup へフォールバックしていたため、ログイン成功だけでは silent flow の正常性を確認できない。

`staticwebapp.config.json` は親画面の `frame-src` に `'self'` を追加し、**`/redirect.html` のみ** `frame-ancestors 'self'` / `X-Frame-Options: SAMEORIGIN` とする。メイン画面の埋め込み拒否（`frame-ancestors 'none'` / `DENY`）は維持する。bridge の script は同一 origin のみ、接続・form・base は拒否、cache は no-store、COOP は空で維持する。ワイルドカード origin、追加認証 scope、timeout 延長は行わない。

`tests\playwright\redirect-bridge.spec.ts` は実際のビルド済み MSAL bridge と合成 state/code を使用し、旧ヘッダーの二つの遮断を個別再現する。同一 origin silent iframe と popup の修正後の応答、cross-origin iframe の拒否も検証する。ユーザーの token / credential / Entra 接続は使わない。この検証はヘッダー不整合を立証するが、個別ブラウザーの cookie 制限や実環境の登録 URI の正しさまで保証しない。

公式資料（2026-09-30 にアクセス確認）：
- [MSAL v5 Redirect Bridge](https://github.com/AzureAD/microsoft-authentication-library-for-js/blob/dev/lib/msal-browser/docs/redirect-bridge.md)
- [Using MSAL in iframed apps](https://github.com/AzureAD/microsoft-authentication-library-for-js/blob/dev/lib/msal-browser/docs/iframe-usage.md)

React の非同期処理 cleanup は公式 [useEffect](https://react.dev/reference/react/useEffect) を確認して実装（2026-09-30 にアクセス確認）。

Markdown は公式 [react-markdown](https://github.com/remarkjs/react-markdown) と [remark-gfm](https://github.com/remarkjs/remark-gfm) を確認して実装（2026-09-30）。`skipHtml` と厳格な URL 変換を使用し、raw HTML plugin / `dangerouslySetInnerHTML` は使用しない。プレビューのモジュールは必要になるまで lazy load する。OAuth 同意・例外的な tool approval の明示操作は変更しない。

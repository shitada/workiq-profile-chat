# Graph版とWork IQ版の日報・週報比較

## 現在の配信構成

既存のWeb URL／Functionを維持し、`azure.yaml`のサービスpathを`src\report-api`／`src\report-web`へ変更しています。旧`src\api`／`src\web`のコードは削除していません。

- Work IQの収集：`workiq-report-collector:5`、`verified-workiq-v1`。MCPの`ask/fetch/call_function`を許可し、`require_approval=always`でFunctionが固定read taskの引数一致を確認して自動承認します。現在のタスクは`ask/fetch`を使い、`call_function`は計画していません。
- 最終生成：`workiq-report-agent`。Graph版の`graph-report-agent`とinstructions全文・モデルdeployment・生成設定を共通化。
- 日付：2026年をテスト基準年とし、プロンプトの日付を優先。現在日付を黙って採用しません。
- 日報日D：前営業日の実績＋Dの予定。週の曖昧な指定は開始日／終了日の確認を挟みます。
- Graph版は別プロジェクト／別URLで、既存Foundry projectを共有します。モデルの自動更新を検知したrunは比較失敗として扱います。

旧agent名`workiq-profile-agent`はlatest参照の旧APIへ影響するため更新せず、新しい最終生成agentへ稼働参照を切り替えています。これにより旧agent versionのまま切り戻せます。

利用者へのツール確認ダイアログを省いても、サーバー側の実行前検証は省きません。未知のツール・変更された引数・二重承認は拒否します。OAuth同意・本人認証は引き続き必要で、SharePoint保存は明示ボタン操作を維持します。collector/profile/schemaを切り替えると旧runの継続は拒否されるため、新しい要求から開始します。

## 2026-10-06 検証付き取得の反映

- 予定はcalendarViewの元JSONを解析し、`ask`や同名招待メールの日時で代用しません。
- Chatは前営業日を指定した固定英語質問を使い、候補の元メッセージをWork IQで確認します。空結果・メタデータ不足の構造化fallbackは明示し、askだけの取得成功に数えません。
- 期間外、日時／作者／種別の確認不足、システムイベントを別計数します。予定を参加・完了実績にしません。
- Channel探索は有界であり、nextLink残存・件数上限・共有Channel未探索はpartialです。通常業務投稿・返信の実データによる正常系確認は未完了です。
- 元応答は解析後に診断プレビューを生成します。Foundryの`structuredResponse`と自然文＋JSON trailerを区別し、大きなJSONをpreview上限で切ってから解析しません。
- 取得タスクと承認POST前のintentを永続化し、結果不明のPOSTを再送しません。最大12ツール承認／32 transport submissions。サービス・認証・policyエラーを0件や不存在として扱いません。

本人のFoundry結合試験では、8/1日報（7/31実績）で予定6件から最終生成まで確認しました。
別の取得器単体fixtureでは8/1の通常Chat3件を確認し、システムイベント1件を除外しました。
後者は日付境界の検証用で、8/2日報の前営業日を8/1へ変更したという意味ではありません。
新しい稼働画面での本人による生成・保存の通し確認は別項目です。
詳しい契約・設定・制約は[Report API README](../src/report-api/README.md)を参照してください。

## 共通定義

正本はGraphプロジェクトの`common\reporting`です。Graphプロジェクト内の`scripts\sync-workiq-reporting.ps1`でこのリポジトリへ配布し、hash一致を検査します。このリポジトリのコピーを直接編集して片方だけ異なるpromptにしないでください。

実行時に隣接するGraphリポジトリは不要です。デプロイ時に共通ファイルをAPI成果物へ同梱します。UIから比較metadataをエクスポートでき、prompt／generation config／schema／model versionの一致を確認できます。

## SharePointと週報

両方式とも確定済みの日報をGraphの委任権限で読み、同じ最終生成仕様で本人・上長週報を生成します。Work IQ版のAPIにもこの保存・読出し用のUser.Read、Files.ReadWrite.All、Sites.Read.Allを付与します。日報のWork IQ収集をGraphに置き換えるものではありません。

保存はユーザーのボタン操作で実行し、Markdownと隣接する`.md.report.json`を版別ファイルとして保存します。カスタム列更新に必要な追加Sites.ReadWrite.Allは要求しません。metadataと本文のSHA-256・folder所属・reportDate・subjectIdを確認します。

`Report__SharePointDriveId`／`Report__SharePointFolderId`が空の場合、保存・週報は未設定として明示します。架空の保存先や元データへの黙ったフォールバックは行いません。

## 設定

最終agent／collectorのversionは`REPORT_AGENT_VERSION`／`REPORT_COLLECTOR_VERSION`からIaCへ渡します。APIでは`Foundry__AgentVersion`／`Foundry__CollectorAgentVersion`を使います。`Report__Provider=workiq`はサーバー側設定であり、クライアントからGraphへ変更できません。

検証付き取得の有効化には`WorkIq__AcquisitionProfile=verified-workiq-v1`と
`WorkIq__ConnectionId=<既存Foundry OAuth接続のresource ID>`も必要です。固定versionとprofileを組で切り替えます。

ルートの全体provisionは既存モデル等にも影響するため、既存環境の切替ではGraphプロジェクトの`workiq-update` entrypointを使います。現在のapp settingsを保全したsecure parameterで`appsettings`を更新し、private `report-runs`コンテナーだけを追加します。定常IaCにも同じreport設定・コンテナーを反映しています。

## 切り戻し

検証付き取得から旧日報方式への切り戻しは、`WorkIq__AcquisitionProfile=legacy-ask-v1`と
取得Agent v4を組で戻す方法、または保全した旧report API/Web packageと設定を組で戻す方法を使います。
今回の変更前package・非機微のAgent設定・定義snapshotは作業端末のGit外に保全しています。
日報方式の切り戻しと、以下の旧プロフィール機能への切り戻しは別です。

切替前の`azure.yaml`とapp settingsは、適用した作業環境のignored `.azure\reporting-rollback`へ保存します。これらは秘密を含む可能性があるため公開・ログ出力しないでください。

復旧では旧`src\api`／`src\web`を使用するサービスpath、旧Function設定、旧agent参照を組で戻し、APIとWebを再デプロイします。agent名だけを戻すと新二段階APIとの不整合が起きます。現在のSharePoint文書、OAuth同意、private networkは削除しません。`azd down`は復旧手順ではありません。

## 比較の解釈

### モデルの処理枠とレート制限

共通のGPT-4.1 deploymentはGlobalStandard capacity200（このモデルでは200,000 TPM／200 RPM）を使用します。旧capacity10（10,000 TPM）では、Work IQのツール応答後にコンテキストが増えた時点で`rate_limit_exceeded`となり、取得Agentが根拠JSONを完成できないケースを確認しました。モデルの入力上限と、1分間の処理枠は異なります。処理枠の割り当て変更は両方式に共通で、モデル・version・最終生成instructionsは変えません。

旧10,000 TPMでの実行時間・失敗率を、200,000 TPMへ変更後の結果と同じ負荷条件として比較しないでください。モデル・指示hashが同じでも、処理枠と他の同時実行数は比較条件に含めます。

既存環境では`capacity-update`のazd entrypointで当該deploymentだけを更新できます。環境変数は`AZURE_SUBSCRIPTION_ID`、`AZURE_LOCATION`、`AZURE_RESOURCE_GROUP`、`AZURE_AI_ACCOUNT_NAME`です。`azd provision --preview --no-prompt`で容量だけの変更と利用可能quotaを確認してから適用してください。元の全体provisionを容量修正のためだけに実行しないでください。GlobalStandardは従量課金のままですが、TPMは予算上限ではなく、追加の生成・再試行には通常のtoken料金が発生します。

旧`legacy-ask-v1`の取得Agentの保存済み応答が明示的に`failed/rate_limit_exceeded`なら、同じ本人・期間・responseのコンテキストを維持し、65秒／130秒待って最大2回自動再開します。有効なツール応答が既にある場合はツールを無効にしてJSON整形のみを再開し、同じaskを繰り返しません。受付不明のPOSTや認証・content filter等の別エラーをこの経路で再送しません。`collector.rateLimitRetries`と待機時間も比較値に含めます。取得元の情報が返らない問題は、このレート制限とは別です。

参考：[quotaと推定token数の関係](https://learn.microsoft.com/en-us/azure/foundry/openai/how-to/quota)、[deployment種類と課金方式](https://learn.microsoft.com/en-us/azure/foundry/foundry-models/concepts/deployment-types)。

同じ最終生成モデルを使ってもWork IQ内部の検索・推論はGraphと同一ではありません。日報は方式全体の比較、同じ確定日報から作る週報は生成の対照試験として区別します。取得時間・生成時間・token数・部分取得を分け、人手で確認していない正確性を測定済みと扱わないでください。

### 根拠0件の診断

Work IQ取得agentの空結果、日時・出典等が不足した根拠候補の除外、ask実行エラーを区別します。失敗時も画面の「生成できなかった理由」で情報源別件数を確認できます。形式不備は最大1回、同じ期間で修正要求を行います。再試行も取得時間とcollector token usageに含まれるため、比較時は診断の`collector.repairAttempts`も記録してください。データがないと確認できたわけではない状態を「実績0」として生成しません。

`reason`は取得Agentの説明で、Work IQ自体の実応答とは異なります。成功・失敗・部分取得確認待ちの結果では、画面下部の「Work IQが実際に返した回答」へ本人の返答を自動表示します。本文に加えて、認証情報等を除去した受信形式（`mcp_call.output`）も展開でき、Functionの本文抽出前のフィールドを確認できます。成功後もprivate run stateへ保持し、既存の本人・tenant・provider・2時間の参照期限を検査します。通常ログ・比較JSON・ブラウザー下書き保存へ実回答を入れず、HTMLも実行しません。最大8応答・本文6,000文字／受信形式65,536文字で、省略は明示します。過去の成功runで削除済みの応答は復元しません。

内容には業務情報があり得るため公開しないでください。表示通信の失敗はレポート生成とは区別して再試行できます。実回答も参照不可なのか、実回答にある根拠を収集AgentがJSONへ変換できていないのかを区別するための表示で、取得条件・モデル・生成指示は変えません。

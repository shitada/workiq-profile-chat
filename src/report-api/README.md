# 日報・週報 API

.NET 10 isolated Azure Functions。`Report__Provider=graph|workiq` はサーバー設定であり、リクエストから変更できません。Work IQの取得profileもサーバー側で選択します。旧 API・インフラの変更は含みません。

## ローカル検証

リポジトリルートから実行します。

```powershell
dotnet build projects\graph-report-chat\src\api\GraphReportChat.Api.csproj --no-restore
dotnet test projects\graph-report-chat\tests\Api.Tests\Api.Tests.csproj
dotnet publish projects\graph-report-chat\src\api\GraphReportChat.Api.csproj -c Release --no-restore -o projects\graph-report-chat\src\api\artifacts\publish
```

初回または依存関係変更後はrestoreが必要です。Functionsホストでの起動には別途Azure Functions Core Toolsが必要です。ユニットテストは実データ・クラウドへの接続を行いません。

`..\..\common\reporting\*` を出力の `reporting` にコピーします。Work IQ側へミラーするときは、親の配布処理でこのContent参照の相対位置を調整してください。隣接リポジトリへの実行時依存はありません。

## 必須・任意の環境変数

| 設定 | 用途 |
|---|---|
| `FUNCTIONS_WORKER_RUNTIME=dotnet-isolated` | Functions worker |
| `AzureWebJobsStorage` または `AzureWebJobsStorage__*` | Functionsホスト用Storage。実行状態用Blobとは別に設定 |
| `Authentication__TenantId` | 許可するEntra tenant |
| `Authentication__ApiClientId` | API audience・OBO client |
| `Authentication__RequiredRole` | 既定 `ProfileChat.User` |
| `Authentication__ManagedIdentityClientId` | user-assigned MI。未指定時はsystem-assigned |
| `Authentication__LocalClientSecret` | 任意。開発時OBO専用。Gitへ保存しない |
| `Foundry__ProjectEndpoint` | Foundry project endpoint |
| `Foundry__AgentName` / `Foundry__AgentVersion` | 共通最終生成Agentと固定version。`latest`は禁止 |
| `Foundry__CollectorAgentName` / `Foundry__CollectorAgentVersion` | Work IQ取得専用Agent。Work IQ日報で必須 |
| `WorkIq__AcquisitionProfile` | 既定 `legacy-ask-v1`。新方式は明示的に `verified-workiq-v1` を指定 |
| `WorkIq__ConnectionId` | 新方式で必須。取得Agentの `project_connection_id` と厳密一致させる既存OAuth接続ID |
| `Foundry__Scope` | 既定 `https://ai.azure.com/.default` |
| `Report__Provider` | `graph`（既定）または`workiq` |
| `Report__StorageServiceUri` | HTTPS Blob service URI。SASは使用しない |
| `Report__StateContainer` | 既定 `report-runs`。事前作成が必要 |
| `Report__SharePointDriveId` / `Report__SharePointFolderId` | テスト専用の保存先。未指定時、保存・週報は利用不可 |
| `Report__TestYear` | 年省略時の基準年。既定2026 |
| `Report__CompanyHolidays` | 任意。ISO日付のカンマ区切り |
| `Report__AdditionalOfficialHolidays` | 任意。例 `2028:2028-01-01,...;2029:...`。その年の全公式休日を管理者が確認して設定 |

共通 `generation-config.json` がモデルdeployment/version、temperature、出力token上限、入力上限、Graph呼出し上限の正です。実際のAgent指示・ツール・モデル・temperatureと、deploymentのmodel versionを毎回検証します。SDKの `GetProjectResponsesClientForAgent(new AgentReference(name, version))` でversionを指定しています。**固定Agentのdefinitionに`temperature: 0`を明示してください。** Agent指定とリクエストのinstructions/temperature併送は実サービスがHTTP 400で拒否するため、これらは共通定義と一致するAgent versionから継承します。リクエストには出力上限、`tool_choice=none`、保存無効を指定します。生成前後でモデルversionが変わった場合はエラーです。

## 検証付きWork IQ profile

`verified-workiq-v1` は日報の取得だけを変更します。最終生成Agentのモデル・指示・ツールなし設定、週報・SharePoint保存の既存Graph経路は変更しません。既定値は旧ask-only方式なので、コード更新だけで有効にはなりません。

### Agentの準備と切り戻し

`tools\AgentSetup` のプレビュー／適用／読み戻し検証では `WORKIQ_ACQUISITION_PROFILE=verified-workiq-v1` と `WORKIQ_REQUIRE_APPROVAL=always` を指定します。`--collector-only` は既存manifestを使用し、取得Agentだけに新しいimmutable versionを作成します。生成Agentは変更しません。MCPは1つ、サーバーは `https://workiq.svc.cloud.microsoft/mcp`、`allowed_tools` は `ask/fetch/call_function` の完全一致です。現profileが実際に計画するのはask/fetchだけで、delta補助取得は未使用です。新versionを読み戻して検証した後、別途承認された配布でAPIのprofile・固定Agent version・接続IDを同時に設定します。

切り戻しは `legacy-ask-v1` と旧ask-only Agent versionをセットで戻します。profile、APIビルドのMVID、タスク契約、指示hash、project endpoint、Agent名、接続ID、既存schema/config/Agent versionのいずれかが変わったrunは継続せず409で新規取得を要求します。元の最終生成指示・共通model設定はそのままです。

### 固定タスクと根拠

- 最初の `fetch /me?$select=id,userPrincipalName` の元data.idを、認証済みrunのSubjectIdとGUIDで比較します。caller JWTを未検証でデコードして本人を推定しません。不一致／取得不能なら業務情報を取得しません。
- 日報日Dに対して、固定英語askは**前営業日**を質問します。質問の言い換え再試行やWork IQ会話継続による整形はしません。
- askの既知Teams messageリンクからChat ID候補を最大3件選び、元messagesをfetchします。リンクがない通常の空結果・メタデータ不足は、Chat一覧→最大3 Chatのmessagesという一度だけの有界fallbackです。**askの主張やJSONを根拠にはしません。** fetch原文を使う場合は経路と「askの主張との意味一致は未検証」を明示します。
- Chat messagesは `createdDateTime lt 対象日の翌日UTC` と同じプロパティのdescending orderで最大50件を要求します。対象日の下限は元のcreatedDateTimeで判定します。Work IQがmessage pageを10件へ制限しnextLinkを返さない場合もあるため、Chat／Channel親投稿／返信の応答が**10件以上ならnextLinkの有無にかかわらずpartial**です（`workiq.pageLimitReached`、reason=`bounded_message_page_at_limit`）。nextLinkがある場合もpartialで、無制限のページングや未検証skiptokenの再送は行いません。探索もChat 3件／Team 2件／Channel合計3件／返信対象親5件に**ちょうど到達した段階**でpartialとします（`workiq.discoveryLimitReached`、reason=`bounded_discovery_at_limit`）。nextLink不在や上限と同じ件数を、完全取得の証拠にはしません。
- Calendarは前営業日とDのcalendarViewを1つのfetchタスクで取得します。開始／終了・timezone・実IDを元JSONから解析し、日またぎは重なりで判定します。取消は除外、終日・繰り返し各回の実IDを保持。過去の予定も「予定」であり参加・完了ではありません。
- ChannelはWork IQ経由でjoinedTeams→最大2 Team、合計最大3 Channel→親投稿→合計最大5親の返信を取得します。通常投稿のcreatedDateTimeを検証し、古い親の対象日返信も採用できます。systemEventMessageまたはeventDetailは別集計で除外し、unknownFutureValue単独は通常発言にしません。shared channel探索・通常Channel投稿の実データ受入は未完了なのでpartialを維持します。getAllMessages・Chat delta・直接Graphによる補完は使いません。
- 非対応queryを避けるため、joinedTeamsにはOData optionを付けず、channelsにはselectだけ、chat/channel messagesにはselectを付けません。これらはWork IQへの相対パスであり、API自身がGraphへ送信するものではありません。
- Transcriptは取得不可を明示。日付・timezone・作者OID・種別が確定できないメッセージは未採用です。分精度は分精度として保持し、日付だけを都合よく午前0時へ変換しません。本人投稿／他者投稿はOIDで区別しますが、発言の業務状態は保守的にunknownとし、実施済み成果に読み替えない注意文を根拠に含めます。

### 上限・障害・保存

taskId、目的、source、tool名、予定引数、phase、response ID、raw source handleを所有者付きrunへ保存します。**最初のPOSTと承認POSTの双方でintentを先に保存**し、中断でresponse IDが不明ならuncertainとして再送しません。受領済みresponse IDはGETのみで確認します。MCP承認はサーバーがtool名・server label・JSON引数のDeepEqualsを検証してから自動承認します。余分な承認・変更引数・未知のツールは拒否し、OAuth本人同意は従来どおりUIで行います。

run全体で実ツール承認は最大12、1タスクのfetchパスは最大5です。初期POSTと承認POSTを別に数える `WorkIqTransportSubmissions` は**run全体で32（12×2＋OAuth継続余裕8）**を上限とし、双方のPOST前に永続化します。診断キーは `workiq.transportSubmissions` です。新方式は1応答1 tool call（既存上限6より小さい）、**タスクごとの**Foundry turn上限8、タスク開始から10分を上限とします。旧方式のrun全体turn上限8は変更しません。新方式で旧CollectorTurnsへ全POSTを加算して基本取得の途中で停止することはありません。10分はrun全体の期限ではなく、タスクの承認待ちも含む期限です。既知IDのGETだけに既存の有界一時障害再試行を使い、Work IQの認証・policy・InternalErrorや未知のPOST失敗を別ルートで回避しません。

カテゴリ単位のエラーは成功済み根拠を保全しpartialにします。identity不一致／承認不一致はrunを停止します。部分的根拠での生成には既存allowPartial確認が必要で、根拠0件では生成しません。

解析上限は**元ツールenvelopeのUTF-8 2 MiB**で、超過は明示エラーです。解析を済ませてから本人限定診断previewをredactし、最大8応答、本文6,000文字・raw 65,536文字へ制限します。previewの切り詰めをJSON解析に流用しません。業務本文・引数・tokenは通常ログ／比較metricsへ出しません。タスク引数は既存の本人限定run状態内だけに保持し、tokenは永続化しません。

fetchの元outputはJSON文字列も受け付け、その直下の `structuredContent.results` と、実Foundry応答で確認された `structuredResponse.results` を解析します。両方が存在して内容が異なる場合は拒否し、任意の深い位置にあるresultsやAgentが再記述したJSONを原文として探索・採用しません。

askは元outputが自然文のみ、JSON envelope、自然文＋structuredResponse trailerのいずれでも処理します。先に同じUTF-8 2 MiB上限を確認し、既存のreply readerで文章／既知エラーを解釈します。候補リンクはpreviewではなく元文章と構造化trailerの双方から抽出します。混在形式でも既知のstatus/errorは通常の空検索へ読み替えず、fallbackを禁止します。ask文章から直接Evidenceを作ることはありません。

### UI向けの任意追加項目

| 対象 | 項目 |
|---|---|
| Evidence | `acquisitionTool` (`fetch`)、`verificationStatus` (`verified`)、`timestampPrecision` (`second/minute/day`)、`messageKind` (`ordinary/calendar`)、`activityStatus` (`unknown/planned`)、`taskId`、`sourceScope` |
| Coverage | `acquisitionPath`。診断は `workiq.tasksPlanned/tasksSubmitted/tasksCompleted/ask/fetch/call_function/askResponses/askCandidates/metadataVerificationRequired/fallback/excludedSystem/unknownKind/outOfPeriod/metadataRejected/mismatch/cancelledExcluded/discoveryExcluded/nextPageRemaining` |
| WorkIqToolReply | `toolName`、`taskId`。既存rawText/rawTruncatedを維持 |
| 診断レスポンス | `acquisitionProfile`、`coverage`。最大8応答の表示省略をnoteへ明示 |

上表の診断名はすべて `workiq.` prefixを持ちます。`ask/fetch/call_function` はtool別の承認予約数で、POSTの結果が不確定でも予算から戻しません。成功数ではありません。未使用キーは省略されるためUIは0として扱えます（現profileのcall_functionは未使用）。identity取得分はsourceType=`workiq`に計上します。legacy返信でtoolNameが省略された場合は`ask`として表示できます。既存のraw診断項目は削除・変更していません。

sourceIdは実IDだけです。sourceScopeは会話／threadの取得パスで、別会話の同じmessage IDを誤って重複扱いしないための内部境界です。内部のEvidence IDは引用番号であり元message IDではありません。1会話出典から3発言を確認できた場合は、実message IDを持つ3根拠として保持します。`fetchedCount`は一意な観測record数（除外前）、`retrievedCount`は採用前に検証できた根拠数、`count/sentCount`は既存の入力budget適用後の数です。検索候補数を発言数に読み替えないでください。

実接続でのChat／通常Channel取得成功は合成テストでは証明できません。新profileの運用受入と配布は別途必要です。APIテストは実ユーザー本文・実IDを含まず、クラウドへの書き込みを行いません。

2026-10-06確認: [Foundry Responses API](https://learn.microsoft.com/en-us/azure/foundry/openai/how-to/responses)、[Chat messagesのquery制約](https://learn.microsoft.com/en-us/graph/api/chat-list-messages?view=graph-rest-1.0)、[joinedTeamsのquery制約](https://learn.microsoft.com/en-us/graph/api/user-list-joinedteams?view=graph-rest-1.0)、[Channel一覧](https://learn.microsoft.com/en-us/graph/api/channel-list?view=graph-rest-1.0)、[Channel messages](https://learn.microsoft.com/en-us/graph/api/channel-list-messages?view=graph-rest-1.0)。Work IQの互換性はこれらのGraph仕様だけから成功保証しません。

## 日報の期間別引用検証

生成入力の `dailyEvidenceGroups` は前営業日の `activityEvidenceIds` と日報日の `scheduleEvidenceIds` を分けます。`scheduledMeetingCount/Minutes` は前営業日の予定枠集計であり、`scheduledMeetingDate` と `scheduledMeetingCategory` で対象を明示します。当日の集計として流用しません。

最終出力では「前営業日に実施したこと」「当日の予定」の引用カテゴリを検証し、期間の取り違え・当日の採用済み予定の引用欠落・対象見出しの重複を拒否します。検査エラーを渡した再生成は1回までです。検証済み参照先にも期間区分を表示します。これは引用の配置検証であり、文章の全事実や会議への参加・完了を保証するものではありません。

生成入力の根拠日時は `+09:00` に揃え、UTC日付をJSTの日付と取り違えにくくします。保存済み根拠の日時は変更しません。前営業日の採用済み予定も「参加・完了未確認」の参考予定として引用を要求します。「根拠と情報不足」の日付・根拠ID対応はモデルの説明を使わず、サーバーの対象期間・採用根拠から構成します。取得制限は従来のサーバー記録を参照します。Markdown全文を囲むコードフェンスだけを除去し、本文内のコードブロックは保持します。

Work IQの既知の構造化応答は `workiq_structured` として、プレビューで確認できた取得先数・返却レコード数を表示します。文章形式の回答がないことを取得失敗と扱わず、HTTPエラー判定は維持します。返却数と日報への採用数は別です。旧保存状態でも、本文が空でraw JSONが未切り詰めなら読み取り時に表示用概要を再構成します。

## 認証と状態保存の前提

- **EasyAuthを必須にしてissuer/audienceを検証し、未認証要求をworkerへ通さない構成が必須です。** workerはプラットフォーム提供の `x-ms-client-principal` とBearerを要求し、tenant・Object ID・roleを確認します。このヘッダーだけで、公開された開発サーバーを認証することはできません。
- GraphとFoundryはそれぞれ別scopeのOBO。MIを利用する構成ではAPIアプリにMIのFederated Identity Credentialが必要です。app-only Graphへ切り替えません。
- 状態Blobは専用private container、Storage Public Access禁止、Private Endpoint/DNS、MIのStorage Blob Data Contributorをインフラ側で設定してください。実装はcontainerを公開・作成しません。
- 32バイト乱数ハンドル、tenant/user/provider所有権、2時間TTL、60秒Blob lease（20秒ごと更新）を使用します。クライアントが送るGraph nextLinkやFoundry response IDは受け付けません。OBO/access tokenは状態に保存しません。
- TTL後の読み取りを拒否します。物理削除にはStorage lifecycleを別途設定してください（例:1日）。本文・根拠・ユーザーIDは2時間以内でも機微な業務データとして扱ってください。Foundryのcollector continuationはFoundry側の保持設定も対象です。

## エンドポイント

`GET /api/health` のみ匿名。その他はEntra認証必須です。

- `GET /api/reports/config`
- `POST /api/reports/resolve`: プロンプトの明示日付を古いUI選択より優先。8/1は2026-08-01、1週目は月内7日区間・月初を含む月〜日・第1月〜金を提示。選択候補のperiodをgenerateへ送ると確定します。
- `POST /api/reports/generate`: completed / progress / oauth_consent_required / tool_approval_required / partial_confirmation。progressはHTTP 202、`retryAfterSeconds` と `Retry-After`（ある場合）を待ち、同じBearerとハンドルで再送します。
- `POST /api/reports/save`: 明示操作のみ。確認済み `confirmed` または検証用 `test-generated`。任意URL・任意所有者への保存は不可。

生成完了後、`runId`を`continuationToken`として`draft`と修正指示を送ると、同じ根拠を再利用した**別run**を作ります。修正指示に別の日付・対象者が明示された場合はその指定を優先して再取得します。新ハンドルを使って続行してください。新規生成でdraftを渡した場合も再取得します。取得中に別の対象期間へ変えたい場合はハンドルを破棄して新規生成してください。

## SharePointの管理メタデータ

既定はMarkdownと同じフォルダーに、`<Markdown名>.report.json`という隣接メタデータファイルを保存します。列作成や`Sites.ReadWrite.All`は不要です。JSONには次の文字列フィールドを保持します。

`subjectId`, `reportDate`, `activityDate`, `periodStart`, `periodEnd`, `reportType`, `status`, `revision`, `method`, `promptHash`, `runId`

さらに`schemaVersion=1`, `contentItemId`, `contentFileName`, `contentSha256`を保存します。`reportDate`等は `yyyy-MM-dd`、revisionはUNIXミリ秒の文字列です。ファイル名は種別・対象日・本人ID・revision・ランダムIDの組み合わせで、一致時は上書きしません。本文アップロード後にmetadataを保存し、後者が失敗した場合は保存成功を返しません。孤立したMarkdownは確定日報として集約しません。週報では対応する本文が同じフォルダーに存在することとSHA-256を検証します。

既存ファイルのlistItem fieldsに同じ名前の文字列メタデータがある場合も読み取れます。ただしAPIは列を書き込みません。GraphのlistItem更新には`Sites.ReadWrite.All`が必要なため、今回の承認済みbaseline（`Files.ReadWrite.All`と`Sites.Read.All`）で成立するファイル方式を使用します。

週報は両providerともGraph OBOで同じ形式のSharePointファイルを読みます。**これはWork IQ日報の収集をGraphに変更するものではなく、確定日報の決定的な集約境界です。** 設定フォルダー直下のMarkdown・confirmed・daily・同じmethod・指定subjectId・指定reportDateだけを選び、同じ対象者/日付は最大revisionを使用します。更新日時で8月データを検索しません。上長週報は1〜30人の明示Object IDリストで、別のDirectory権限・直属メンバー探索は追加しません。

## 実装上の取得範囲・上限

- JST、土日、内閣府の2026/2027年公式休日、追加会社休日。2026-01-01の前日は2025-12-31。未登録年の営業日を推測しません。
- Graph: Calendar view、Chat一覧/messages、joinedTeamsとassociatedTeams→allChannels/messages/replies、Calendar join URL→onlineMeeting→transcripts/content。古い親投稿の対象日返信も取得します。`recent`とメールは使用しません。
- Chatの`lastModifiedDateTime gt`と同じプロパティのdescending orderを使い、本文はcreatedDateTimeでJST対象日に限定します。編集時刻の上限で古い投稿を落としません。
- 1要求8 work items・取得slice18秒・全run500 Graph呼出し（共通設定による）。同時数は1（共通設定の上限4以内）、待機queue上限1,500。429/503/504は最大4試行、長いRetry-Afterは要求を閉じて再開します。
- Shared channelは既存のTeam.ReadBasic.All / Channel.ReadBasic.AllでassociatedTeamsとallChannelsを探索します。Graphが返す`@odata.id`からhost teamを識別し、公式の既知問題に従って`/tenants/...`を取り除き、同じ委任トークンで取得します。外部host・不正なGraphパスは拒否します。探索成功なら無条件にpartialを付けません。403等は実際の処理段階・HTTP statusを記録します。
- 入力budgetは従来どおり24,000 tokens（共通設定）。GPT-4.1の`o200k_base`をMicrosoft.ML.Tokenizersで計測し、共通instructions・入力JSON・追加余裕1,000 tokensを含めて制限します。UTF-8 byte数は診断値であり、token数と同一視しません。辞書はNuGetから同梱され、実行時ダウンロードはありません。
- stagingは既存の予約分を差し引いた16,000 tokens、最大256根拠。大きな本文は主題のある先頭と末尾を残し、中間省略を明示します。情報源別の使用token量が大きいグループから縮小し、1情報源だけで枠を占有しにくくします。日時・出典・本人帰属を保持し、送信時は情報源・日時順に整列します。この共通選択をGraph/Work IQの両方へ適用し、Work IQの取得カテゴリは増やしません。
- `metrics.inputSelectionVersion=balanced-excerpts-v2-o200k`を比較条件に加えてください。共通prompt/config/schemaは変更しませんが、旧byte方式との結果を同じ取得profileとして比較しないでください。旧runの継続は409で新規生成を要求します。
- 予定表会議件数・時間は会議（online meetingまたはattendeesあり）の予定枠のみ。重なりを統合し、対象日にクリップします。実参加時間ではありません。Work IQの同等集計は取得不能としてnullです。
- 既定のWork IQ `legacy-ask-v1` はaskのみの別Agentで根拠JSONを収集。新profileは上記参照。OAuth/approvalのFoundry IDはserver側だけに保存します。検索の網羅性をGraphページングと同一視せずpartialを明示します。
- Collectorの`allowed_tools`は作成時の`["ask"]`とFoundry/SDKが返す`{"tool_names":["ask"]}`の両形式を検証します。空・全許可・追加ツール・不正型は拒否します。既存のserver URL・require_approval定義は変更せず、承認継続の検証も維持します。
- 根拠が0件ならレポートを創作せず422。部分的根拠は利用者のallowPartial=trueが必要。Markdownの必須見出し・引用IDを検証し、最終生成の修復は最大1回。編集保存でも検証します。
- auditは所要時間、取得・採用・除外件数、retry、token使用、provider、共通prompt/config/schema hash、Agent/model version。通常ログへ原文・OAuth token・未加工例外を出しません。

## 確認した公式資料

- [内閣府: 国民の祝日](https://www8.cao.go.jp/chosei/shukujitsu/gaiyou.html)
- [Graph: Chat messagesのfilter/orderby](https://learn.microsoft.com/en-us/graph/api/chat-list-messages?view=graph-rest-1.0)
- [Graph: folder childrenとページング](https://learn.microsoft.com/en-us/graph/api/driveitem-list-children?view=graph-rest-1.0)
- [Graph: meeting transcriptsと利用制約](https://learn.microsoft.com/en-us/graph/api/onlinemeeting-list-transcripts?view=graph-rest-1.0)
- [Graph: listItem更新にはSites.ReadWrite.Allが必要](https://learn.microsoft.com/en-us/graph/api/listitem-update?view=graph-rest-1.0)
- [Graph: shared membershipを含むassociatedTeams](https://learn.microsoft.com/en-us/graph/api/associatedteaminfo-list?view=graph-rest-1.0)
- [Graph: allChannelsとhost teamの識別](https://learn.microsoft.com/en-us/graph/api/team-list-allchannels?view=graph-rest-1.0)
- [Microsoft.ML.Tokenizers: encoding指定](https://learn.microsoft.com/en-us/dotnet/api/microsoft.ml.tokenizers.tiktokentokenizer.createforencoding?view=ml-dotnet-preview)
- [OpenAI: GPT-4.1のo200k_base対応](https://raw.githubusercontent.com/openai/tiktoken/main/tiktoken/model.py)

### 取得状態の読み分け

`coverage.count`は従来どおり現在の採用数です。`retrievedCount`は重複除外後・本文縮小前に取得できた根拠数、`sentCount`は最終生成への送信数です。`collectionStatus`（API取得）と`inputStatus`（入力選択）を分離し、`truncatedCount`・`inputDroppedCount`で入力側の省略を示します。取得済み本文の省略は権限不足ではありません。

`diagnostics`は段階別の数値だけです。例：`chats.items/pages`、`messages.items/periodMatched/periodExcluded/selfAuthored/otherAuthored`、`activity.periodMatched`、`schedule.periodMatched`、`activityCalendar.joinUrlsFound`、`meetings.items`、`transcripts.items`。Chatのfilter結果が空か、取得したメッセージが期間外だったかを区別できます。

`errors`は`stage`・`httpStatus`・許可リスト内の`graphCode`・回数だけを保持します。HTTP本文・メッセージ・名前・URL・tokenは診断やログにコピーしません。403はアクセス拒否の観測であり、委任scope・メンバーシップ・tenant policyのどれが原因かは別途確認が必要です。

Chat/Channelの`evidence.attribution`は`self / other / unknown`です。`from.user.id`と本人oidが有効なGUIDで一致した場合のみself、異なる有効GUIDならother、送信者ID欠落・不正・アプリ送信者のみならunknownです。表示名で補完しません。`messages.selfAuthored / otherAuthored / unknownAuthored`を別々に数えます。旧版はID欠落も「本人ではない」と表示していたため、旧レポートの「本人発言確認なし」から他者発言だけだったとは断定できません。
selfは投稿者が本人であることだけを示し、投稿で言及した業務をすべて本人の実績と認定するものではありません。

| reason | 証明できること |
|---|---|
| `no_accessible_chats_returned` | 本人のChat一覧APIが成功し0件。組織全体にChatがないという意味ではない |
| `no_messages_returned_for_query` | 指定filterのmessages APIが成功し0件 |
| `no_messages_in_activity_period` | messagesは取得できたが作成日時が実績対象日の範囲外 |
| `no_online_meeting_join_urls_in_activity_calendar` | 実績対象日のCalendarからjoin URLが見つからず、onlineMeeting検索未実施 |
| `online_meeting_lookup_empty` | join URLでonlineMeeting検索を実施し成功、結果0件 |
| `transcript_list_empty_recording_or_retention_unknown` | transcript一覧取得に成功、結果0件。未記録か保持切れかまでは不明 |
| `meetings:graph_http_403`等 | その処理段階でアクセス拒否。空データとは扱わない |

旧出力だけでは新しい段階別内訳を復元できません。修正版で同じ日付・本人の新規runが必要です。

2026-09-30にアクセスを確認。Foundry SDKはインストール済みAzure.AI.Projects 2.0.1 / Azure.AI.Extensions.OpenAI 2.0.0のpublic metadataとビルドでも確認しています。

## 合成資料による実サービス検証

2026-09-30、`graph-report-agent:2`と`workiq-report-agent:2`に同じ2026-08-04日報用合成根拠を渡し、実際の`FoundryService.Generate`を確認しました。両方とも必須9見出し・引用ID検証に成功、修復0回、入力1,360 tokens、出力は459/458 tokensでした。共通prompt/config hashとモデルversionの前後検証にも成功しています。

この確認は既存Azure CLIの委任Foundryトークンによる最終生成部分のみです。FunctionsへのBearer/OBO、Work IQのM365収集、Graph/SharePoint操作、実ユーザーのレポート品質を検証したものではありません。Agent `:1`はtemperature未定義であり、修正版では設定不一致として拒否されます。使用するAgent versionの設定を必ず更新してください。

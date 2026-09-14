# Troubleshooting

| Symptom | Check |
|---|---|
| Subscription not found | Sign in with the expected tenant, then `az account set --subscription ...` |
| `WorkIQAgent.Ask` not found | Provision the Work IQ service principal and repeat preprovision |
| Admin consent fails | Activate Global Administrator, refresh Azure CLI token, rerun preprovision |
| SPA gets 401 | Check API scope、redirect URI、Easy Auth audience、enterprise app assignment |
| Function gets no user assertion | Check Easy Auth token store and Authorization/header forwarding |
| OBO returns `AADSTS70021` | FIC issuer、managed identity principal ID、audience must match exactly |
| OBO token endpoint returns HTTP 400 / `AADSTS650057` | Function API app に `https://ai.azure.com` の delegated `user_impersonation` permission と admin consent があるか確認 |
| Foundry returns `oauth_consent_request` repeatedly | Check Work IQ app redirect URI、`offline_access`、refresh token revocation |
| MCP tool is absent | Verify Prompt Agent version and `project_connection_id` |
| MCP mutation appears | Stop release; agent allowlist or tenant policy is incorrect |
| MCP timeout | Non-streaming MCP calls have a 100-second limit; narrow the question |
| SWA can't call Function | Check Function CORS origin and CSP `connect-src` |
| No Foundry traces | Verify the Application Insights project connection and RBAC |
| Secret expiry banner appears | Run the rotation script and validate consent before removing old credential |
| User A data appears for User B | Disable the app immediately and investigate OBO/response ID isolation |
| Entra consent 後も popup が閉じない | MSAL Browser v5 の `/redirect.html` Bridge が配信され、Entra SPA redirect URI が同じ URL と完全一致することを確認 |
| Work IQ 認証で blank tab が開く | async callback から自動で tab を開かず、表示された consent link をユーザークリックで開く。認証後の blank page は完了画面の場合があるため閉じて親画面で続行する。古い link は再発行する |
| 「認証ページを開く」が反応しない | popup block を画面に表示し、「同じタブで開く」を使う。旧UIから sessionStorage に残った link は「認証リンクを再発行」で更新する |
| consent controls がすべて反応しない | session schema v2 で旧状態を破棄し、質問を再送する。native anchor または「認証URLをコピー」で browser address bar から直接開く |

## Validation commands

```powershell
dotnet build WorkIqProfileChat.slnx
dotnet test WorkIqProfileChat.slnx --no-build

Push-Location src/web
npm run build
npm run lint
npm test
Pop-Location

az bicep build --file infra/main.bicep
```

MSAL Browser v5 の callback 要件は [Redirect Bridge の公式手順](https://learn.microsoft.com/entra/msal/javascript/browser/redirect-bridge) を参照してください。Bridge page では React、router、`MsalProvider` を起動せず、`broadcastResponseToMainFrame()` だけを実行します。

# Operations

## Monitoring

Function and Foundry use one Application Insights resource and one Log Analytics workspace with 30-day retention.

Safe telemetry fields:

- timestamp
- operation/trace ID
- Function name
- Foundry response/run ID
- tool name
- latency
- HTTP/result status

Do not log prompt、response、tool arguments、tool result、token、email address、person name、file name、meeting subject.

Example trace query:

```kusto
union requests, dependencies, traces
| where timestamp > ago(24h)
| project timestamp, operation_Id, itemType, name, target, resultCode, success, duration
| order by operation_Id asc, timestamp asc
```

## Work IQ secret expiry

The authenticated UI calls `/api/status`. The API compares the non-secret expiration timestamp deployed with the Function configuration. When 30 days or less remain, the UI displays a rotation warning. このAPIはsecret値を返しません。IaCの目標構成はKey VaultのPublic access Disabled、Private Endpoint、RBAC、purge protectionです。既存環境は[移行・検証](private-network.md)を完了するまで閉域化済みとは扱わないでください。

Rotate safely:

```powershell
$env:AZURE_DEV_USER_AGENT = 'microsoft_foundry_skill'
./scripts/rotate-workiq-secret.ps1 -WhatIf
./scripts/rotate-workiq-secret.ps1
```

The script:

1. Creates a replacement app secret.
2. Writes it to Key Vault.
3. Updates the Foundry project connection.
4. Updates the azd secret.
5. Leaves the previous credential in place until end-user consent validation succeeds.

Remove the previous Entra credential only after verification.

rotation scriptはARM経由のsecret更新とFoundry connection更新を使います。通常の`az keyvault secret show/list`とは異なり、Key Vaultのデータプレーンへ直接接続する処理ではありません。閉域後のデータ直接管理にはVNet内の実行環境と必要なRBACを用意してください。

## Adding users

Add users only to the dedicated allowlist group. Do not assign individual users directly to Azure resources unless diagnosing a temporary RBAC issue.

```powershell
./scripts/add-allowed-user.ps1 -UserPrincipalName user@contoso.com
```

The helper adds direct allowlist membership and the least-privilege `Foundry Agent Consumer` project role. Direct Foundry assignment avoids stale group-claim propagation during first-use testing. After membership changes, allow time for app-role claims and RBAC propagation.

If the current Foundry Preview path rejects `Foundry Agent Consumer`, use the broader project-scoped workaround only after approval:

```powershell
./scripts/add-allowed-user.ps1 `
  -UserPrincipalName user@contoso.com `
  -FoundryRole 'Foundry User'
```

## Cost

No Cost Management Budget is created in this iteration. Review Functions、Foundry model token usage、Copilot Credits、Application Insights ingestion regularly.

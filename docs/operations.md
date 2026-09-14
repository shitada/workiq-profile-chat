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

The authenticated UI calls `/api/status`. The API compares the non-secret expiration timestamp deployed with the Function configuration. When 30 days or less remain, the UI displays a rotation warning. この API は secret 値を返しません。Key Vault は RBAC と purge protection を使用しますが、現在の IaC では Public access が有効で、network ACL の既定動作は Allow です。ネットワーク隔離済みとは扱わないでください。

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

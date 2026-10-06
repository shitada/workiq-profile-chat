# Deployment

## Prerequisites

- Azure CLI、Azure Developer CLI、Bicep、.NET 10、Node.js 22+、PowerShell 7
- Owner and User Access Administrator on the target subscription
- Global Administrator available for Work IQ admin consent
- Microsoft 365 admin available for Work IQ MCP tenant policy
- GPT-4.1 availability/quota in the selected region
- IPAMで承認されたVNet／Function subnet／Private Endpoint subnetのCIDR、Microsoft.Network／Microsoft.Appの登録と必要権限

既存の公開Storageを使う環境は、以下の通常provisionを先に実行せず、[閉域化の段階移行](private-network.md)に従ってください。

## IaC-only workflow

All resource definitions and scripts must be reviewed before the first provision. Portal-created resources are not part of the supported final state.

```powershell
Set-Location projects/workiq-profile-chat
$env:AZURE_DEV_USER_AGENT = 'microsoft_foundry_skill'

az login --tenant '<tenant-id>'
az account set --subscription '<subscription-id>'
azd auth login --tenant-id '<tenant-id>'

azd env new workiq-profile-chat --no-prompt
azd env set AZURE_SUBSCRIPTION_ID '<subscription-id>'
azd env set AZURE_TENANT_ID '<tenant-id>'
azd env set AZURE_LOCATION 'eastus2'
azd env set AZURE_RESOURCE_GROUP 'workiq_agent_test'
azd env set AZURE_NETWORK_ADDRESS_PREFIX '<approved-vnet-cidr>'
azd env set AZURE_FUNCTION_SUBNET_PREFIX '<approved-function-subnet-cidr>'
azd env set AZURE_PRIVATE_ENDPOINT_SUBNET_PREFIX '<approved-pe-subnet-cidr>'

azd provision --no-prompt
azd deploy --no-prompt
```

`AZURE_DEV_USER_AGENT` is process-only and must not be persisted in azd environment or source.

## Hook sequence

### preprovision

- Confirms the Azure context.
- Registers required providers.
- Creates the allowlist group and initial membership.
- Creates/reuses three Entra apps and service principals.
- Exposes the Function API scope and assigns the SPA.
- Adds `WorkIQAgent.Ask` and performs admin consent.
- Creates a one-year Work IQ client secret and stores it as an azd secret.

### Bicep

- Creates `workiq_agent_test` if absent.
- Deploys all Azure resources with one location.
- Storage／Key VaultのPublic accessをDisabledにし、Private Endpoint／Private DNSとFunctionの送信側VNet統合を構成します。Ignoreタグは付与しません。
- Enables Function Easy Auth and shared Application Insights.
- Work IQ OAuth secretを`Microsoft.KeyVault/vaults/secrets`経由で保存します。Key VaultのPublic accessはDisabled、network ACLはDeny、RBACは有効です。ARM管理操作とPrivate Endpoint経由のデータプレーン操作は区別します。

### postprovision

- Adds the production SPA redirect URI.
- Creates the API app FIC for the Function UAMI.
- Adds the Foundry OAuth redirect URI when exposed by the connection.
- Creates/updates the Work IQ OAuth2 and Application Insights project connections with `azd ai connection`.
- Creates a Prompt Agent version with only the Work IQ `ask` tool.
- Sets Vite build variables for `azd deploy`.

## Verification

```powershell
az resource list -g workiq_agent_test `
  --query "[].{name:name,type:type,location:location}" -o table

az storage account show -g workiq_agent_test -n '<storage-name>' `
  --query "{publicNetworkAccess:publicNetworkAccess,allowSharedKeyAccess:allowSharedKeyAccess}" -o json
```

Expected Storage values: `publicNetworkAccess=Disabled`、`allowSharedKeyAccess=false`。PE承認状態、VNet内のPrivate DNS、health、CORS、認証付きチャットと閉域状態での再デプロイも確認してください。

Azure MonitorのApplication Insights Smart Detectionと、Private DNS zones／VNet linksは`global` locationです。その他のリージョナルリソースは同一locationを使用します。

Verify Application Insights contains correlated Function and Foundry traces before adding users. Foundry server-side traces can contain prompt/tool content, so restrict telemetry access and retain it for only 30 days.

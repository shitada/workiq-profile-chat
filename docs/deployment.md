# Deployment

## Prerequisites

- Azure CLI、Azure Developer CLI、Bicep、.NET 10、Node.js 22+、PowerShell 7
- Owner and User Access Administrator on the target subscription
- Global Administrator available for Work IQ admin consent
- Microsoft 365 admin available for Work IQ MCP tenant policy
- GPT-4.1 availability/quota in the selected region

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
- Applies `SecurityControl=Ignore` to the Function Storage account.
- Enables Function Easy Auth and shared Application Insights.
- Work IQ OAuth secret を `Microsoft.KeyVault/vaults/secrets` 経由で保存します。現在の IaC は Key Vault の `publicNetworkAccess: 'Enabled'`、network ACL の `defaultAction: 'Allow'`、RBAC 有効という構成です。ネットワーク隔離は行っていません。

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
  --query tags.SecurityControl -o tsv
```

Expected Storage tag value: `Ignore`.

Azure Monitor also creates an Application Insights Smart Detection resource with `global` location. This platform-managed rule is the only Azure Monitor non-regional exception.

Verify Application Insights contains correlated Function and Foundry traces before adding users. Foundry server-side traces can contain prompt/tool content, so restrict telemetry access and retain it for only 30 days.

# Security and privacy

## Data minimization

- Work IQ `ask` is the only enabled MCP tool.
- Raw `fetch`、`fetch_blob`、CRUD、action tools are not enabled.
- The 90-day scope is a prompt default, not permission expansion.
- Responses summarize rather than quote message bodies.
- No application database stores chat or profiles.

## Safe “traits”

Allowed:

- recurring collaboration topics
- meeting/email cadence
- documented responsibilities
- visible follow-up patterns
- frequently referenced projects

Disallowed:

- personality diagnosis
- health/disability inference
- protected-class inference
- political/religious inference
- private-life inference
- sentiment surveillance
- performance score、ranking、promotion or termination recommendation

## Threat controls

- Prompt injection can't expand `allowed_tools`.
- Mutation is denied by both the agent definition and Work IQ tenant policy.
- Easy Auth returns 401 before Function execution for unauthenticated requests.
- Function checks tenant and allowlist group.
- CORS allows only the production SWA and localhost development origin.
- CSP denies framing、camera、microphone、geolocation、third-party scripts.
- Response payloads use `Cache-Control: no-store`.
- Shared-key Storage access is disabled.
- Key Vault uses RBAC and purge protection.

### Key Vault network boundary

`infra/modules/platform.bicep`の目標構成はStorage／Key Vaultの`publicNetworkAccess: 'Disabled'`、network ACLの`defaultAction: 'Deny'`、`bypass: 'None'`です。Blob／vault Private EndpointとPrivate DNSを通じて接続します。既存環境への適用状況は[移行手順](private-network.md)で検証してください。

ネットワーク到達性とsecretへのアクセス権限は別です。Private Endpoint経由でもMicrosoft Entra認証とRBACによる認可が必要です。ARM管理プレーン、公開のWeb／Function API、Foundry／Work IQ通信は今回の閉域化対象外です。Ignoreタグの再付与や認証無効化を復旧手段にしません。

参照: [Azure Key Vault のネットワーク セキュリティ](https://learn.microsoft.com/en-us/azure/key-vault/general/network-security)

## Trace privacy

Foundry server-side tracing can include prompts、outputs、tool arguments、and tool results. Treat the shared Application Insights resource as confidential production telemetry. Access is restricted through RBAC, retention is 30 days, and application code doesn't add content to span attributes.

Before production use, verify the actual trace payload and disable/reduce content capture if the current Foundry control plane exposes that setting.

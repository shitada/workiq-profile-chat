# Authentication and authorization

## Entra objects

`scripts/preprovision.ps1` creates these tenant objects idempotently:

1. **SPA public client** — PKCE, no secret, SPA redirect URIs.
2. **Function API confidential app** — exposes `access_as_user` and the `ProfileChat.User` app role, then requires enterprise app assignment.
3. **Work IQ OAuth app** — delegated `WorkIQAgent.Ask`, client secret, Foundry Web redirect URI.
4. **Allowlist security group** — initial member is the signed-in deployment user.

No Graph application permission is added.

## OBO

The incoming Function API token is a user assertion. The Function API app proves its identity with a signed assertion from the user-assigned managed identity:

```text
SPA token (aud=Function API)
  -> Function Easy Auth
  -> OBO exchange (API app + managed identity FIC)
  -> Foundry user token (scope=https://ai.azure.com/.default)
  -> Foundry OAuth project connection
  -> Work IQ user credential
```

The Function API app also has the tenant's `https://ai.azure.com` delegated `user_impersonation` permission with admin consent. Azure RBAC alone doesn't authorize Entra to issue an OBO token; both delegated permission/consent and Foundry project RBAC are required.

The Function system/application identity alone must never call Work IQ. A shared token cache key must never be used across users.

## Consent

- Global Administrator grants tenant-wide admin consent for `WorkIQAgent.Ask`.
- Each end user follows the Foundry `consent_link` on first tool use.
- Foundry partitions OAuth credentials by project, connection name, and user.
- Revoked or expired refresh tokens cause a new `oauth_consent_request`.

## Roles

- Deployment user: Owner/User Access Administrator for provisioning and RBAC.
- Allowed user group: `ProfileChat.User` enterprise app role and Foundry Agent Consumer on the project.
- Function UAMI: Storage Blob Data Owner and Monitoring Metrics Publisher.
- Deployment user: Foundry User on the project for Prompt Agent version management.
- Foundry project identity: Log Analytics Reader and Privileged Monitoring Data Reader on Application Insights for traces.

## Mandatory two-user test

Use two users with visibly different profiles:

1. User A consents and requests a self profile.
2. User B uses a separate browser profile, consents, and requests a self profile.
3. Verify B never sees A's identity, activity, `previous_response_id`, or inaccessible data.
4. Revoke A's Work IQ consent and verify only A is prompted again.
5. Cross-submit A's response ID as B and verify the request fails rather than returning content.

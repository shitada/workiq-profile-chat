# Agent instructions

This project uses Microsoft Foundry, Azure Functions, Azure Static Web Apps, and Work IQ MCP.

- Read the `microsoft-foundry`, `azure-prepare`, `azure-validate`, and `azure-deploy` skills before changing or deploying Azure resources.
- Bicep + azd is the only supported infrastructure/deployment path.
- Never commit tenant IDs, subscription IDs, app IDs, group IDs, user IDs, secrets, tokens, `.azure` state, Microsoft 365 content, or generated profiles.
- Preserve delegated OBO identity. Never replace the signed-in user with application permissions or a shared user token.
- Keep Work IQ mutation tools disabled. The agent tool allowlist is `ask` only.
- Do not log prompt text, model output, MCP arguments/results, names, email addresses, or tokens.

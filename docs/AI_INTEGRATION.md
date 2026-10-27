# AI integration — Ollama local and Anthropic API

CodeAtlas 0.10.0 adds an optional engineering assistant. It is intentionally separate from the static scanner and local service manager: model output is an explanation/recommendation, never evidence that a dependency or runtime flow exists.

## Ollama (recommended starting point)

1. Start Ollama on the Mac and confirm a model is installed (`ollama list`).
2. Open **Inteligencia → Configuración IA**.
3. Select **Ollama local**, leave the base URL as `http://localhost:11434`, and choose a model discovered by **Probar conexión**.
4. Save and open **Asistente IA**.

CodeAtlas queries `GET /api/tags` to enumerate local models and sends a non-streaming request to `POST /api/chat`. The standard Ollama local API does not require an API key. A remote, forwarded, or custom base URL may send the context outside the Mac, so confirm the URL before using it.

## Anthropic API

1. Open Claude in a browser and check **Settings → Billing → API credits**.
2. If eligible, link the Claude Console organization where you intend to create the API key. Claiming a credit is separate from having a Claude Max subscription; an API key is created in Claude Console and is not the same as a Claude app session/token.
3. Create an API key in the linked Console organization and enter it in **Configuración IA → Anthropic API**.
4. Save. CodeAtlas stores the secret in the macOS Keychain, not in the repository, JSON settings, or UserDefaults. Use **Probar conexión** to query the model catalog, then select a model ID and save.

Official credit terms can change. At the current documentation date, Anthropic lists $100/month of API credit for Max 5x and $200/month for Max 20x; the credit is applied to Claude API / Managed Agents / Agent SDK / Console Playground, not Claude app extra usage or Claude Code. Eligible new subscribers can claim after seven days; unused credits expire each billing cycle and do not roll over. Verify the current terms in the official docs before relying on a balance:

- https://platform.claude.com/docs/en/about-claude/api-credits-for-subscribers
- https://support.claude.com/en/articles/9876003-i-have-a-paid-claude-subscription-pro-max-team-or-enterprise-plans-why-is-claude-api-usage-billed-separately-from-my-paid-claude-plan

## Context and privacy

- The **Adjuntar resumen de arquitectura** toggle is explicit and enabled by default. It sends bounded structured metadata: repository labels, project names/types/paths, endpoints, contracts, inferred relationships, evidence references, business-flow summaries, warnings and snapshot deltas.
- It does not automatically attach source file contents, `.http` bodies, private HTTP environment files, environment variables, access tokens, or credentials.
- The user-authored question is sent to the selected provider.
- For Anthropic, prompts and selected metadata leave the machine and are sent to Anthropic's API.
- With Ollama bound to `localhost`, model inference occurs on the local Ollama server. Check the configured host if it is not localhost.
- The app does not silently invoke AI after a Git change. AI calls occur only when the user submits a question.
- Responses must be checked against source evidence. The assistant does not mutate code, run commands, start services, edit `.http` files or commit changes.

## Initial assistant workflows

Quick prompts help to explain the architecture, review snapshot changes, identify risks, and suggest tests. Responses are single-turn in this version; conversation history and context retrieval over selected source files are planned future work. The structured context is capped to avoid sending an entire repository snapshot unboundedly.

## Key handling

Anthropic keys are stored using a Keychain generic-password item under service `com.codeatlas.ai.anthropic`. Removing a key deletes the item. No API key is written to the code repository or included in the snapshot. Do not paste API keys into the question box.

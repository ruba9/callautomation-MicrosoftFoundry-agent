# CallAutomationOpenAI — Bilingual ACS Voice Agent (Azure AI Foundry)

An inbound call bot built on **Azure Communication Services (ACS) Call Automation** that answers phone calls, converses in **English or Gulf Arabic**, escalates to a live agent on request, and hangs up automatically once the caller is done — powered by an **Azure AI Foundry** agent.

> This is a customized sample derived from the [Azure Communication Services OpenAI quickstart](https://github.com/Azure-Samples/communication-services-dotnet-quickstarts). It is provided as-is for reference/demo purposes, not as an officially supported Microsoft sample.

## Features

- Answers inbound calls via the ACS `Microsoft.Communication.IncomingCall` Event Grid event.
- Bilingual greeting (English + Gulf Arabic) using SSML with per-language neural voices.
- Multi-turn conversation handled by an Azure AI Foundry agent (`agent_reference` over the Responses API), with per-call conversation state so context carries across turns.
- Per-turn sentiment score, intent, and category classification.
- Automatic transfer to a live human agent when the caller asks for one.
- Automatic call hang-up once the caller says goodbye / has nothing more to ask (no more calls left hanging open).
- Silence-timeout retry, then a graceful goodbye and hang-up if the caller never responds.

## Architecture / call flow

1. Event Grid delivers an `IncomingCall` event to `POST /api/incomingCall`.
2. The app answers the call, plays a bilingual greeting, and starts speech recognition.
3. Each recognized utterance is sent to the Foundry agent, which returns a reply plus `escalate`/`endCall` flags.
4. The reply is spoken back (voice/locale matched to the detected language) and recognition restarts — unless the caller wants a human (`escalate`, triggers `TransferCallToParticipantAsync`) or is done (`endCall`, triggers a goodbye + hang-up).
5. All in-call events (play completed/failed, recognize completed/failed, transfer accepted/failed) are delivered to `POST /api/callbacks/{contextId}`, which the app already registered as the callback URI when answering.

## Prerequisites

- [.NET 9 SDK](https://dotnet.microsoft.com/download)
- An Azure subscription with:
  - An **Azure Communication Services** resource with a phone number (or other calling channel) enabled
  - An **Azure AI multi-service (Cognitive Services)** resource, used by ACS Call Automation for built-in speech-to-text/text-to-speech (`CallIntelligenceOptions`)
  - An **Azure AI Foundry** project with a deployed agent (e.g. GPT-5-backed) reachable via the project's Responses API (`agent_reference`)
- [Azure CLI](https://learn.microsoft.com/cli/azure/install-azure-cli) signed in (`az login`), with permission to create an Event Grid subscription on the ACS resource
- [devtunnel CLI](https://learn.microsoft.com/azure/developer/dev-tunnels/get-started) (or another tunneling tool) to expose your local port publicly so ACS/Event Grid can reach your webhooks
- An identity (your local `az login` user for dev, or a managed identity in production) with a role granting access to call the Foundry project's Responses API, e.g. **Azure AI User** on the Foundry account/project — the app authenticates to Foundry with `DefaultAzureCredential`, not an API key
- (Optional) An E.164-format phone number to transfer escalated calls to

## Configuration

All settings live in `appsettings.json`. Replace the placeholder values with your own resources before running — **never commit real secrets**:

| Key | Description |
|---|---|
| `DevTunnelUri` | Public HTTPS base URL of your dev tunnel, e.g. `https://<id>-8080.<region>.devtunnels.ms` |
| `CognitiveServiceEndpoint` | Endpoint of your Azure AI / Cognitive Services multi-service resource |
| `AcsConnectionString` | Connection string of your Azure Communication Services resource (contains an access key) |
| `FoundryProjectEndpoint` | Your Azure AI Foundry project endpoint, e.g. `https://<resource>.services.ai.azure.com/api/projects/<project>` |
| `FoundryAgentName` | Name of the Foundry agent to invoke via `agent_reference` |
| `FoundryAgentVersion` | (optional) specific agent version; leave empty to use the latest |
| `AgentPhoneNumber` | E.164 phone number to transfer escalated calls to (e.g. `+15551234567`) |

For local development, prefer putting real values in `appsettings.Development.json` (already git-ignored) instead of editing `appsettings.json` directly. For production, use environment variables, `dotnet user-secrets`, or Azure Key Vault rather than checking secrets into any config file.

## Running locally

1. Sign in to Azure: `az login`
2. Start a dev tunnel on the port you'll run the app on:
   ```powershell
   devtunnel host -p 8080 --allow-anonymous
   ```
   Copy the generated tunnel URL into `DevTunnelUri` in your config.
3. Restore and run:
   ```powershell
   dotnet restore
   dotnet run --urls http://localhost:8080
   ```
4. Create the Event Grid subscription once, pointing at your tunnel (replace the placeholders):
   ```powershell
   az eventgrid event-subscription create `
     --name acs-incomingcall `
     --source-resource-id <ACS resource ID> `
     --endpoint "<DevTunnelUri>/api/incomingCall" `
     --included-event-types Microsoft.Communication.IncomingCall `
     --endpoint-type webhook
   ```
5. Call the ACS phone number — the app answers, greets, and converses with you.

## Customizing the agent's behavior

The system prompt (dialect rules, tone, escalation/goodbye detection, and the JSON reply schema) lives in the `answerPromptSystemTemplate` string in `Program.cs`. Adjust the tone/language rules there, and update the matching `AgentReply` class if you add or rename fields in the JSON contract.

## Notes

- The Foundry agent is invoked through the project's `openai/v1/responses` endpoint using Microsoft Entra ID auth only (no API keys) via `DefaultAzureCredential`.
- Conversation state (the Foundry `previous_response_id`) is tracked in-memory per call connection ID; it resets if the app restarts.

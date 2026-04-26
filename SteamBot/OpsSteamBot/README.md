# OpsSteamBot

Steam adapter for `RustOpsAgent`.

## What it does

- Logs into a dedicated Steam account with SteamKit2
- Relays agent-generated admin messages from the agent outbox
- Forwards natural-language admin chat into the agent inbox
- Accepts approval and feedback replies from whitelisted Steam IDs
- Writes decisions and feedback into the agent inbox folders
- Keeps Steam as a transport adapter instead of the primary control surface

## Setup

1. Copy `botsettings.example.json` to `botsettings.json`.
2. Copy `config.env.example` to `config.env` (at the repo root) and fill in the shared values.
3. Prefer leaving agent inbox and outbox paths in `config.env`; only keep bot-specific overrides in `botsettings.json`.
4. Start the API and `RustOpsAgent`.
5. Run the bot:

```powershell
dotnet run --project H:\RUSTICALANDPROJECTS\AIProject\SteamBot\OpsSteamBot\OpsSteamBot.csproj
```

If Steam asks for a guard code, the bot will prompt in the console during login.

The bot also reads shared settings from `config.env` (repo root) or `RUSTOPS_ENV_FILE` when present.
Mixed path separators in config values are normalized at startup, so the published Linux deployment no longer breaks when an older config still contains `..\\..\\agent\\...`.

## Direct control commands

- `help`
- `ping`
- `approve <actionId> [reason]`
- `reject <actionId> [reason]`
- `feedback <actionId> <good|bad|note> [text]`

All other messages are forwarded to the agent for interpretation, for example:

- `what servers are running`
- `status vanilla`
- `health modded`
- `restart onegrid`
- `what happened recently`

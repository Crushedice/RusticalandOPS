# Known Errors

Plugin compile failures should be checked through the existing Oxide validation and plugin update flow before editing plugin source.

## Plugin Source

Raw plugin source should be indexed into the plugin reference index, not imported as normal semantic memory.

## Compile Failures

Root causes:
- Plugin built for older Rust version
- Missing dependency
- Syntax / API changes

Process:
1. Check console
2. Check oxide/logs/
3. Look for updated plugin version
4. Only modify code as last resort

## Permissions Issues

Symptom:
- "no permission"

Fix:
- Oxide denies everything by default
- Grant explicitly:
  oxide.grant group default plugin.use

## Plugin Not Working

Checklist:
- Is Oxide installed?
- Plugin compiled?
- Permissions granted?
- Config exists?

## Config Issues

Common problems:
- Missing config → start server once
- Invalid JSON → plugin resets config

Rule:
Validate JSON before reload

## Performance Issues

Symptoms:
- Lag
- Rubberbanding
- High CPU

Cause:
Bad plugin behavior

Fix:
1. Disable all plugins
2. Re-enable one by one
3. Identify offender

## Common Failures

- Plugin outdated → update it
- Oxide missing after update → reinstall
- RCON not working → check +rcon.web 1
- Conflicting plugins → remove duplicates

## Prevention

- Add plugins one by one
- Monitor console after each change
- Keep backups before updates
- Avoid overlapping plugin functionality

Critical rule:
DO NOT ingest plugin source into semantic memory.
Use structured plugin knowledge instead.
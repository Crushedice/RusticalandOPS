# Incident Trend Review — 2026-04-27

## Summary
The majority of recent incidents stem from recurrent Discord API connectivity failures, leading to timeouts, service unavailability responses, and plugin crashes, with a secondary issue of insecure reporting connections.

## Top Pattern
discord_api_connectivity_failure

## Proposed Mitigation
Implement an exponential backoff retry strategy for all Discord API calls and add a local fallback queue to buffer messages during outages.

## Config Suggestion
Add `retry_policy = { max_attempts = 5, backoff_ms = 200 }` and enforce TLS in the reporting server configuration.
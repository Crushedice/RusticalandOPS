# Incident Trend Review — 2026-04-27

## Summary
Many incidents reveal that internal server errors and notification failures are consistently triggered by DNS resolution failures when the server tries to reach the Discord API, compounded by occasional WebSocket timeouts and connection resets.

## Top Pattern
dns_resolution_failure

## Proposed Mitigation
Configure the server to use a reliable public DNS resolver (e.g., 8.8.8.8) and add a fallback DNS entry for the Discord API domain to prevent resolution failures.

## Config Suggestion
Add 8.8.8.8 and 1.1.1.1 to /etc/resolv.conf and insert a hosts entry mapping discord.com to a known IP address.
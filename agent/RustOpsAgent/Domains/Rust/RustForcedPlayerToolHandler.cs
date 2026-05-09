using System.Globalization;
using System.Text.RegularExpressions;
using RustOpsAgent.Core.Contracts;
using RustOpsAgent.Infrastructure.Memory;

namespace RustOpsAgent.Domains.Rust;

// Tool for managing the rusticaland.net launcher-force list. Three operations:
//   query  → POST /getplayer  body=<steamid>  → "No"|"Yes"|"Using"|"<heartbeat datetime>"
//   add    → POST /Sadduser   body=<steamid>
//   remove → POST /removeuser body=<steamid>
//
// The admin can refer to a player by steamid (17 digits, 7656…) or by display name.
// Names are resolved via the local PlayerStore — ambiguous or unknown names produce a
// clarification asking for the steamid, since the apps service is the source of truth
// and we won't invent IDs.
internal sealed class RustForcedPlayerToolHandler : IToolHandler
{
    private const string BaseUrl = "http://apps.rusticaland.net:8853";
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };

    private readonly PlayerStore _playerStore;

    public RustForcedPlayerToolHandler(PlayerStore playerStore)
    {
        _playerStore = playerStore;
    }

    public string Name => "rust.player.forced";
    public IReadOnlyCollection<AdminIntentType> EligibleIntents => new[] { AdminIntentType.PlayerForcedManagement };

    private static readonly Regex SteamIdRegex = new(@"\b7656\d{13}\b", RegexOptions.Compiled);

    private static readonly Regex AddPhraseRegex = new(
        @"\b(?:force|add\s+(?:to\s+)?(?:the\s+)?force(?:d)?(?:\s+list)?|add\s+force(?:d)?)\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex RemovePhraseRegex = new(
        @"\b(?:unforce|deforce|remove(?:\s+from)?\s+(?:the\s+)?force(?:d)?(?:\s+list)?|lift\s+(?:the\s+)?force|stop\s+forcing)\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex QueryPhraseRegex = new(
        @"\b(?:is|check|status|are|verify|whether)\b.*\bforce(?:d)?\b|\bforce(?:d)?\s+status\b|\bon\s+(?:the\s+)?force(?:d)?\s+list\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public async Task<ToolExecutionResult> ExecuteAsync(ToolExecutionContext context, CancellationToken cancellationToken)
    {
        var message = context.Message ?? string.Empty;
        var op = ClassifyOperation(message);

        var (steamId, lookupNote) = ResolveTarget(message, context.Route.Slots.PlayerName);
        if (steamId is null)
        {
            return new ToolExecutionResult(
                false,
                lookupNote ?? "Which player? Give me a steamid (17 digits) or a name I've seen before.",
                null, false, "clarification_required");
        }

        try
        {
            return op switch
            {
                ForcedOp.Add    => await ExecuteAddAsync(steamId, cancellationToken),
                ForcedOp.Remove => await ExecuteRemoveAsync(steamId, cancellationToken),
                _               => await ExecuteQueryAsync(steamId, cancellationToken)
            };
        }
        catch (Exception ex)
        {
            return new ToolExecutionResult(false,
                $"Forced-list request failed for {steamId}: {ex.Message}",
                null, false, "api_error");
        }
    }

    private async Task<ToolExecutionResult> ExecuteQueryAsync(string steamId, CancellationToken cancellationToken)
    {
        var body = await PostAsync("/getplayer", steamId, cancellationToken);
        var (forced, summary) = InterpretGetPlayerResponse(body);

        // Mirror the result into our local roster so the WebUI reflects it without waiting
        // for the next /all sweep. We only update if the player is already known.
        if (forced.HasValue && _playerStore.Get(steamId) is not null)
            _playerStore.MarkForcedStatus(steamId, forced);

        var name = _playerStore.Get(steamId)?.DisplayName;
        var who = string.IsNullOrWhiteSpace(name) ? steamId : $"{name} ({steamId})";
        return new ToolExecutionResult(true, $"{who}: {summary}", null, false, Payload: body);
    }

    private async Task<ToolExecutionResult> ExecuteAddAsync(string steamId, CancellationToken cancellationToken)
    {
        await PostAsync("/Sadduser", steamId, cancellationToken);
        if (_playerStore.Get(steamId) is not null)
            _playerStore.MarkForcedStatus(steamId, true);
        var name = _playerStore.Get(steamId)?.DisplayName;
        var who = string.IsNullOrWhiteSpace(name) ? steamId : $"{name} ({steamId})";
        return new ToolExecutionResult(true, $"Added {who} to the forced list. They'll be required to use the launcher next time they connect.", null, true);
    }

    private async Task<ToolExecutionResult> ExecuteRemoveAsync(string steamId, CancellationToken cancellationToken)
    {
        await PostAsync("/removeuser", steamId, cancellationToken);
        if (_playerStore.Get(steamId) is not null)
            _playerStore.MarkForcedStatus(steamId, false);
        var name = _playerStore.Get(steamId)?.DisplayName;
        var who = string.IsNullOrWhiteSpace(name) ? steamId : $"{name} ({steamId})";
        return new ToolExecutionResult(true, $"Removed {who} from the forced list.", null, true);
    }

    private static async Task<string> PostAsync(string path, string steamId, CancellationToken cancellationToken)
    {
        // The SimpleRESTServer endpoints bind a single string body parameter directly, so
        // we send the raw steamid as plain text (matches the launcher plugin's POST shape).
        using var content = new StringContent(steamId);
        using var resp = await Http.PostAsync(BaseUrl + path, content, cancellationToken);
        var body = (await resp.Content.ReadAsStringAsync(cancellationToken)).Trim().Trim('"');
        if (!resp.IsSuccessStatusCode)
            throw new HttpRequestException($"HTTP {(int)resp.StatusCode} from {path}: {body}");
        return body;
    }

    private static (bool? Forced, string Summary) InterpretGetPlayerResponse(string body)
    {
        return body switch
        {
            "No"    => (false, "not on the forced list."),
            "Yes"   => (true,  "forced — has not yet logged in via the launcher."),
            "Using" => (false, "currently connected via the launcher (not forced)."),
            _ => TryParseHeartbeat(body)
        };

        static (bool?, string) TryParseHeartbeat(string body)
        {
            if (DateTime.TryParse(body, CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                    out var stamp))
            {
                var minutes = (int)(DateTime.UtcNow - stamp).TotalMinutes;
                var stale = minutes > 120;
                return (stale,
                    stale
                        ? $"forced — last heartbeat {minutes} min ago (stale, >120 min)."
                        : $"forced; launcher heartbeat seen {minutes} min ago (still fresh).");
            }
            return (null, $"unrecognised response from the apps service: '{body}'.");
        }
    }

    private (string? SteamId, string? Note) ResolveTarget(string message, string? slotPlayerName)
    {
        // Prefer an explicit steamid in the message — most accurate.
        var sidMatch = SteamIdRegex.Match(message);
        if (sidMatch.Success) return (sidMatch.Value, null);

        // Slot might already carry a steamid.
        if (!string.IsNullOrWhiteSpace(slotPlayerName))
        {
            var trimmed = slotPlayerName.Trim();
            if (LooksLikeSteamId(trimmed)) return (trimmed, null);
            var bySlot = ResolveNameToSteamId(trimmed);
            if (bySlot is not null) return (bySlot, null);
        }

        // Try to extract a name from the message.
        var nameCandidate = ExtractNameCandidate(message);
        if (!string.IsNullOrWhiteSpace(nameCandidate))
        {
            var byName = ResolveNameToSteamId(nameCandidate!);
            if (byName is not null) return (byName, null);
            return (null, $"I don't know a player called \"{nameCandidate}\" yet — give me their steamid (17 digits, starts with 7656).");
        }

        return (null, null);
    }

    private string? ResolveNameToSteamId(string name)
    {
        var roster = _playerStore.GetDisplayNamesBySteamId(5000);
        // Exact case-insensitive display-name match wins.
        var exact = roster.FirstOrDefault(kv => string.Equals(kv.Value, name, StringComparison.OrdinalIgnoreCase));
        if (!string.IsNullOrEmpty(exact.Key)) return exact.Key;
        // Otherwise look across alias history.
        foreach (var (sid, _) in roster)
        {
            var record = _playerStore.Get(sid);
            if (record is null) continue;
            if (record.Aliases.Any(a => string.Equals(a, name, StringComparison.OrdinalIgnoreCase)))
                return sid;
        }
        return null;
    }

    // Pulls a likely player name out of the message by stripping operation verbs and
    // common boilerplate. Crude but works well for short admin commands.
    private static string? ExtractNameCandidate(string message)
    {
        var text = message.Trim();
        text = Regex.Replace(text, @"^(please|could you|can you|hey|yo|hi|ok)\s+", string.Empty, RegexOptions.IgnoreCase);
        text = Regex.Replace(text,
            @"\b(force|unforce|deforce|add|remove|lift|stop|check|verify|is|are|status|on|the|forced?|list|player|from|to|of|please|launcher|use|using)\b",
            string.Empty, RegexOptions.IgnoreCase);
        text = Regex.Replace(text, @"[?!.,]+", " ");
        text = Regex.Replace(text, @"\s+", " ").Trim();
        if (string.IsNullOrWhiteSpace(text)) return null;
        // Take the longest remaining token group (handles names with internal punctuation poorly,
        // but the slot/steamid path covers the common cases).
        var token = text.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                        .OrderByDescending(t => t.Length)
                        .FirstOrDefault();
        return token;
    }

    private static bool LooksLikeSteamId(string s) =>
        s.Length == 17 && s.StartsWith("7656", StringComparison.Ordinal) && s.All(char.IsDigit);

    private static ForcedOp ClassifyOperation(string message)
    {
        // Order matters: "remove from forced" contains "force"; check remove first.
        if (RemovePhraseRegex.IsMatch(message)) return ForcedOp.Remove;
        if (QueryPhraseRegex.IsMatch(message))  return ForcedOp.Query;
        if (AddPhraseRegex.IsMatch(message))    return ForcedOp.Add;
        return ForcedOp.Query;
    }

    private enum ForcedOp { Query, Add, Remove }
}

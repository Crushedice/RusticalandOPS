using System.Text.RegularExpressions;
using RustOpsAgent.Core.Contracts;

namespace RustOpsAgent.Core.Interaction;

internal sealed record ScopeResolution(
    ServerScopeKind ScopeKind,
    IReadOnlyList<string> Servers,
    bool RequiresClarification);

internal static class ServerScopeResolver
{
    private static readonly Regex CollectivelyAllRegex = new(
        @"\b(all|every|each)\b|\ball\s+\d+\b|\ball\s+(one|two|three|four|five|six|seven|eight|nine|ten)\b|\ball\s+servers?\b|\ball\s+of\s+them\b|\bevery\s+server\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex CorrectionPrefixRegex = new(
        @"^\s*(no|nah|nope|wait|hold on|actually|sorry)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static ScopeResolution Resolve(
        string message,
        IReadOnlyList<string> knownServers,
        ConversationSelectionState state,
        ServerScopeKind requestedScopeKind,
        IReadOnlyList<string>? requestedServers,
        string? requestedServer,
        bool allowPluralDefaultAll,
        bool allowLastScopeFallback)
    {
        var canonicalKnownServers = Canonicalize(knownServers, knownServers);
        var loweredMessage = message.ToLowerInvariant();
        var fromMessage = MatchKnownServers(message, canonicalKnownServers);

        var requested = new List<string>();
        if (!string.IsNullOrWhiteSpace(requestedServer))
        {
            var match = MatchKnownServer(requestedServer, canonicalKnownServers);
            if (!string.IsNullOrWhiteSpace(match))
            {
                requested.Add(match!);
            }
            else
            {
                requested.Add(requestedServer.Trim());
            }
        }

        if (requestedServers is not null)
        {
            foreach (var candidate in requestedServers.Where(static item => !string.IsNullOrWhiteSpace(item)))
            {
                var match = MatchKnownServer(candidate, canonicalKnownServers);
                requested.Add(string.IsNullOrWhiteSpace(match) ? candidate.Trim() : match!);
            }
        }

        requested = Canonicalize(requested, canonicalKnownServers).ToList();
        var correctionFollowUp = IsCorrectionFollowUp(loweredMessage);
        var explicitAll = requestedScopeKind == ServerScopeKind.All || ContainsCollectiveAll(loweredMessage);

        if (explicitAll)
        {
            if (canonicalKnownServers.Count > 0)
            {
                return new ScopeResolution(ServerScopeKind.All, canonicalKnownServers, false);
            }

            if (state.LastResolvedServers.Count > 0)
            {
                var remembered = Canonicalize(state.LastResolvedServers, state.LastResolvedServers);
                return new ScopeResolution(
                    remembered.Count > 1 ? ServerScopeKind.All : ServerScopeKind.Single,
                    remembered,
                    remembered.Count == 0);
            }

            return new ScopeResolution(ServerScopeKind.All, Array.Empty<string>(), true);
        }

        if (fromMessage.Count > 1)
        {
            return new ScopeResolution(ServerScopeKind.Subset, fromMessage, false);
        }

        if (requested.Count > 1)
        {
            return new ScopeResolution(ServerScopeKind.Subset, requested, false);
        }

        if (fromMessage.Count == 1)
        {
            return new ScopeResolution(ServerScopeKind.Single, fromMessage, false);
        }

        if (requested.Count == 1)
        {
            return new ScopeResolution(ServerScopeKind.Single, requested, false);
        }

        if (allowPluralDefaultAll && LooksPluralOrCollective(loweredMessage) && canonicalKnownServers.Count > 0)
        {
            return new ScopeResolution(ServerScopeKind.All, canonicalKnownServers, false);
        }

        if (allowLastScopeFallback && state.PendingClarification is not null && correctionFollowUp)
        {
            var remembered = Canonicalize(state.LastResolvedServers, canonicalKnownServers.Count > 0 ? canonicalKnownServers : state.LastResolvedServers);
            if (remembered.Count > 1)
            {
                return new ScopeResolution(
                    state.LastScopeKind == ServerScopeKind.All ? ServerScopeKind.All : ServerScopeKind.Subset,
                    remembered,
                    false);
            }

            if (remembered.Count == 1)
            {
                return new ScopeResolution(ServerScopeKind.Single, remembered, false);
            }
        }

        if (allowLastScopeFallback && ShouldReuseLastScope(loweredMessage) && state.LastResolvedServers.Count > 0)
        {
            var remembered = Canonicalize(state.LastResolvedServers, canonicalKnownServers.Count > 0 ? canonicalKnownServers : state.LastResolvedServers);
            if (remembered.Count > 1)
            {
                var rememberedScope = state.LastScopeKind == ServerScopeKind.All
                    ? ServerScopeKind.All
                    : ServerScopeKind.Subset;
                return new ScopeResolution(rememberedScope, remembered, false);
            }

            if (remembered.Count == 1)
            {
                return new ScopeResolution(ServerScopeKind.Single, remembered, false);
            }
        }

        if (allowLastScopeFallback && ShouldUseLastServer(loweredMessage) && !string.IsNullOrWhiteSpace(state.LastServerName))
        {
            var remembered = MatchKnownServer(state.LastServerName, canonicalKnownServers);
            if (!string.IsNullOrWhiteSpace(remembered))
            {
                return new ScopeResolution(ServerScopeKind.Single, new[] { remembered! }, false);
            }
        }

        if (canonicalKnownServers.Count == 1)
        {
            return new ScopeResolution(ServerScopeKind.Single, canonicalKnownServers, false);
        }

        return new ScopeResolution(ServerScopeKind.Unspecified, Array.Empty<string>(), true);
    }

    public static IReadOnlyList<string> MatchKnownServers(string message, IReadOnlyList<string> knownServers)
    {
        if (knownServers.Count == 0 || string.IsNullOrWhiteSpace(message))
        {
            return Array.Empty<string>();
        }

        var normalizedMessage = NormalizeKey(message);
        var messageTokens = SplitTokens(message);

        return knownServers
            .Select(server => new
            {
                Server = server,
                Score = ScoreServerMatch(server, message, normalizedMessage, messageTokens)
            })
            .Where(item => item.Score >= 80)
            .OrderByDescending(item => item.Score)
            .ThenBy(item => item.Server, StringComparer.OrdinalIgnoreCase)
            .Select(item => item.Server)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public static string? MatchKnownServer(string? candidate, IReadOnlyList<string> knownServers)
    {
        if (string.IsNullOrWhiteSpace(candidate))
        {
            return null;
        }

        var trimmed = candidate.Trim();
        var exact = knownServers.FirstOrDefault(server => string.Equals(server, trimmed, StringComparison.OrdinalIgnoreCase));
        if (!string.IsNullOrWhiteSpace(exact))
        {
            return exact;
        }

        var normalizedCandidate = NormalizeKey(trimmed);
        var candidateTokens = SplitTokens(trimmed);

        return knownServers
            .Select(server => new
            {
                Server = server,
                Score = ScoreServerMatch(server, trimmed, normalizedCandidate, candidateTokens)
            })
            .Where(item => item.Score >= 60)
            .OrderByDescending(item => item.Score)
            .ThenBy(item => item.Server.Length)
            .Select(item => item.Server)
            .FirstOrDefault();
    }

    private static bool ContainsCollectiveAll(string loweredMessage) => CollectivelyAllRegex.IsMatch(loweredMessage);

    private static bool LooksPluralOrCollective(string loweredMessage)
    {
        if (ContainsCollectiveAll(loweredMessage))
        {
            return true;
        }

        return loweredMessage.Contains("servers", StringComparison.Ordinal) ||
               loweredMessage.Contains("both servers", StringComparison.Ordinal) ||
               loweredMessage.Contains("all of them", StringComparison.Ordinal) ||
               loweredMessage.Contains("them all", StringComparison.Ordinal);
    }

    private static bool IsCorrectionFollowUp(string loweredMessage) =>
        CorrectionPrefixRegex.IsMatch(loweredMessage) ||
        loweredMessage.Contains("i meant", StringComparison.Ordinal) ||
        loweredMessage.Contains("meant all", StringComparison.Ordinal);

    private static bool ShouldReuseLastScope(string loweredMessage) =>
        loweredMessage.Contains("again", StringComparison.Ordinal) ||
        loweredMessage.Contains("same", StringComparison.Ordinal) ||
        loweredMessage.Contains("those", StringComparison.Ordinal) ||
        loweredMessage.Contains("them", StringComparison.Ordinal) ||
        loweredMessage.Contains("as before", StringComparison.Ordinal);

    private static bool ShouldUseLastServer(string loweredMessage) =>
        loweredMessage.Contains("that one", StringComparison.Ordinal) ||
        loweredMessage.Contains("same server", StringComparison.Ordinal) ||
        loweredMessage.Contains("same one", StringComparison.Ordinal) ||
        loweredMessage.Contains("restart it", StringComparison.Ordinal) ||
        loweredMessage.Contains("stop it", StringComparison.Ordinal) ||
        loweredMessage.Contains("start it", StringComparison.Ordinal) ||
        loweredMessage.Contains("kill it", StringComparison.Ordinal) ||
        loweredMessage.Contains("update it", StringComparison.Ordinal) ||
        loweredMessage.Contains("check it", StringComparison.Ordinal);

    private static int ScoreServerMatch(
        string server,
        string candidate,
        string normalizedCandidate,
        IReadOnlyCollection<string> candidateTokens)
    {
        if (candidate.Contains(server, StringComparison.OrdinalIgnoreCase) ||
            server.Contains(candidate, StringComparison.OrdinalIgnoreCase))
        {
            return 100;
        }

        var normalizedServer = NormalizeKey(server);
        if (!string.IsNullOrWhiteSpace(normalizedCandidate) &&
            (normalizedCandidate.Contains(normalizedServer, StringComparison.OrdinalIgnoreCase) ||
             normalizedServer.Contains(normalizedCandidate, StringComparison.OrdinalIgnoreCase)))
        {
            return 85;
        }

        var serverTokens = SplitTokens(server);
        var shared = serverTokens.Intersect(candidateTokens, StringComparer.OrdinalIgnoreCase).Count();
        return shared switch
        {
            >= 2 => 80,
            1 => 60,
            _ => 0
        };
    }

    private static string NormalizeKey(string value) =>
        new string(value.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();

    private static IReadOnlyCollection<string> SplitTokens(string value) =>
        value
            .Split(new[] { '-', '_', '.', ' ', ',', ':', ';', '(', ')', '?', '!' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(token => token.Length >= 3)
            .Select(token => token.ToLowerInvariant())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

    private static IReadOnlyList<string> Canonicalize(IEnumerable<string> input, IReadOnlyList<string> knownServers)
    {
        var output = new List<string>();
        foreach (var raw in input.Where(static item => !string.IsNullOrWhiteSpace(item)))
        {
            var trimmed = raw.Trim();
            var match = knownServers.FirstOrDefault(server => string.Equals(server, trimmed, StringComparison.OrdinalIgnoreCase));
            output.Add(match ?? trimmed);
        }

        return output
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(item => item, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}

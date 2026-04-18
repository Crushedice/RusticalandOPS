using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Sentry;
using SteamKit2;

var configPath = args.FirstOrDefault() ?? Path.Combine(AppContext.BaseDirectory, "botsettings.json");
RustOpsEnv.LoadFromDefaultLocations(configPath);
using var sentry = RustOpsSentry.Initialize("opssteambot");

try
{
if (!File.Exists(configPath))
{
    Console.Error.WriteLine($"Config file not found: {configPath}");
    Console.Error.WriteLine("Copy botsettings.example.json to botsettings.json and fill in your values.");
    return 1;
}

var config = AppConfig.Load(configPath, configPath);
Console.WriteLine($"Config loaded from {Path.GetFullPath(configPath)}");
Console.WriteLine($"API base URL: {config.Api.BaseUrl}");
Console.WriteLine($"Agent state path: {config.Agent.StatePath}");
Console.WriteLine($"Feedback inbox: {config.Agent.FeedbackInboxPath}");
Console.WriteLine($"Decision inbox: {config.Agent.DecisionInboxPath}");
Console.WriteLine($"Chat inbox: {config.Agent.ChatInboxPath}");
Console.WriteLine($"Message outbox: {config.Agent.MessageOutboxPath}");
Console.WriteLine($"Sent outbox: {config.Agent.SentOutboxPath}");
Console.WriteLine($"Dead-letter outbox: {config.Agent.DeadLetterPath}");
Console.WriteLine($"Outbox max retries: {config.Agent.OutboxMaxRetries}");
Console.WriteLine($"Steam admins: {string.Join(", ", config.Steam.AdminSteamIds)}");
using var api = new RustMgrApiClient(config.Api);
var bot = new OpsSteamBot(config, api);
await bot.RunAsync();
return 0;
}
catch (Exception ex)
{
    Console.Error.WriteLine(ex);
    SentrySdk.CaptureException(ex);
    return 1;
}
finally
{
    await RustOpsSentry.FlushAsync();
}

internal sealed class OpsSteamBot
{
    private readonly AppConfig _config;
    private readonly RustMgrApiClient _api;
    private readonly CallbackManager _manager;
    private readonly SteamClient _client;
    private readonly SteamUser _user;
    private readonly SteamFriends _friends;
    private readonly HashSet<ulong> _admins;
    private readonly Dictionary<string, int> _outboxFailureCounts = new(StringComparer.OrdinalIgnoreCase);
    private string? _authCode;
    private bool _isRunning;
    private DateTime _lastOutboxPollUtc = DateTime.MinValue;

    public OpsSteamBot(AppConfig config, RustMgrApiClient api)
    {
        _config = config;
        _api = api;
        _admins = new HashSet<ulong>(config.Steam.AdminSteamIds);
        _client = new SteamClient();
        _manager = new CallbackManager(_client);
        _user = _client.GetHandler<SteamUser>() ?? throw new InvalidOperationException("SteamUser handler not available.");
        _friends = _client.GetHandler<SteamFriends>() ?? throw new InvalidOperationException("SteamFriends handler not available.");

        Directory.CreateDirectory(_config.Agent.FeedbackInboxPath);
        Directory.CreateDirectory(_config.Agent.DecisionInboxPath);
        Directory.CreateDirectory(_config.Agent.ChatInboxPath);
        Directory.CreateDirectory(_config.Agent.MessageOutboxPath);
        Directory.CreateDirectory(_config.Agent.SentOutboxPath);
        Directory.CreateDirectory(_config.Agent.DeadLetterPath);

        _manager.Subscribe<SteamClient.ConnectedCallback>(OnConnected);
        _manager.Subscribe<SteamClient.DisconnectedCallback>(OnDisconnected);
        _manager.Subscribe<SteamUser.LoggedOnCallback>(OnLoggedOn);
        _manager.Subscribe<SteamUser.LoggedOffCallback>(OnLoggedOff);
        _manager.Subscribe<SteamUser.AccountInfoCallback>(OnAccountInfo);
        _manager.Subscribe<SteamFriends.FriendMsgCallback>(OnFriendMessage);
    }

    public async Task RunAsync()
    {
        _isRunning = true;
        Console.WriteLine("Connecting to Steam...");
        RustOpsSentry.AddBreadcrumb("Connecting to Steam.", "steam.connection");
        _client.Connect();

        var outboxLoop = Task.Run(OutboxLoopAsync);

        while (_isRunning)
        {
            _manager.RunWaitCallbacks(TimeSpan.FromSeconds(1));
            await Task.Yield();
        }

        await outboxLoop;
    }

    private async Task OutboxLoopAsync()
    {
        while (_isRunning)
        {
            try
            {
                await PollOutboxAsync();
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Outbox loop failed: {ex.Message}");
                SentrySdk.CaptureException(ex);
            }

            await Task.Delay(250);
        }
    }

    private void OnConnected(SteamClient.ConnectedCallback callback)
    {
        Console.WriteLine("Connected to Steam. Logging in...");

        _user.LogOn(new SteamUser.LogOnDetails
        {
            Username = _config.Steam.Username,
            Password = _config.Steam.Password,
            AuthCode = _authCode
        });
    }

    private void OnDisconnected(SteamClient.DisconnectedCallback callback)
    {
        Console.WriteLine("Disconnected from Steam. Reconnecting in 5 seconds...");
        RustOpsSentry.AddBreadcrumb("Disconnected from Steam. Scheduling reconnect.", "steam.connection");
        Task.Delay(TimeSpan.FromSeconds(5)).ContinueWith(_ => _client.Connect());
    }

    private void OnLoggedOn(SteamUser.LoggedOnCallback callback)
    {
        if (callback.Result == EResult.AccountLogonDenied ||
            callback.Result == EResult.InvalidLoginAuthCode)
        {
            Console.Write("Steam Guard code: ");
            _authCode = Console.ReadLine()?.Trim();
            _client.Disconnect();
            return;
        }

        if (callback.Result == EResult.AccountLoginDeniedNeedTwoFactor)
        {
            Console.Error.WriteLine("This bot currently expects a Steam Guard auth code flow.");
            Console.Error.WriteLine("If you use mobile 2FA, extend the bot with a shared-secret TOTP step.");
            _isRunning = false;
            return;
        }

        if (callback.Result != EResult.OK)
        {
            Console.Error.WriteLine($"Steam login failed: {callback.Result}");
            SentrySdk.CaptureMessage($"Steam login failed: {callback.Result}");
            return;
        }

        Console.WriteLine("Logged on.");
        RustOpsSentry.AddBreadcrumb("Logged on to Steam.", "steam.connection");
        _friends.SetPersonaState(EPersonaState.Online);
        if (!string.IsNullOrWhiteSpace(_config.Steam.PersonaName))
            _friends.SetPersonaName(_config.Steam.PersonaName);
    }

    private void OnLoggedOff(SteamUser.LoggedOffCallback callback)
    {
        Console.WriteLine($"Logged off: {callback.Result}");
    }

    private void OnAccountInfo(SteamUser.AccountInfoCallback callback)
    {
        _friends.SetPersonaState(EPersonaState.Online);
    }

    private async void OnFriendMessage(SteamFriends.FriendMsgCallback callback)
    {
        if (callback.EntryType != EChatEntryType.ChatMsg)
            return;

        var senderId = callback.Sender.ConvertToUInt64();
        var text = (callback.Message ?? string.Empty).Trim();

        if (!_admins.Contains(senderId))
        {
            Console.WriteLine($"Rejected message from non-admin {senderId}: {text}");
            _friends.SendChatMessage(callback.Sender, EChatEntryType.ChatMsg, "Unauthorized.");
            return;
        }

        Console.WriteLine($"[{senderId}] {text}");
        RustOpsSentry.AddBreadcrumb($"Steam admin message from {senderId}.", "steam.chat");

        try
        {
            var response = await HandleIncomingMessageAsync(senderId, text);
            foreach (var chunk in ChunkMessage(response))
            {
                _friends.SendChatMessage(callback.Sender, EChatEntryType.ChatMsg, chunk);
                await Task.Delay(350);
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex);
            SentrySdk.CaptureException(ex);
            var userMessage = ex is ApiClientException apiEx
                ? apiEx.Message
                : $"Error: {ex.Message}";
            _friends.SendChatMessage(callback.Sender, EChatEntryType.ChatMsg, userMessage);
        }
    }

    private async Task PollOutboxAsync()
    {
        var now = DateTime.UtcNow;
        if (now - _lastOutboxPollUtc < TimeSpan.FromSeconds(_config.Agent.OutboxPollSeconds))
            return;

        _lastOutboxPollUtc = now;

        var allFiles = Directory.GetFiles(_config.Agent.MessageOutboxPath, "*.json");
        var prioritizedFiles = allFiles
            .Where(IsChatReplyOutboxFile)
            .OrderBy(Path.GetFileName)
            .Concat(allFiles
                .Where(path => !IsChatReplyOutboxFile(path))
                .OrderBy(Path.GetFileName)
                .Take(2))
            .ToList();

        foreach (var path in prioritizedFiles)
        {
            try
            {
                var json = JsonSerializer.Deserialize<AdapterMessage>(File.ReadAllText(path), JsonOptions.Default);
                if (json is null || string.IsNullOrWhiteSpace(json.Message))
                {
                    ArchiveOutboxFile(path);
                    continue;
                }

                var targetAdmins = ResolveTargets(json);
                foreach (var adminId in targetAdmins)
                {
                    foreach (var chunk in ChunkMessage(json.Message))
                    {
                        _friends.SendChatMessage(new SteamID(adminId), EChatEntryType.ChatMsg, chunk);
                        await Task.Delay(350);
                    }
                }

                ArchiveOutboxFile(path);
                _outboxFailureCounts.Remove(path);
            }
            catch (Exception ex)
            {
                HandleOutboxFailure(path, ex);
            }
        }
    }

    private static bool IsChatReplyOutboxFile(string path) =>
        Path.GetFileName(path).Contains("-chat-reply-", StringComparison.OrdinalIgnoreCase);

    private IEnumerable<ulong> ResolveTargets(AdapterMessage message)
    {
        if (!string.IsNullOrWhiteSpace(message.TargetAdminId) &&
            ulong.TryParse(message.TargetAdminId, out var targetAdminId) &&
            _admins.Contains(targetAdminId))
        {
            return new[] { targetAdminId };
        }

        return _admins;
    }

    private void ArchiveOutboxFile(string path)
    {
        var target = Path.Combine(_config.Agent.SentOutboxPath, Path.GetFileName(path));
        MoveFileWithOverwrite(path, target);
    }

    private void HandleOutboxFailure(string path, Exception ex)
    {
        Console.Error.WriteLine($"Failed to process outbox message '{path}': {ex.Message}");
        SentrySdk.CaptureException(ex);

        var currentCount = _outboxFailureCounts.TryGetValue(path, out var count) ? count : 0;
        currentCount++;
        _outboxFailureCounts[path] = currentCount;

        if (currentCount < _config.Agent.OutboxMaxRetries)
            return;

        try
        {
            var fileName = Path.GetFileName(path);
            var deadTarget = Path.Combine(_config.Agent.DeadLetterPath, fileName);
            MoveFileWithOverwrite(path, deadTarget);
            var reasonPath = deadTarget + ".error.txt";
            File.WriteAllText(reasonPath, $"Moved to dead-letter after {currentCount} failures at {DateTime.UtcNow:O}\n{ex}");
            _outboxFailureCounts.Remove(path);
        }
        catch (Exception moveEx)
        {
            Console.Error.WriteLine($"Failed to move outbox file '{path}' to dead-letter: {moveEx.Message}");
            SentrySdk.CaptureException(moveEx);
        }
    }

    private static void MoveFileWithOverwrite(string sourcePath, string targetPath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
        try
        {
            File.Move(sourcePath, targetPath, true);
        }
        catch (IOException)
        {
            File.Copy(sourcePath, targetPath, true);
            File.Delete(sourcePath);
        }
    }

    private async Task<string> HandleIncomingMessageAsync(ulong senderId, string input)
    {
        var parts = SplitCommand(input);
        if (parts.Count == 0)
            return "Empty command.";

        var command = parts[0].ToLowerInvariant();
        return command switch
        {
            "help" => GetHelpText(),
            "ping" => "pong",
            "approve" => HandleDecisionCommand(senderId, parts, "approve"),
            "reject" => HandleDecisionCommand(senderId, parts, "reject"),
            "feedback" => HandleFeedbackCommand(senderId, parts),
            _ => QueueChatRequest(senderId, input)
        };
    }

    private string HandleDecisionCommand(ulong senderId, List<string> parts, string decision)
    {
        if (parts.Count < 2)
            return $"Usage: {decision} <actionId> [reason]";

        var item = new DecisionInboxItem
        {
            AdminId = senderId.ToString(),
            ActionId = parts[1],
            Decision = decision,
            Note = parts.Count >= 3 ? string.Join(' ', parts.Skip(2)) : null
        };

        WriteInboxFile(_config.Agent.DecisionInboxPath, "decision", item);
        return $"{decision} queued for action {parts[1]}.";
    }

    private string HandleFeedbackCommand(ulong senderId, List<string> parts)
    {
        if (parts.Count < 3)
            return "Usage: feedback <actionId> <good|bad|note> [text]";

        var actionId = parts[1];
        var verdict = parts[2];
        var note = parts.Count >= 4 ? string.Join(' ', parts.Skip(3)) : null;
        var serverName = TryFindServerNameForAction(actionId);

        var item = new FeedbackInboxItem
        {
            AdminId = senderId.ToString(),
            ActionId = actionId,
            ServerName = serverName,
            Verdict = verdict,
            Note = note,
            Preference = note
        };

        WriteInboxFile(_config.Agent.FeedbackInboxPath, "feedback", item);
        return $"feedback queued for action {actionId}.";
    }

    private string? TryFindServerNameForAction(string actionId)
    {
        using var state = LoadAgentState();
        if (state is null)
            return null;

        foreach (var collectionName in new[] { "pendingActions", "actionHistory" })
        {
            if (!state.RootElement.TryGetProperty(collectionName, out var collection) || collection.ValueKind != JsonValueKind.Array)
                continue;

            var match = collection.EnumerateArray().FirstOrDefault(a =>
                a.TryGetProperty(collectionName == "actionHistory" ? "actionId" : "id", out var idNode) &&
                string.Equals(idNode.GetString(), actionId, StringComparison.OrdinalIgnoreCase));

            if (match.ValueKind == JsonValueKind.Object &&
                match.TryGetProperty("serverName", out var serverNode) &&
                serverNode.ValueKind == JsonValueKind.String)
            {
                return serverNode.GetString();
            }
        }

        return null;
    }

    private JsonDocument? LoadAgentState()
    {
        if (!File.Exists(_config.Agent.StatePath))
            return null;

        return JsonDocument.Parse(File.ReadAllText(_config.Agent.StatePath));
    }

    private void WriteInboxFile<T>(string inboxPath, string prefix, T payload)
    {
        var fileName = $"{DateTime.UtcNow:yyyyMMddHHmmssfff}-{prefix}-{Guid.NewGuid():N}.json";
        var path = Path.Combine(inboxPath, fileName);
        File.WriteAllText(path, JsonSerializer.Serialize(payload, JsonOptions.Default));
    }

    private string QueueChatRequest(ulong senderId, string input)
    {
        var item = new ChatInboxItem
        {
            RequestId = Guid.NewGuid().ToString("N"),
            AdminId = senderId.ToString(),
            Message = input.Trim(),
            Channel = "steam"
        };

        WriteInboxFile(_config.Agent.ChatInboxPath, "chat", item);
        return "Request received. I'll reply shortly.";
    }

    private string GetHelpText()
    {
        return string.Join('\n',
            "Natural language is enabled.",
            "Examples:",
            "what servers are running",
            "status vanilla",
            "health modded",
            "restart onegrid",
            "what happened recently",
            "show pending actions",
            "",
            "Direct control commands:",
            "help",
            "ping",
            "approve <actionId> [reason]",
            "reject <actionId> [reason]",
            "feedback <actionId> <good|bad|note> [text]");
    }

    private static List<string> SplitCommand(string input)
    {
        var result = new List<string>();
        var current = new StringBuilder();
        var inQuotes = false;

        foreach (var ch in input)
        {
            if (ch == '"')
            {
                inQuotes = !inQuotes;
                continue;
            }

            if (char.IsWhiteSpace(ch) && !inQuotes)
            {
                if (current.Length > 0)
                {
                    result.Add(current.ToString());
                    current.Clear();
                }

                continue;
            }

            current.Append(ch);
        }

        if (current.Length > 0)
            result.Add(current.ToString());

        return result;
    }

    private static IEnumerable<string> ChunkMessage(string text)
    {
        const int limit = 350;
        var normalized = text.Replace("\r", string.Empty);
        var lines = normalized.Split('\n');
        var buffer = new StringBuilder();

        foreach (var line in lines)
        {
            var candidate = buffer.Length == 0 ? line : $"{buffer}\n{line}";
            if (candidate.Length <= limit)
            {
                buffer.Clear();
                buffer.Append(candidate);
                continue;
            }

            if (buffer.Length > 0)
            {
                yield return buffer.ToString();
                buffer.Clear();
            }

            if (line.Length <= limit)
            {
                buffer.Append(line);
                continue;
            }

            for (var i = 0; i < line.Length; i += limit)
                yield return line.Substring(i, Math.Min(limit, line.Length - i));
        }

        if (buffer.Length > 0)
            yield return buffer.ToString();
    }

    private static string TrimSingleLine(string input, int maxLength)
    {
        var singleLine = input.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return singleLine.Length <= maxLength
            ? singleLine
            : $"{singleLine[..(maxLength - 3)]}...";
    }

    private static string Encode(string value) => Uri.EscapeDataString(value);
}

internal sealed class RustMgrApiClient : IDisposable
{
    private readonly HttpClient _http;

    public RustMgrApiClient(ApiSettings settings)
    {
        _http = new HttpClient
        {
            BaseAddress = new Uri(settings.BaseUrl.TrimEnd('/'))
        };
        _http.DefaultRequestHeaders.Add("X-Api-Key", settings.ApiKey);
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
    }

    public async Task<JsonDocument> GetJsonAsync(string path)
    {
        using var response = await _http.GetAsync(path);
        return await ReadJsonAsync(response);
    }

    private static async Task<JsonDocument> ReadJsonAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode)
            throw new ApiClientException(response.StatusCode, FormatError(response.StatusCode, body));

        return JsonDocument.Parse(string.IsNullOrWhiteSpace(body) ? "{}" : body);
    }

    private static string FormatError(HttpStatusCode statusCode, string body)
    {
        if (!string.IsNullOrWhiteSpace(body))
        {
            try
            {
                using var json = JsonDocument.Parse(body);
                if (json.RootElement.TryGetProperty("message", out var messageNode))
                    return $"{(int)statusCode} {statusCode}: {messageNode.GetString()}";
            }
            catch
            {
            }
        }

        return $"{(int)statusCode} {statusCode}";
    }

    public void Dispose() => _http.Dispose();
}

internal sealed class ApiClientException : Exception
{
    public ApiClientException(HttpStatusCode statusCode, string message) : base(message)
    {
        StatusCode = statusCode;
    }

    public HttpStatusCode StatusCode { get; }
}

internal sealed class AppConfig
{
    public required SteamSettings Steam { get; init; }
    public required ApiSettings Api { get; init; }
    public required AgentPaths Agent { get; init; }

    public static AppConfig Load(string path, string sourcePath)
    {
        var config = JsonSerializer.Deserialize<AppConfig>(
            File.ReadAllText(path),
            new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
                ReadCommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true
            })
            ?? throw new InvalidOperationException("Failed to parse config.");

        ApplyEnvironmentOverrides(config);

        var baseDir = Path.GetDirectoryName(Path.GetFullPath(sourcePath))!;
        config.Agent.Normalize(baseDir);
        ValidateResolvedConfig(config);
        return config;
    }

    private static void ApplyEnvironmentOverrides(AppConfig config)
    {
        config.Steam.Username = RustOpsEnv.FirstNonEmptyEnvironment("RUSTOPS_STEAM_USERNAME")
            ?? RustOpsEnv.ResolvePlaceholders(config.Steam.Username);
        config.Steam.Password = RustOpsEnv.FirstNonEmptyEnvironment("RUSTOPS_STEAM_PASSWORD")
            ?? RustOpsEnv.ResolvePlaceholders(config.Steam.Password);
        config.Steam.PersonaName = RustOpsEnv.FirstNonEmptyEnvironment("RUSTOPS_STEAM_PERSONA_NAME")
            ?? RustOpsEnv.ResolvePlaceholders(config.Steam.PersonaName);

        var adminIds = RustOpsEnv.GetCsvValues("RUSTOPS_STEAM_ADMIN_IDS")
            .Select(value => ulong.TryParse(value, out var parsed) ? parsed : (ulong?)null)
            .Where(value => value.HasValue)
            .Select(value => value!.Value)
            .ToList();
        if (adminIds.Count > 0)
            config.Steam.AdminSteamIds = adminIds;

        config.Api.BaseUrl = RustOpsEnv.FirstNonEmptyEnvironment("RUSTOPS_API_BASE_URL")
            ?? RustOpsEnv.ResolvePlaceholders(config.Api.BaseUrl);
        config.Api.ApiKey = RustOpsEnv.FirstNonEmptyEnvironment("RUSTMGR_API_KEY", "RUSTOPS_API_KEY")
            ?? RustOpsEnv.ResolvePlaceholders(config.Api.ApiKey);

        config.Agent.StatePath = RustOpsEnv.FirstNonEmptyEnvironment("RUSTOPS_AGENT_STATE_PATH")
            ?? config.Agent.StatePath;
        config.Agent.FeedbackInboxPath = RustOpsEnv.FirstNonEmptyEnvironment("RUSTOPS_FEEDBACK_INBOX_PATH")
            ?? config.Agent.FeedbackInboxPath;
        config.Agent.DecisionInboxPath = RustOpsEnv.FirstNonEmptyEnvironment("RUSTOPS_DECISION_INBOX_PATH")
            ?? config.Agent.DecisionInboxPath;
        config.Agent.ChatInboxPath = RustOpsEnv.FirstNonEmptyEnvironment("RUSTOPS_CHAT_INBOX_PATH")
            ?? config.Agent.ChatInboxPath;
        config.Agent.MessageOutboxPath = RustOpsEnv.FirstNonEmptyEnvironment("RUSTOPS_MESSAGE_OUTBOX_PATH")
            ?? config.Agent.MessageOutboxPath;
        config.Agent.SentOutboxPath = RustOpsEnv.FirstNonEmptyEnvironment("RUSTOPS_MESSAGE_OUTBOX_SENT_PATH")
            ?? config.Agent.SentOutboxPath;
        config.Agent.DeadLetterPath = RustOpsEnv.FirstNonEmptyEnvironment("RUSTOPS_STEAM_DEADLETTER_PATH")
            ?? config.Agent.DeadLetterPath;
        config.Agent.OutboxPollSeconds = RustOpsEnv.GetInt32("RUSTOPS_STEAM_OUTBOX_POLL_SECONDS", config.Agent.OutboxPollSeconds);
        config.Agent.OutboxMaxRetries = RustOpsEnv.GetInt32("RUSTOPS_STEAM_OUTBOX_MAX_RETRIES", config.Agent.OutboxMaxRetries);
    }

    private static void ValidateResolvedConfig(AppConfig config)
    {
        var unresolved = new List<string>();

        Check("steam.username", config.Steam.Username);
        Check("steam.password", config.Steam.Password);
        Check("api.baseUrl", config.Api.BaseUrl);
        Check("api.apiKey", config.Api.ApiKey);
        Check("agent.statePath", config.Agent.StatePath);
        Check("agent.feedbackInboxPath", config.Agent.FeedbackInboxPath);
        Check("agent.decisionInboxPath", config.Agent.DecisionInboxPath);
        Check("agent.chatInboxPath", config.Agent.ChatInboxPath);
        Check("agent.messageOutboxPath", config.Agent.MessageOutboxPath);
        Check("agent.sentOutboxPath", config.Agent.SentOutboxPath);
        Check("agent.deadLetterPath", config.Agent.DeadLetterPath);

        if (unresolved.Count > 0)
        {
            throw new InvalidOperationException(
                "Config contains unresolved placeholders. Missing env-backed values: " +
                string.Join(", ", unresolved));
        }

        void Check(string name, string? value)
        {
            if (string.IsNullOrWhiteSpace(value) || RustOpsEnv.HasUnresolvedPlaceholder(value))
                unresolved.Add(name);
        }
    }
}

internal sealed class SteamSettings
{
    public string Username { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
    public string PersonaName { get; set; } = "Rusticaland Ops";
    public List<ulong> AdminSteamIds { get; set; } = new();
}

internal sealed class ApiSettings
{
    public string BaseUrl { get; set; } = "http://127.0.0.1:2077";
    public string ApiKey { get; set; } = "changeme";
}

internal sealed class AgentPaths
{
    public string StatePath { get; set; } = "..\\..\\agent\\RustOpsAgent\\data\\agent-state.json";
    public string FeedbackInboxPath { get; set; } = "..\\..\\agent\\RustOpsAgent\\data\\feedback-inbox";
    public string DecisionInboxPath { get; set; } = "..\\..\\agent\\RustOpsAgent\\data\\decision-inbox";
    public string ChatInboxPath { get; set; } = "..\\..\\agent\\RustOpsAgent\\data\\chat-inbox";
    public string MessageOutboxPath { get; set; } = "..\\..\\agent\\RustOpsAgent\\data\\message-outbox";
    public string SentOutboxPath { get; set; } = "..\\..\\agent\\RustOpsAgent\\data\\message-outbox-sent";
    public string DeadLetterPath { get; set; } = "..\\..\\agent\\RustOpsAgent\\data\\message-outbox-deadletter";
    public int OutboxPollSeconds { get; set; } = 3;
    public int OutboxMaxRetries { get; set; } = 5;

    public void Normalize(string baseDir)
    {
        StatePath = NormalizePath(baseDir, RustOpsEnv.ResolvePlaceholders(StatePath));
        FeedbackInboxPath = NormalizePath(baseDir, RustOpsEnv.ResolvePlaceholders(FeedbackInboxPath));
        DecisionInboxPath = NormalizePath(baseDir, RustOpsEnv.ResolvePlaceholders(DecisionInboxPath));
        ChatInboxPath = NormalizePath(baseDir, RustOpsEnv.ResolvePlaceholders(ChatInboxPath));
        MessageOutboxPath = NormalizePath(baseDir, RustOpsEnv.ResolvePlaceholders(MessageOutboxPath));
        SentOutboxPath = NormalizePath(baseDir, RustOpsEnv.ResolvePlaceholders(SentOutboxPath));
        DeadLetterPath = NormalizePath(baseDir, RustOpsEnv.ResolvePlaceholders(DeadLetterPath));
        OutboxMaxRetries = Math.Max(1, OutboxMaxRetries);
    }

    private static string NormalizePath(string baseDir, string path)
    {
        var normalized = RustOpsEnv.NormalizePath(path);
        return Path.IsPathRooted(normalized)
            ? normalized
            : Path.GetFullPath(Path.Combine(baseDir, normalized));
    }
}

internal sealed class DecisionInboxItem
{
    public string? AdminId { get; set; }
    public string? ActionId { get; set; }
    public string? Decision { get; set; }
    public string? Note { get; set; }
}

internal sealed class FeedbackInboxItem
{
    public string? AdminId { get; set; }
    public string? ServerName { get; set; }
    public string? ActionId { get; set; }
    public string? Verdict { get; set; }
    public string? Note { get; set; }
    public string? Preference { get; set; }
}

internal sealed class ChatInboxItem
{
    public string? RequestId { get; set; }
    public string? AdminId { get; set; }
    public string? Message { get; set; }
    public string? Channel { get; set; }
}

internal sealed class AdapterMessage
{
    public DateTime CreatedAtUtc { get; set; }
    public string Kind { get; set; } = string.Empty;
    public string Audience { get; set; } = "admins";
    public string? TargetAdminId { get; set; }
    public string ServerName { get; set; } = string.Empty;
    public string? ActionId { get; set; }
    public string Message { get; set; } = string.Empty;
}

internal static class JsonOptions
{
    public static readonly JsonSerializerOptions Default = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };
}

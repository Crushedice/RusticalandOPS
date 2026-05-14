using System.Text;
using RustOpsAgent.Core.Contracts;
using RustOpsAgent.Infrastructure.Memory;

namespace RustOpsAgent.Domains.Rust;

// Handles two intents:
//   schedule_task        — create a deferred/recurring task (rust.schedule.task)
//   schedule_management  — list, cancel, pause, resume scheduled tasks (rust.schedule.management)
//
// Persistence lives in NeoCortexStore (scheduler/tasks.json). The agent runtime's main loop
// invokes ScheduleFirer.TickAsync to fire tasks whose NextFireAtUtc has elapsed.
internal sealed class RustScheduleToolHandler : IToolHandler
{
    private readonly NeoCortexStore _neoCortex;

    public RustScheduleToolHandler(NeoCortexStore neoCortex)
    {
        _neoCortex = neoCortex;
    }

    public string Name => "rust.schedule.task";

    public IReadOnlyCollection<AdminIntentType> EligibleIntents => new[]
    {
        AdminIntentType.ScheduleTask,
        AdminIntentType.ScheduleManagement
    };

    public Task<ToolExecutionResult> ExecuteAsync(ToolExecutionContext context, CancellationToken cancellationToken)
    {
        if (context.Route.Intent == AdminIntentType.ScheduleManagement)
            return Task.FromResult(HandleManagement(context));

        return Task.FromResult(HandleCreate(context));
    }

    private ToolExecutionResult HandleCreate(ToolExecutionContext context)
    {
        var route = context.Route;
        var spec = route.Slots.Schedule;
        if (spec is null)
        {
            return new ToolExecutionResult(
                false,
                "Couldn't parse the schedule. Try \"every Friday at 04:00\" or \"in 30 minutes\".",
                ErrorCode: "schedule_missing_spec");
        }

        var steps = route.Steps;
        if (steps is null || steps.Count == 0)
        {
            // If no explicit steps but the parent route looks actionable, store the route itself
            // as a single step (e.g., "in 30 minutes restart cotton" might come without explicit steps).
            if (route.Intent == AdminIntentType.ScheduleTask &&
                route.Slots.CommandText is not null &&
                !string.IsNullOrWhiteSpace(route.Slots.ServerName))
            {
                steps = new[]
                {
                    new AdminIntentStep(
                        AdminIntentType.ServerControl,
                        route.Slots with { Schedule = null },
                        "rust.server.control")
                };
            }
            else
            {
                return new ToolExecutionResult(
                    false,
                    "Couldn't determine what to do at the scheduled time. Spell out the action (e.g. \"... wipe with random map seed\").",
                    ErrorCode: "schedule_missing_steps");
            }
        }

        var nextFire = ComputeNextFire(spec, DateTime.UtcNow);
        if (nextFire is null)
        {
            return new ToolExecutionResult(
                false,
                $"Schedule spec is incomplete (cadence={spec.Cadence}). Provide a day-of-week + time, or an interval.",
                ErrorCode: "schedule_unresolvable");
        }

        var task = new ScheduledTask
        {
            AdminId = context.AdminId,
            Description = spec.Description ?? DescribeRoute(route),
            OriginalMessage = context.Message,
            Cadence = spec.Cadence,
            DayOfWeek = spec.DayOfWeek,
            TimeOfDay = spec.TimeOfDay,
            IntervalMinutes = spec.IntervalMinutes,
            RandomizeSeed = spec.RandomizeSeed,
            NextFireAtUtc = nextFire,
            Steps = steps.Select(s => new ScheduledStep
            {
                Intent = s.Intent.ToString(),
                TargetRef = s.TargetRef,
                ServerName = s.Slots.ServerName,
                CommandText = s.Slots.CommandText,
                ConfigKey = s.Slots.ConfigKey,
                ConfigValue = s.Slots.ConfigValue
            }).ToList()
        };

        var state = _neoCortex.LoadScheduledTasks();
        state.Tasks.Add(task);
        _neoCortex.SaveScheduledTasks(state);

        var summary = new StringBuilder();
        summary.AppendLine($"Scheduled: {task.Description}");
        summary.AppendLine($"  id: {task.Id[..8]}  cadence: {task.Cadence}{(task.DayOfWeek is null ? "" : "/" + task.DayOfWeek)}{(task.TimeOfDay is null ? "" : "@" + task.TimeOfDay)}{(task.IntervalMinutes is null ? "" : $" every {task.IntervalMinutes}m")}");
        summary.AppendLine($"  next fire: {task.NextFireAtUtc:yyyy-MM-dd HH:mm} UTC");
        summary.AppendLine($"  steps ({task.Steps.Count}):");
        foreach (var s in task.Steps)
            summary.AppendLine($"    - {s.Intent} {s.ServerName ?? "*"} {s.CommandText ?? s.ConfigKey ?? string.Empty}");

        Console.WriteLine($"[schedule] Created task {task.Id[..8]} for {context.AdminId}: {task.Description} (next={task.NextFireAtUtc:O})");

        return new ToolExecutionResult(
            true,
            summary.ToString().TrimEnd(),
            SelectedServer: task.Steps.Select(s => s.ServerName).FirstOrDefault(s => !string.IsNullOrWhiteSpace(s)),
            MutatedState: true,
            Payload: task);
    }

    private ToolExecutionResult HandleManagement(ToolExecutionContext context)
    {
        var raw = (context.Route.Slots.CommandText ?? context.Message).Trim();
        var lowered = raw.ToLowerInvariant();
        var state = _neoCortex.LoadScheduledTasks();

        if (lowered.StartsWith("list") || lowered.StartsWith("show") || string.IsNullOrEmpty(lowered))
        {
            var live = state.Tasks.Where(t => !t.Completed).ToList();
            if (live.Count == 0)
                return new ToolExecutionResult(true, "No scheduled tasks.", MutatedState: false);

            var sb = new StringBuilder($"Scheduled tasks ({live.Count}):\n");
            foreach (var t in live.OrderBy(t => t.NextFireAtUtc))
            {
                var status = t.Paused ? "PAUSED" : "active";
                sb.AppendLine($"  [{t.Id[..8]}] {status} — {t.Description}");
                sb.AppendLine($"      next: {t.NextFireAtUtc:yyyy-MM-dd HH:mm} UTC  fired: {t.FireCount}x  cadence: {t.Cadence}");
            }
            return new ToolExecutionResult(true, sb.ToString().TrimEnd(), MutatedState: false);
        }

        // <verb> <id-or-keyword>
        string verb;
        string target;
        var spaceIdx = lowered.IndexOf(' ');
        if (spaceIdx < 0)
        {
            verb = lowered;
            target = string.Empty;
        }
        else
        {
            verb = lowered[..spaceIdx];
            target = raw[(spaceIdx + 1)..].Trim();
        }

        var match = FindTask(state.Tasks, target);
        if (match is null)
            return new ToolExecutionResult(false, $"No scheduled task matches \"{target}\". Use \"list scheduled tasks\".", ErrorCode: "schedule_no_match");

        switch (verb)
        {
            case "cancel":
            case "delete":
            case "remove":
                state.Tasks.Remove(match);
                _neoCortex.SaveScheduledTasks(state);
                return new ToolExecutionResult(true, $"Cancelled scheduled task: {match.Description}", MutatedState: true);
            case "pause":
                match.Paused = true;
                _neoCortex.SaveScheduledTasks(state);
                return new ToolExecutionResult(true, $"Paused scheduled task: {match.Description}", MutatedState: true);
            case "resume":
            case "unpause":
                match.Paused = false;
                if (match.NextFireAtUtc is null || match.NextFireAtUtc < DateTime.UtcNow)
                    match.NextFireAtUtc = ComputeNextFire(SpecFromTask(match), DateTime.UtcNow);
                _neoCortex.SaveScheduledTasks(state);
                return new ToolExecutionResult(true, $"Resumed scheduled task: {match.Description}. Next fire {match.NextFireAtUtc:yyyy-MM-dd HH:mm} UTC.", MutatedState: true);
            default:
                return new ToolExecutionResult(false, $"Unknown scheduler verb \"{verb}\". Use list/cancel/pause/resume.", ErrorCode: "schedule_bad_verb");
        }
    }

    private static ScheduledTask? FindTask(List<ScheduledTask> tasks, string target)
    {
        if (string.IsNullOrWhiteSpace(target)) return null;
        var byIdPrefix = tasks.FirstOrDefault(t => t.Id.StartsWith(target, StringComparison.OrdinalIgnoreCase));
        if (byIdPrefix is not null) return byIdPrefix;
        return tasks.FirstOrDefault(t => t.Description.Contains(target, StringComparison.OrdinalIgnoreCase));
    }

    private static ScheduleSpec SpecFromTask(ScheduledTask t) =>
        new(t.Cadence, t.DayOfWeek, t.TimeOfDay, t.IntervalMinutes, t.RandomizeSeed, t.Description);

    private static string DescribeRoute(AdminIntentRoute route)
    {
        var server = route.Slots.ServerName;
        var cmd = route.Slots.CommandText;
        if (!string.IsNullOrWhiteSpace(server) && !string.IsNullOrWhiteSpace(cmd))
            return $"{cmd} {server}";
        return route.Intent.ToString();
    }

    // ---- Cron-ish next-fire computation ---------------------------------------------

    public static DateTime? ComputeNextFire(ScheduleSpec spec, DateTime nowUtc)
    {
        switch (spec.Cadence?.ToLowerInvariant())
        {
            case "once":
                if (spec.IntervalMinutes is int min)
                    return nowUtc.AddMinutes(min);
                if (TryParseTimeOfDay(spec.TimeOfDay, out var tod))
                {
                    var candidate = new DateTime(nowUtc.Year, nowUtc.Month, nowUtc.Day, tod.Hours, tod.Minutes, 0, DateTimeKind.Utc);
                    if (candidate <= nowUtc) candidate = candidate.AddDays(1);
                    return candidate;
                }
                return null;

            case "daily":
                if (TryParseTimeOfDay(spec.TimeOfDay, out var dtod))
                {
                    var c = new DateTime(nowUtc.Year, nowUtc.Month, nowUtc.Day, dtod.Hours, dtod.Minutes, 0, DateTimeKind.Utc);
                    if (c <= nowUtc) c = c.AddDays(1);
                    return c;
                }
                return null;

            case "weekly":
                if (TryParseDayOfWeek(spec.DayOfWeek, out var dow) && TryParseTimeOfDay(spec.TimeOfDay, out var wtod))
                {
                    var c = new DateTime(nowUtc.Year, nowUtc.Month, nowUtc.Day, wtod.Hours, wtod.Minutes, 0, DateTimeKind.Utc);
                    var daysAhead = ((int)dow - (int)c.DayOfWeek + 7) % 7;
                    c = c.AddDays(daysAhead);
                    if (c <= nowUtc) c = c.AddDays(7);
                    return c;
                }
                return null;

            case "interval":
                if (spec.IntervalMinutes is int im && im > 0)
                    return nowUtc.AddMinutes(im);
                return null;

            default:
                return null;
        }
    }

    public static DateTime? AdvanceAfterFire(ScheduledTask task, DateTime nowUtc)
    {
        switch (task.Cadence?.ToLowerInvariant())
        {
            case "once":
                return null;
            case "daily":
                return TryParseTimeOfDay(task.TimeOfDay, out var t)
                    ? nowUtc.Date.AddDays(1).AddHours(t.Hours).AddMinutes(t.Minutes)
                    : nowUtc.AddDays(1);
            case "weekly":
                return ComputeNextFire(SpecFromTask(task), nowUtc);
            case "interval":
                return task.IntervalMinutes is int im && im > 0 ? nowUtc.AddMinutes(im) : null;
            default:
                return null;
        }
    }

    private static bool TryParseTimeOfDay(string? value, out TimeSpan tod)
    {
        tod = default;
        if (string.IsNullOrWhiteSpace(value)) return false;
        if (TimeSpan.TryParse(value, out tod)) return true;
        // "4am" / "4pm"
        var match = System.Text.RegularExpressions.Regex.Match(value, @"^(?<h>\d{1,2})(?::(?<m>\d{1,2}))?\s*(?<ap>am|pm)?$",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (!match.Success) return false;
        var h = int.Parse(match.Groups["h"].Value);
        var m = match.Groups["m"].Success ? int.Parse(match.Groups["m"].Value) : 0;
        var ap = match.Groups["ap"].Value.ToLowerInvariant();
        if (ap == "pm" && h < 12) h += 12;
        if (ap == "am" && h == 12) h = 0;
        if (h is < 0 or > 23 || m is < 0 or > 59) return false;
        tod = new TimeSpan(h, m, 0);
        return true;
    }

    private static bool TryParseDayOfWeek(string? value, out DayOfWeek day)
    {
        day = DayOfWeek.Monday;
        if (string.IsNullOrWhiteSpace(value)) return false;
        return Enum.TryParse(value.Trim(), true, out day);
    }
}

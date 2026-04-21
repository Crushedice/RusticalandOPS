using System.Diagnostics;

internal sealed class TmuxServerManager
{
    public Task<CommandExecutionResult> DiscoverSessionsAsync() => ExecuteShellAsync("tmux list-sessions -F '#S'");

    public Task<CommandExecutionResult> ReadConsoleAsync(string sessionName, int lines = 120)
        => ExecuteShellAsync($"tmux capture-pane -pt {Escape(sessionName)} -S -{Math.Max(10, lines)}");

    public Task<CommandExecutionResult> SendCommandAsync(string sessionName, string command)
        => ExecuteShellAsync($"tmux send-keys -t {Escape(sessionName)} {Escape(command)} Enter");

    private static async Task<CommandExecutionResult> ExecuteShellAsync(string command)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "/usr/bin/env",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        psi.ArgumentList.Add("bash");
        psi.ArgumentList.Add("-lc");
        psi.ArgumentList.Add(command);

        using var process = new Process { StartInfo = psi };
        process.Start();
        var stdout = await process.StandardOutput.ReadToEndAsync();
        var stderr = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();

        return new CommandExecutionResult
        {
            Ok = process.ExitCode == 0,
            ExitCode = process.ExitCode,
            StdOut = stdout.Trim(),
            StdErr = stderr.Trim(),
            Arguments = new[] { command }
        };
    }

    private static string Escape(string value) => "'" + value.Replace("'", "'\\''") + "'";
}

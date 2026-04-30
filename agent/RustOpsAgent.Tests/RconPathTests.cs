using System.Text.Json;
using RustOpsAgent.Domains.Rust;

namespace RustOpsAgent.Tests;

public class RconPathTests
{
    [Fact]
    public void LoadConnectionFromConfig_Uses_RconIp_From_AdditionalArgs_When_TopLevel_Missing()
    {
        using var doc = JsonDocument.Parse("""
        {
          "rcon.port": 28016,
          "rcon.password": "secret",
          "additionalArgs": "+rcon.ip 10.10.0.7 +rcon.web 1"
        }
        """);

        var connection = RustDirectRconHelper.LoadConnectionFromConfig(doc.RootElement);

        Assert.NotNull(connection);
        Assert.Equal("ws://10.10.0.7:28016/secret", connection!.Value.Uri.ToString());
        Assert.Equal("secret", connection.Value.Password);
    }

    [Fact]
    public void LoadConnectionFromConfig_Does_Not_Use_ServerIp_As_RconHost()
    {
        // server.ip is the player-facing bind address, not the RCON connection target.
        // When rcon.ip is absent, we must default to 127.0.0.1 rather than server.ip.
        using var doc = JsonDocument.Parse("""
        {
          "rcon.port": 28016,
          "rcon.password": "secret",
          "server.ip": "10.10.0.7"
        }
        """);

        var connection = RustDirectRconHelper.LoadConnectionFromConfig(doc.RootElement);

        Assert.NotNull(connection);
        Assert.Equal("ws://127.0.0.1:28016/secret", connection!.Value.Uri.ToString());
    }

    [Fact]
    public void LoadConnectionFromConfig_Reads_Port_And_Password_From_AdditionalArgs()
    {
        using var doc = JsonDocument.Parse("""
        {
          "additionalArgs": "+rcon.port 28017 +rcon.password mypass +rcon.web 1"
        }
        """);

        var connection = RustDirectRconHelper.LoadConnectionFromConfig(doc.RootElement);

        Assert.NotNull(connection);
        Assert.Equal("ws://127.0.0.1:28017/mypass", connection!.Value.Uri.ToString());
        Assert.Equal("mypass", connection.Value.Password);
    }

    [Fact]
    public void LoadConnectionFromConfig_Connects_Even_When_RconWeb_Is_Zero()
    {
        // rcon.web is no longer gated — WebRCON is always the transport.
        using var doc = JsonDocument.Parse("""
        {
          "rcon.port": 28016,
          "rcon.password": "secret",
          "additionalArgs": "+rcon.web 0"
        }
        """);

        var connection = RustDirectRconHelper.LoadConnectionFromConfig(doc.RootElement);

        Assert.NotNull(connection);
        Assert.Equal("ws://127.0.0.1:28016/secret", connection!.Value.Uri.ToString());
    }

    [Fact]
    public void LoadConnectionFromConfig_Defaults_WebRcon_To_Enabled_When_Flag_Is_Missing()
    {
        using var doc = JsonDocument.Parse("""
        {
          "rcon.port": 28016,
          "rcon.password": "secret"
        }
        """);

        var connection = RustDirectRconHelper.LoadConnectionFromConfig(doc.RootElement);

        Assert.NotNull(connection);
        Assert.Equal("ws://127.0.0.1:28016/secret", connection!.Value.Uri.ToString());
    }

    [Fact]
    public void BuildApiFallbackReply_Uses_Output_Messages_When_DirectReply_Is_Empty()
    {
        using var doc = JsonDocument.Parse("""
        {
          "directReply": null,
          "output": {
            "messages": [
              "12/31/2024 03:14:22: first line",
              "12/31/2024 03:14:23: second line"
            ]
          }
        }
        """);

        var reply = RustRconToolHandler.BuildApiFallbackReply("alpha", doc.RootElement);

        Assert.Contains("first line", reply);
        Assert.Contains("second line", reply);
        Assert.DoesNotContain("no direct reply", reply, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void BuildApiFallbackReply_Ignores_Rcon_Command_Echo_Lines()
    {
        using var doc = JsonDocument.Parse("""
        {
          "command": "say hello",
          "directReply": null,
          "output": {
            "messages": [
              "[rcon] 127.0.0.1: say hello"
            ]
          }
        }
        """);

        var reply = RustRconToolHandler.BuildApiFallbackReply("alpha", doc.RootElement);

        Assert.Contains("command sent via API", reply, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("[rcon]", reply, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ExtractCommandFromMessage_Does_Not_Match_Rcon_Substring_Inside_Serverconfig()
    {
        var command = RustRconToolHandler.ExtractCommandFromMessage("show me the serverconfig for cotton");

        Assert.Equal(string.Empty, command);
    }

    [Fact]
    public void NormalizeCommandForServer_Strips_Trailing_Server_Qualifier()
    {
        var command = RustRconToolHandler.NormalizeCommandForServer("status on cotton", "cotton");

        Assert.Equal("status", command);
    }

    [Fact]
    public void NormalizeCommandForServer_Strips_Generic_Trailing_Server_Qualifier()
    {
        var command = RustRconToolHandler.NormalizeCommandForServer("say hello to monthly server", "cotton");

        Assert.Equal("say hello", command);
    }

    [Fact]
    public void NormalizeCommandForServer_Does_Not_Strip_Command_Argument_After_To()
    {
        var command = RustRconToolHandler.NormalizeCommandForServer("say hello to all", "cotton");

        Assert.Equal("say hello to all", command);
    }

    [Fact]
    public void BuildApiFallbackReply_Reports_Unconfirmed_Api_Result()
    {
        using var doc = JsonDocument.Parse("""
        {
          "ok": false,
          "message": "tmux fallback attempted but command was not confirmed"
        }
        """);

        var reply = RustRconToolHandler.BuildApiFallbackReply("alpha", doc.RootElement);

        Assert.Contains("not confirmed", reply, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void BuildApiFallbackReply_Reports_Tmux_Fallback_When_Api_Uses_Rustmgr_Send()
    {
        using var doc = JsonDocument.Parse("""
        {
          "ok": true,
          "transport": "rustmgr-send",
          "directReply": null,
          "fallback": "sent to modded via tmux fallback: say greetings",
          "output": {
            "messages": []
          }
        }
        """);

        var reply = RustRconToolHandler.BuildApiFallbackReply("modded", doc.RootElement);

        Assert.Contains("tmux fallback", reply, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("say greetings", reply, StringComparison.OrdinalIgnoreCase);
    }
}

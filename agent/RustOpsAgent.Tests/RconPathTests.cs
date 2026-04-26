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
}

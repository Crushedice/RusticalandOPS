using System.Text.Json;
using RustOpsAgent.Domains.Rust;

namespace RustOpsAgent.Tests;

public class RconPathTests
{
    [Fact]
    public void LoadConnectionFromConfig_Uses_AdditionalArgs_Host_When_RconIp_Is_Missing()
    {
        using var doc = JsonDocument.Parse("""
        {
          "rcon.port": 28016,
          "rcon.password": "secret",
          "additionalArgs": "+server.ip 10.10.0.7 +rcon.web 1"
        }
        """);

        var connection = RustDirectRconHelper.LoadConnectionFromConfig(doc.RootElement);

        Assert.NotNull(connection);
        Assert.Equal("ws://10.10.0.7:28016/secret", connection!.Value.Uri.ToString());
        Assert.Equal("secret", connection.Value.Password);
    }

    [Fact]
    public void LoadConnectionFromConfig_Returns_Null_When_WebRcon_Is_Disabled()
    {
        using var doc = JsonDocument.Parse("""
        {
          "rcon.port": 28016,
          "rcon.password": "secret",
          "additionalArgs": "+rcon.web 0"
        }
        """);

        var connection = RustDirectRconHelper.LoadConnectionFromConfig(doc.RootElement);

        Assert.Null(connection);
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
}

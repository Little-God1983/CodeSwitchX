using System.IO;
using System.Text;
using System.Text.Json.Nodes;
using CodeSwitchX.Core.Yard;
using CodeSwitchX.UI.Raven;

namespace CodeSwitchX.UI.Tests.Raven;

public sealed class StartSettingsTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "csx-start-settings-" + Guid.NewGuid().ToString("N"));
    private readonly string _file;

    public StartSettingsTests()
    {
        Directory.CreateDirectory(_folder);
        _file = StartSettings.FileIn(_folder);
    }

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    [Fact]
    public void Nothing_to_set_writes_nothing()
    {
        StartSettings.Apply(_folder, null, null).ShouldBeNull();

        Directory.Exists(Path.GetDirectoryName(_file)).ShouldBeFalse();
    }

    [Fact]
    public void Without_a_file_one_is_written_and_removed_again_with_its_folder()
    {
        using (var settings = StartSettings.Apply(_folder, "claude-opus-5-5", "high").ShouldNotBeNull())
        {
            var written = JsonNode.Parse(File.ReadAllText(_file))!.AsObject();
            written["model"]!.GetValue<string>().ShouldBe("claude-opus-5-5");
            written["effortLevel"]!.GetValue<string>().ShouldBe("high");
        }

        File.Exists(_file).ShouldBeFalse();
        Directory.Exists(Path.GetDirectoryName(_file)).ShouldBeFalse();
    }

    [Fact]
    public void The_user_s_file_keeps_its_other_keys_and_comes_back_byte_for_byte()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_file)!);
        var original = "{\r\n  \"permissions\": { \"allow\": [\"Bash(git status)\"] },\r\n  \"model\": \"sonnet\"\r\n}\r\n";
        File.WriteAllText(_file, original, new UTF8Encoding(false));

        var settings = StartSettings.Apply(_folder, null, "max").ShouldNotBeNull();
        var written = JsonNode.Parse(File.ReadAllText(_file))!.AsObject();
        written["model"]!.GetValue<string>().ShouldBe("sonnet", "only what is given is set");
        written["effortLevel"]!.GetValue<string>().ShouldBe("max");
        written["permissions"]!["allow"]![0]!.GetValue<string>().ShouldBe("Bash(git status)");

        settings.Dispose();

        settings.Restored.ShouldBeTrue();
        File.ReadAllText(_file).ShouldBe(original);
        Directory.Exists(Path.GetDirectoryName(_file)).ShouldBeTrue();
    }

    [Fact]
    public void A_file_the_user_changed_meanwhile_is_left_as_they_made_it()
    {
        var settings = StartSettings.Apply(_folder, "claude-opus-5-5", null).ShouldNotBeNull();
        File.WriteAllText(_file, "{ \"model\": \"haiku\" }");

        settings.Dispose();

        settings.Restored.ShouldBeFalse();
        File.ReadAllText(_file).ShouldBe("{ \"model\": \"haiku\" }");
    }

    [Fact]
    public void A_file_that_is_no_JSON_is_refused_and_not_touched()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_file)!);
        File.WriteAllText(_file, "{ not json");

        Should.Throw<YardActionException>(() => StartSettings.Apply(_folder, "claude-opus-5-5", null)).Message.ShouldContain("is not valid JSON");

        File.ReadAllText(_file).ShouldBe("{ not json");
    }

    [Fact]
    public void Putting_back_twice_does_it_once()
    {
        var settings = StartSettings.Apply(_folder, "claude-opus-5-5", null).ShouldNotBeNull();
        settings.Dispose();
        Directory.CreateDirectory(Path.GetDirectoryName(_file)!);
        File.WriteAllText(_file, "{}"); // the user's own, written afterwards

        settings.Dispose();

        File.ReadAllText(_file).ShouldBe("{}");
    }
}

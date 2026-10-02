using System.IO;
using System.Text;
using System.Text.Json.Nodes;
using CodeSwitchX.Core.Yard;
using CodeSwitchX.UI.Raven;

namespace CodeSwitchX.UI.Tests.Raven;

public sealed class StartSettingsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "csx-start-settings-" + Guid.NewGuid().ToString("N"));
    private readonly string _folder;
    private readonly string _pending;
    private readonly string _file;

    public StartSettingsTests()
    {
        _folder = Path.Combine(_root, "App");
        _pending = Path.Combine(_root, "pending");
        Directory.CreateDirectory(_folder);
        _file = StartSettings.FileIn(_folder);
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private StartSettings Apply(string? model, string? effort) => StartSettings.Apply(_folder, model, effort, _pending).ShouldNotBeNull();

    private void UserFile(string json, bool bom = false)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_file)!);
        File.WriteAllBytes(_file, [.. (bom ? new byte[] { 0xEF, 0xBB, 0xBF } : []), .. Encoding.UTF8.GetBytes(json)]);
    }

    private JsonObject Current() => JsonNode.Parse(File.ReadAllText(_file))!.AsObject();

    [Fact]
    public void Nothing_to_set_writes_nothing()
    {
        StartSettings.Apply(_folder, null, null, _pending).ShouldBeNull();

        Directory.Exists(Path.GetDirectoryName(_file)).ShouldBeFalse();
        Directory.Exists(_pending).ShouldBeFalse();
    }

    [Fact]
    public void Without_a_file_one_is_written_and_removed_again_with_its_folder()
    {
        using (Apply("claude-opus-5-5", "high"))
        {
            Current()["model"]!.GetValue<string>().ShouldBe("claude-opus-5-5");
            Current()["effortLevel"]!.GetValue<string>().ShouldBe("high");
        }

        File.Exists(_file).ShouldBeFalse();
        Directory.Exists(Path.GetDirectoryName(_file)).ShouldBeFalse();
        Directory.EnumerateFiles(_pending).ShouldBeEmpty("put back, there is nothing left to recover");
    }

    [Fact]
    public void The_user_s_file_keeps_its_other_keys_and_comes_back_byte_for_byte()
    {
        var original = "{\r\n  \"permissions\": { \"allow\": [\"Bash(git status)\"] },\r\n  \"model\": \"sonnet\"\r\n}\r\n";
        UserFile(original);

        var settings = Apply(null, "max");
        Current()["model"]!.GetValue<string>().ShouldBe("sonnet", "only what is given is set");
        Current()["effortLevel"]!.GetValue<string>().ShouldBe("max");
        Current()["permissions"]!["allow"]![0]!.GetValue<string>().ShouldBe("Bash(git status)");

        settings.Dispose();

        settings.Restored.ShouldBeTrue();
        File.ReadAllText(_file).ShouldBe(original);
    }

    [Fact]
    public void A_file_saved_with_a_byte_order_mark_is_read_and_comes_back_with_it()
    {
        // Notepad and PowerShell 5 write one; Claude Code reads past it.
        UserFile("{ \"permissions\": {} }", bom: true);
        var original = File.ReadAllBytes(_file);

        var settings = Apply("claude-opus-5-5", null);
        Current()["model"]!.GetValue<string>().ShouldBe("claude-opus-5-5");
        settings.Dispose();

        File.ReadAllBytes(_file).ShouldBe(original);
    }

    [Fact]
    public void What_was_written_meanwhile_stays_and_only_Raven_s_keys_are_undone()
    {
        // The user allowed something "for good" in another chat of the folder while the voice chat started: Claude Code
        // wrote the file, with Raven's model in it, and its permission added.
        UserFile("{ \"model\": \"sonnet\" }");
        var settings = Apply("claude-opus-5-5", "high");
        var meanwhile = Current();
        meanwhile["permissions"] = new JsonObject { ["allow"] = new JsonArray("Bash(npm test)") };
        File.WriteAllText(_file, meanwhile.ToJsonString());

        settings.Dispose();

        settings.Restored.ShouldBeTrue();
        var after = Current();
        after["model"]!.GetValue<string>().ShouldBe("sonnet", "what it was before");
        after.ContainsKey("effortLevel").ShouldBeFalse("it was not there before");
        after["permissions"]!["allow"]![0]!.GetValue<string>().ShouldBe("Bash(npm test)");
    }

    [Fact]
    public void A_key_the_user_set_to_something_else_meanwhile_is_theirs()
    {
        var settings = Apply("claude-opus-5-5", "high");
        File.WriteAllText(_file, "{ \"model\": \"haiku\", \"effortLevel\": \"high\" }");

        settings.Dispose();

        Current()["model"]!.GetValue<string>().ShouldBe("haiku");
        Current().ContainsKey("effortLevel").ShouldBeFalse("still what Raven set: undone");
    }

    [Fact]
    public void A_file_the_user_removed_or_broke_meanwhile_is_left_alone()
    {
        var removed = Apply("claude-opus-5-5", null);
        File.Delete(_file);
        removed.Dispose();
        File.Exists(_file).ShouldBeFalse();

        var broken = Apply("claude-opus-5-5", null);
        File.WriteAllText(_file, "{ half");
        broken.Dispose();
        File.ReadAllText(_file).ShouldBe("{ half");
    }

    [Fact]
    public void A_file_that_is_no_JSON_is_refused_and_not_touched()
    {
        UserFile("{ not json");

        Should.Throw<YardActionException>(() => StartSettings.Apply(_folder, "claude-opus-5-5", null, _pending)).Message.ShouldContain("is not valid JSON");

        File.ReadAllText(_file).ShouldBe("{ not json");
    }

    [Fact]
    public void A_start_cut_short_by_the_app_ending_is_put_back_at_the_next_start()
    {
        UserFile("{ \"model\": \"sonnet\" }");
        var original = File.ReadAllBytes(_file);
        _ = Apply("claude-opus-5-5", "high"); // CodeSwitchX dies here: never disposed

        StartSettings.RecoverAll(_pending, NextRun).ShouldBeEmpty();

        File.ReadAllBytes(_file).ShouldBe(original);
        Directory.EnumerateFiles(_pending).ShouldBeEmpty();
    }

    [Fact]
    public void Recovery_leaves_the_notes_of_this_run_s_own_starts_alone()
    {
        // A voice start right after launch, while the recovery still runs: its note is no leftover.
        var settings = Apply("claude-opus-5-5", null);

        StartSettings.RecoverAll(_pending, DateTime.UtcNow.AddMinutes(-1)).ShouldBeEmpty();

        Current()["model"]!.GetValue<string>().ShouldBe("claude-opus-5-5", "the chat is still starting");
        settings.Dispose();
        File.Exists(_file).ShouldBeFalse();
    }

    [Fact]
    public void A_start_after_a_put_back_that_failed_puts_the_first_back_before_it_reads_the_file()
    {
        // The first start's put-back failed (the file was locked): its note is kept. The next start in the folder must not
        // take Raven's first model for the user's own.
        UserFile("{ \"model\": \"sonnet\" }");
        var original = File.ReadAllBytes(_file);
        _ = Apply("claude-opus-5-5", "high"); // its put-back never happened

        using (var second = Apply("claude-haiku-4-5-20251001", null))
        {
            Current()["model"]!.GetValue<string>().ShouldBe("claude-haiku-4-5-20251001");
            Current().ContainsKey("effortLevel").ShouldBeFalse("the first start's effort is gone with it");
        }

        File.ReadAllBytes(_file).ShouldBe(original);
        Directory.EnumerateFiles(_pending).ShouldBeEmpty();
    }

    [Fact]
    public void A_start_whose_earlier_put_back_still_cannot_be_done_is_refused()
    {
        _ = Apply("claude-opus-5-5", null); // its put-back never happened
        using var locked = new FileStream(_file, FileMode.Open, FileAccess.Read, FileShare.None);

        Should.Throw<YardActionException>(() => StartSettings.Apply(_folder, "claude-haiku-4-5-20251001", null, _pending)).Message
            .ShouldContain("still holds the model of an earlier chat Raven started");

        Directory.EnumerateFiles(_pending).ShouldHaveSingleItem("the note that knows what the file was is kept");
    }

    [Fact]
    public void Recovering_without_anything_left_does_nothing()
    {
        StartSettings.RecoverAll(_pending, NextRun).ShouldBeEmpty();

        Directory.CreateDirectory(_pending);
        File.WriteAllText(Path.Combine(_pending, "junk.json"), "{ half");
        StartSettings.RecoverAll(_pending, NextRun).ShouldBeEmpty();
        Directory.EnumerateFiles(_pending).ShouldBeEmpty("what cannot be read is no instruction to touch anything");
    }

    /// <summary>A run that began after every note here was written: they are all left over from an earlier one.</summary>
    private static DateTime NextRun => DateTime.UtcNow.AddMinutes(1);

    [Fact]
    public void Putting_back_twice_does_it_once()
    {
        var settings = Apply("claude-opus-5-5", null);
        settings.Dispose();
        UserFile("{ \"model\": \"claude-opus-5-5\" }"); // the user's own, afterwards, the same model as it happens

        settings.Dispose();

        Current()["model"]!.GetValue<string>().ShouldBe("claude-opus-5-5");
    }
}

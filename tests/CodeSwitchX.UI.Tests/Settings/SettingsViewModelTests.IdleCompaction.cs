using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using CodeSwitchX.UI.Settings;

namespace CodeSwitchX.UI.Tests.Settings;

/// <summary>Claude Code's idle compaction on the Yard page (#450): read from and written to its settings.json only.</summary>
public partial class SettingsViewModelTests
{
    private const string MinTokensKey = "CLAUDE_CODE_IDLE_COMPACT_MIN_TOKENS";

    private void IdleFile(string json)
    {
        Directory.CreateDirectory(_claude.ClaudeDirectory);
        File.WriteAllText(_claude.SettingsFile, json);
    }

    private JsonObject IdleJson() => JsonNode.Parse(File.ReadAllText(_claude.SettingsFile))!.AsObject();

    [Fact]
    public async Task Idle_compaction_without_a_settings_file_shows_on_from_200000_tokens_and_creates_none()
    {
        await _vm.LoadAsync(CancellationToken.None);

        _vm.IdleCompaction.ShouldBeTrue();
        _vm.IdleCompactionMinTokens.ShouldBe(200_000);
        _vm.IdleCompactionChoices.Select(c => c.Value).ShouldBe([100_000, 150_000, 200_000, 300_000, 400_000]);
        _vm.IdleCompactionReadable.ShouldBeTrue();
        File.Exists(_claude.SettingsFile).ShouldBeFalse();
    }

    [Fact]
    public async Task Idle_compaction_shows_what_the_file_says()
    {
        const string json = """{"idleCompaction":false,"env":{"CLAUDE_CODE_IDLE_COMPACT_MIN_TOKENS":"100000"}}""";
        IdleFile(json);

        await _vm.LoadAsync(CancellationToken.None);
        await _vm.IdleCompactionSaved;

        _vm.IdleCompaction.ShouldBeFalse();
        _vm.IdleCompactionMinTokens.ShouldBe(100_000);
        _vm.IdleCompactionSizeEnabled.ShouldBeFalse();
        File.ReadAllText(_claude.SettingsFile).ShouldBe(json);
    }

    [Fact]
    public async Task Switching_idle_compaction_off_writes_false_and_says_so()
    {
        IdleFile("""{"model":"x"}""");
        await _vm.LoadAsync(CancellationToken.None);

        _vm.IdleCompaction = false;
        await _vm.IdleCompactionSaved;

        var settings = IdleJson();
        settings["idleCompaction"]!.GetValueKind().ShouldBe(JsonValueKind.False);
        settings["model"]!.GetValue<string>().ShouldBe("x");
        _vm.LastMessage.ShouldNotBeNull().ShouldContain("Idle compaction saved");
    }

    [Fact]
    public async Task Switching_it_on_again_removes_the_key()
    {
        IdleFile("""{"idleCompaction":false}""");
        await _vm.LoadAsync(CancellationToken.None);

        _vm.IdleCompaction = true;
        await _vm.IdleCompactionSaved;

        IdleJson().ContainsKey("idleCompaction").ShouldBeFalse();
    }

    [Fact]
    public async Task Picking_a_size_writes_it_into_env()
    {
        await _vm.LoadAsync(CancellationToken.None);

        _vm.IdleCompactionMinTokens = 100_000;
        await _vm.IdleCompactionSaved;

        IdleJson()["env"]![MinTokensKey]!.GetValue<string>().ShouldBe("100000");
    }

    [Fact]
    public async Task A_size_in_the_file_that_is_no_choice_is_shown_as_one_more()
    {
        IdleFile("""{"env":{"CLAUDE_CODE_IDLE_COMPACT_MIN_TOKENS":"250000"}}""");

        await _vm.LoadAsync(CancellationToken.None);
        await _vm.IdleCompactionSaved;

        _vm.IdleCompactionChoices.Select(c => c.Value).ShouldBe([100_000, 150_000, 200_000, 250_000, 300_000, 400_000]);
        _vm.IdleCompactionChoices.Single(c => c.Value == 250_000).Label.ShouldBe("250,000 tokens");
        _vm.IdleCompactionMinTokens.ShouldBe(250_000);
    }

    [Fact]
    public async Task Refresh_shows_a_change_made_in_the_file_meanwhile()
    {
        await _vm.LoadAsync(CancellationToken.None);
        const string json = """{"idleCompaction":false}""";
        IdleFile(json);

        _vm.Refresh();
        await _vm.IdleCompactionSaved;

        _vm.IdleCompaction.ShouldBeFalse();
        File.ReadAllText(_claude.SettingsFile).ShouldBe(json);
    }

    [Fact]
    public async Task A_settings_file_that_cannot_be_read_disables_the_card()
    {
        IdleFile("not json");

        await _vm.LoadAsync(CancellationToken.None);

        _vm.IdleCompactionProblem.ShouldNotBeNull();
        _vm.IdleCompactionReadable.ShouldBeFalse();
        _vm.IdleCompactionSizeEnabled.ShouldBeFalse();
    }

    [Fact]
    public async Task A_write_that_fails_says_why_and_shows_the_file_again()
    {
        IdleFile("""{"model":"x"}""");
        await _vm.LoadAsync(CancellationToken.None);
        File.SetAttributes(_claude.SettingsFile, FileAttributes.ReadOnly);
        try
        {
            _vm.IdleCompaction = false;
            await _vm.IdleCompactionSaved;

            _vm.LastMessage.ShouldNotBeNull().ShouldContain("Cannot write");
            _vm.IdleCompaction.ShouldBeTrue();
        }
        finally
        {
            File.SetAttributes(_claude.SettingsFile, FileAttributes.Normal);
        }
    }

    [Fact]
    public async Task Two_quick_changes_are_both_written()
    {
        await _vm.LoadAsync(CancellationToken.None);

        _vm.IdleCompaction = false;
        _vm.IdleCompactionMinTokens = 150_000;
        await _vm.IdleCompactionSaved;

        var settings = IdleJson();
        settings["idleCompaction"]!.GetValueKind().ShouldBe(JsonValueKind.False);
        settings["env"]![MinTokensKey]!.GetValue<string>().ShouldBe("150000");
    }

    [Fact]
    public async Task A_size_below_the_floor_from_the_view_is_ignored()
    {
        await _vm.LoadAsync(CancellationToken.None);

        _vm.IdleCompactionMinTokens = 0;
        await _vm.IdleCompactionSaved;

        File.Exists(_claude.SettingsFile).ShouldBeFalse();
    }

    [Fact]
    public void Tokens_are_written_with_commas_in_any_locale()
    {
        var culture = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = new CultureInfo("de-DE");
        try
        {
            SettingsViewModel.TokensText(200_000).ShouldBe("200,000 tokens");
        }
        finally
        {
            CultureInfo.CurrentCulture = culture;
        }
    }

    [Theory]
    [InlineData("compact")]
    [InlineData("idle compaction")]
    [InlineData("cache")]
    public void A_search_for_compaction_or_the_cache_finds_the_Yard_page(string search)
    {
        _vm.Search = search;

        _vm.Pages.ShouldContain(p => p.Page == SettingsPage.Yard);
    }
}

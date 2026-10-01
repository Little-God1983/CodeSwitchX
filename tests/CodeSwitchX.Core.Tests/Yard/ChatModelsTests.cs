using CodeSwitchX.Core.Yard;

namespace CodeSwitchX.Core.Tests.Yard;

public sealed class ChatModelsTests
{
    private static readonly IReadOnlyList<ModelAlias> Aliases = ChatModels.DefaultAliases;

    [Theory]
    [InlineData("Fable", "claude-fable-5-1")]
    [InlineData("fable", "claude-fable-5-1")]
    [InlineData("Opus", "claude-opus-5-5")]
    [InlineData("Opus 5.5", "claude-opus-5-5")]
    [InlineData("fable 5.1", "claude-fable-5-1")]
    [InlineData("Haiku 4.5", "claude-haiku-4-5-20251001")]
    [InlineData("sonnet.", "claude-sonnet-5-5")]
    [InlineData("Haiku", "claude-haiku-4-5-20251001")]
    [InlineData("claude-opus-5-5", "claude-opus-5-5")]
    [InlineData("Claude-Sonnet-5", "claude-sonnet-5")]
    public void A_spoken_name_or_a_full_id_is_a_model(string said, string id)
    {
        ChatModels.ResolveModel(said, Aliases).ShouldBe(id);
    }

    [Theory]
    [InlineData("")]
    [InlineData("GPT")]
    [InlineData("Opus Max")]
    [InlineData("claude opus")]
    [InlineData("Opus 4.1")] // an older Opus, not the one the alias stands for
    [InlineData("Sonnet 4.5")]
    [InlineData("Haiku 4.5.1")]
    public void Anything_else_is_no_model(string said)
    {
        ChatModels.ResolveModel(said, Aliases).ShouldBeNull();
    }

    [Fact]
    public void The_table_is_read_one_alias_a_line_and_lines_of_another_shape_are_left_out()
    {
        var aliases = ChatModels.ParseAliases("Fable = claude-fable-5-2\r\nOpus: claude-opus-5-5\nnonsense\n= claude-x\nFable = claude-old\nBad = two words\n");

        aliases.ShouldBe([new ModelAlias("Fable", "claude-fable-5-2"), new ModelAlias("Opus", "claude-opus-5-5")]);
        ChatModels.ResolveModel("fable", aliases).ShouldBe("claude-fable-5-2");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("just words")]
    public void A_table_with_no_alias_in_it_is_the_default_one(string? text)
    {
        ChatModels.ParseAliases(text).ShouldBe(ChatModels.DefaultAliases);
    }

    [Fact]
    public void The_default_table_reads_back_as_it_is_written()
    {
        ChatModels.ParseAliases(ChatModels.FormatAliases(ChatModels.DefaultAliases)).ShouldBe(ChatModels.DefaultAliases);
    }

    [Theory]
    [InlineData("high", "high")]
    [InlineData("High", "high")]
    [InlineData("extra high", "xhigh")]
    [InlineData("x-high", "xhigh")]
    [InlineData("maximum", "max")]
    [InlineData("mid", "medium")]
    [InlineData("low", "low")]
    public void An_effort_is_read_as_it_is_said(string said, string level)
    {
        ChatModels.ResolveEffort(said).ShouldBe(level);
    }

    [Theory]
    [InlineData("")]
    [InlineData("hard")]
    [InlineData(null)]
    public void Another_word_is_no_effort(string? said)
    {
        ChatModels.ResolveEffort(said).ShouldBeNull();
    }

    [Theory]
    [InlineData("claude-fable-5-1", "Fable 5.1")]
    [InlineData("claude-opus-5-5", "Opus 5.5")]
    [InlineData("claude-haiku-4-5-20251001", "Haiku 4.5")]
    [InlineData("claude-sonnet-5", "Sonnet 5")]
    [InlineData("gpt-5", "gpt-5")]
    [InlineData("claude-3-5-sonnet", "claude-3-5-sonnet")]
    public void An_id_reads_as_its_name_and_version(string id, string shown)
    {
        ChatModels.DisplayName(id).ShouldBe(shown);
    }
}

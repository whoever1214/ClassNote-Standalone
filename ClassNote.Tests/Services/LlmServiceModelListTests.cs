using ClassNote.Services;
using Xunit;

namespace ClassNote.Tests.Services;

public class LlmServiceModelListTests
{
    [Fact]
    public void ParseModelIds_StandardResponse_ReadsIds()
    {
        const string json = """
        {
          "object": "list",
          "data": [
            { "id": "deepseek-chat", "object": "model" },
            { "id": "deepseek-reasoner", "object": "model" }
          ]
        }
        """;

        var models = LlmService.ParseModelIds(json);

        Assert.Equal(new[] { "deepseek-chat", "deepseek-reasoner" }, models);
    }

    [Fact]
    public void ParseModelIds_SortsAndDeduplicates()
    {
        const string json = """
        { "data": [ { "id": "zeta" }, { "id": "Alpha" }, { "id": "ZETA" }, { "id": "beta" } ] }
        """;

        var models = LlmService.ParseModelIds(json);

        // 去重（不区分大小写）后按字母序排列，下拉框里顺序稳定
        Assert.Equal(new[] { "Alpha", "beta", "zeta" }, models);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not json at all")]
    [InlineData("{}")]
    [InlineData("{ \"data\": null }")]
    [InlineData("{ \"data\": [] }")]
    public void ParseModelIds_UnusableInput_ReturnsEmpty(string? json)
    {
        Assert.Empty(LlmService.ParseModelIds(json));
    }

    [Fact]
    public void ParseModelIds_SkipsBlankAndMissingIds()
    {
        const string json = """
        { "data": [ { "id": "ok" }, { "id": "   " }, { "object": "model" }, { "id": "" } ] }
        """;

        Assert.Equal(new[] { "ok" }, LlmService.ParseModelIds(json));
    }

    [Fact]
    public void ParseModelIds_TrimsWhitespaceAroundIds()
    {
        const string json = """{ "data": [ { "id": "  gpt-4o-mini  " } ] }""";

        Assert.Equal(new[] { "gpt-4o-mini" }, LlmService.ParseModelIds(json));
    }
}

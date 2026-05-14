using System.Text.Json;
using OpenClaw.Shared;
using Xunit;

namespace OpenClaw.Shared.Tests;

public class JsonElementExtensionsTests
{
    private static JsonElement Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    [Fact]
    public void GetStringOrNull_ReturnsNull_ForMissingWrongTypeOrNonObject()
    {
        var element = Parse("""{"name":"node","count":3}""");

        Assert.Equal("node", element.GetStringOrNull("name"));
        Assert.Null(element.GetStringOrNull("missing"));
        Assert.Null(element.GetStringOrNull("count"));
        Assert.Null(Parse("""["name"]""").GetStringOrNull("name"));
    }

    [Fact]
    public void GetNumbers_ReturnDefaults_ForMissingWrongTypeAndOverflow()
    {
        var element = Parse("""{"small":42,"big":9999999999999,"floating":12.75,"text":"42"}""");

        Assert.Equal(42, element.GetInt32OrDefault("small", 7));
        Assert.Equal(7, element.GetInt32OrDefault("big", 7));
        Assert.Equal(int.MaxValue, element.GetInt32OrDefault("big", 7, clampOverflow: true));
        Assert.Equal(12, element.GetInt64OrDefault("floating", allowDouble: true));
        Assert.Equal(7, element.GetInt32OrDefault("text", 7));
    }

    [Fact]
    public void GetBoolOrDefault_ReturnsDefault_ForMissingOrWrongType()
    {
        var element = Parse("""{"enabled":true,"disabled":false,"text":"true"}""");

        Assert.True(element.GetBoolOrDefault("enabled"));
        Assert.False(element.GetBoolOrDefault("disabled", true));
        Assert.True(element.GetBoolOrDefault("missing", true));
        Assert.False(element.GetBoolOrDefault("text"));
    }

    [Fact]
    public void GetStringArray_FiltersAndOptionallyTrimsValues()
    {
        var element = Parse("""{"items":[" one ","  ","two",3,true,""]}""");

        Assert.Equal(new[] { " one ", "two" }, element.GetStringArray("items", skipEmpty: true));
        Assert.Equal(new[] { "one", "two" }, element.GetStringArray("items", trimValues: true, skipEmpty: true));
        Assert.Empty(element.GetStringArray("missing"));
    }

    [Fact]
    public void GetObject_DeserializesPropertyOrReturnsDefault()
    {
        var element = Parse("""{"item":{"Name":"node"},"bad":"not-object"}""");

        var item = element.GetObject<TestItem>("item");

        Assert.NotNull(item);
        Assert.Equal("node", item.Name);
        Assert.Null(element.GetObject<TestItem>("missing"));
        Assert.Null(element.GetObject<TestItem>("bad"));
    }

    private sealed class TestItem
    {
        public string Name { get; set; } = "";
    }
}

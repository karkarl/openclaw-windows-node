using System.Text.Json;

namespace OpenClaw.Shared;

public static class JsonElementExtensions
{
    public static string? GetStringOrNull(this JsonElement element, string property)
    {
        if (element.ValueKind != JsonValueKind.Object)
            return null;

        return element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
    }

    public static string GetStringOrDefault(this JsonElement element, string property, string @default = "")
        => element.GetStringOrNull(property) ?? @default;

    public static int? GetInt32OrNull(this JsonElement element, string property)
    {
        if (element.ValueKind != JsonValueKind.Object)
            return null;

        return element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var result)
            ? result
            : null;
    }

    public static int GetInt32OrDefault(this JsonElement element, string property, int @default = 0, bool clampOverflow = false)
    {
        var value = element.GetInt32OrNull(property);
        if (value.HasValue)
            return value.Value;

        if (clampOverflow &&
            element.ValueKind == JsonValueKind.Object &&
            element.TryGetProperty(property, out var jsonValue) &&
            jsonValue.ValueKind == JsonValueKind.Number &&
            jsonValue.TryGetInt64(out var longValue))
        {
            return (int)Math.Clamp(longValue, int.MinValue, int.MaxValue);
        }

        return @default;
    }

    public static long? GetInt64OrNull(this JsonElement element, string property)
    {
        if (element.ValueKind != JsonValueKind.Object)
            return null;

        return element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var result)
            ? result
            : null;
    }

    public static long GetInt64OrDefault(this JsonElement element, string property, long @default = 0, bool allowDouble = false)
    {
        var value = element.GetInt64OrNull(property);
        if (value.HasValue)
            return value.Value;

        if (allowDouble &&
            element.ValueKind == JsonValueKind.Object &&
            element.TryGetProperty(property, out var jsonValue) &&
            jsonValue.ValueKind == JsonValueKind.Number &&
            jsonValue.TryGetDouble(out var doubleValue))
        {
            return (long)doubleValue;
        }

        return @default;
    }

    public static double? GetDoubleOrNull(this JsonElement element, string property)
    {
        if (element.ValueKind != JsonValueKind.Object)
            return null;

        return element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var result)
            ? result
            : null;
    }

    public static double GetDoubleOrDefault(this JsonElement element, string property, double @default = 0)
        => element.GetDoubleOrNull(property) ?? @default;

    public static bool? GetBoolOrNull(this JsonElement element, string property)
    {
        if (element.ValueKind != JsonValueKind.Object)
            return null;

        if (!element.TryGetProperty(property, out var value))
            return null;

        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => null
        };
    }

    public static bool GetBoolOrDefault(this JsonElement element, string property, bool @default = false)
        => element.GetBoolOrNull(property) ?? @default;

    public static T? GetObject<T>(this JsonElement element, string property, JsonSerializerOptions? opts = null)
    {
        if (element.ValueKind != JsonValueKind.Object)
            return default;

        if (!element.TryGetProperty(property, out var value))
            return default;

        try
        {
            return value.Deserialize<T>(opts);
        }
        catch (JsonException)
        {
            return default;
        }
        catch (NotSupportedException)
        {
            return default;
        }
    }

    public static string[] GetStringArray(this JsonElement element, string property, bool trimValues = false, bool skipEmpty = false)
    {
        if (element.ValueKind != JsonValueKind.Object)
            return Array.Empty<string>();

        if (!element.TryGetProperty(property, out var value) || value.ValueKind != JsonValueKind.Array)
            return Array.Empty<string>();

        var buffer = new string[value.GetArrayLength()];
        var count = 0;
        foreach (var item in value.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String)
                continue;

            var itemValue = item.GetString() ?? "";
            if (trimValues)
                itemValue = itemValue.Trim();
            if (skipEmpty && string.IsNullOrWhiteSpace(itemValue))
                continue;

            buffer[count++] = itemValue;
        }

        return count > 0 ? buffer[..count] : [];
    }
}

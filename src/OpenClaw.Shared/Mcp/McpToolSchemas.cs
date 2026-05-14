using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace OpenClaw.Shared.Mcp;

public partial class McpToolBridge
{
    private static readonly InputSchema UnknownInputSchema = S(
        null,
        P("arguments", "object", "Capability-specific arguments for commands without an inline schema.", additionalProperties: true));

    private static readonly Dictionary<string, InputSchema> CommandInputSchemas = new(StringComparer.Ordinal)
    {
        ["system.notify"] = S(null,
            P("title", "string", "Toast title.", "OpenClaw"),
            P("body", "string", "Toast body text.", ""),
            P("subtitle", "string", "Optional toast subtitle."),
            P("sound", "boolean", "Whether to play the default notification sound.", true)),
        ["system.run"] = S(new[] { "command" },
            P("command", new[] { "string", "array" }, "Command to execute, either a shell command string or argv array.", items: P("item", "string")),
            P("args", "array", "Additional argv items when command is a string.", items: P("item", "string")),
            P("shell", "string", "Shell override."),
            P("cwd", "string", "Working directory."),
            P("timeoutMs", "integer", "Execution timeout in milliseconds.", 30000, minimum: 1, maximum: 600000),
            P("timeout", "integer", "Legacy execution timeout in milliseconds.", 30000, minimum: 1, maximum: 600000),
            P("env", "object", "Environment variable overrides.", additionalProperties: true)),
        ["system.run.prepare"] = S(new[] { "command" },
            P("command", new[] { "string", "array" }, "Command to prepare, either a shell command string or argv array.", items: P("item", "string")),
            P("rawCommand", "string", "Raw command text for approval display."),
            P("cwd", "string", "Working directory."),
            P("agentId", "string", "Calling agent id."),
            P("sessionKey", "string", "Calling session key.")),
        ["system.which"] = S(new[] { "bins" },
            P("bins", "array", "Executable names to resolve on PATH.", items: P("item", "string"), minItems: 1)),
        ["system.execApprovals.get"] = S(),
        ["system.execApprovals.set"] = S(null,
            P("baseHash", "string", "Current policy hash from system.execApprovals.get; baseHash or base_hash is required."),
            P("base_hash", "string", "Snake-case alias for baseHash."),
            P("rules", "array", "Replacement approval rules.", items: P("rule", "object", properties: new[]
            {
                P("pattern", "string", "Command pattern."),
                P("action", "string", "Rule action.", enumValues: new[] { "allow", "deny", "prompt" }),
                P("shells", "array", "Optional shell names.", items: P("item", "string")),
                P("description", "string", "Operator-facing rule description."),
                P("enabled", "boolean", "Whether the rule is active.", true),
            })),
            P("defaultAction", "string", "Default approval action.", enumValues: new[] { "deny", "prompt" })),

        ["canvas.present"] = S(null,
            P("url", "string", "URL to show in the Canvas."),
            P("html", "string", "Inline HTML to show in the Canvas."),
            P("width", "integer", "Canvas width in pixels.", 800, minimum: 100, maximum: 7680),
            P("height", "integer", "Canvas height in pixels.", 600, minimum: 100, maximum: 7680),
            P("x", "integer", "Canvas X position; -1 centers.", -1, minimum: -16384, maximum: 16384),
            P("y", "integer", "Canvas Y position; -1 centers.", -1, minimum: -16384, maximum: 16384),
            P("title", "string", "Canvas window title.", "Canvas"),
            P("alwaysOnTop", "boolean", "Keep the Canvas above other windows.", false)),
        ["canvas.hide"] = S(),
        ["canvas.navigate"] = S(new[] { "url" },
            P("url", "string", "HTTP(S) URL to navigate to.")),
        ["canvas.eval"] = S(null,
            P("script", "string", "JavaScript source to evaluate."),
            P("javaScript", "string", "Alias for script."),
            P("javascript", "string", "Alias for script.")),
        ["canvas.snapshot"] = S(null,
            P("format", "string", "Image format.", "png", enumValues: new[] { "png", "jpeg" }),
            P("maxWidth", "integer", "Maximum output width.", 1200, minimum: 32, maximum: 7680),
            P("quality", "integer", "JPEG quality.", 80, minimum: 1, maximum: 100)),
        ["canvas.a2ui.push"] = S(null,
            P("jsonl", "string", "A2UI JSONL payload."),
            P("jsonlPath", "string", "Path to a temp-directory JSONL payload."),
            P("props", "object", "Optional A2UI props.", additionalProperties: true)),
        ["canvas.a2ui.pushJSONL"] = S(null,
            P("jsonl", "string", "A2UI JSONL payload."),
            P("jsonlPath", "string", "Path to a temp-directory JSONL payload."),
            P("props", "object", "Optional A2UI props.", additionalProperties: true)),
        ["canvas.a2ui.reset"] = S(),
        ["canvas.a2ui.dump"] = S(),
        ["canvas.caps"] = S(),

        ["screen.snapshot"] = S(null,
            P("format", "string", "Image format.", "png", enumValues: new[] { "png", "jpeg" }),
            P("maxWidth", "integer", "Maximum output width.", 1920, minimum: 16, maximum: 7680),
            P("quality", "integer", "JPEG quality.", 80, minimum: 1, maximum: 100),
            P("monitor", "integer", "Display index alias used as screenIndex default.", 0, minimum: 0, maximum: 32),
            P("screenIndex", "integer", "Display index to capture.", 0, minimum: 0, maximum: 32),
            P("includePointer", "boolean", "Include the mouse pointer.", true)),
        ["screen.record"] = S(null,
            P("durationMs", "integer", "Recording duration in milliseconds.", 10000, minimum: 100, maximum: 300000),
            P("format", "string", "Recording format.", "mp4", enumValues: new[] { "mp4" }),
            P("screenIndex", "integer", "Display index to record.", 0, minimum: 0, maximum: 32),
            P("fps", "number", "Frames per second.", 10, minimum: 1, maximum: 60),
            P("includeAudio", "boolean", "Include system audio when supported.", false)),

        ["camera.list"] = S(),
        ["camera.snap"] = S(null,
            P("deviceId", "string", "Camera device id; omit for default camera."),
            P("format", "string", "Image format.", "jpeg", enumValues: new[] { "jpeg", "png" }),
            P("maxWidth", "integer", "Maximum output width.", 1280, minimum: 16, maximum: 4096),
            P("quality", "integer", "JPEG quality.", 80, minimum: 1, maximum: 100)),
        ["camera.clip"] = S(null,
            P("deviceId", "string", "Camera device id; omit for default camera."),
            P("durationMs", "integer", "Clip duration in milliseconds.", 3000, minimum: 100, maximum: 60000),
            P("includeAudio", "boolean", "Include microphone audio.", true),
            P("format", "string", "Clip format.", "mp4", enumValues: new[] { "mp4", "webm" })),

        ["stt.transcribe"] = S(new[] { "maxDurationMs" },
            P("maxDurationMs", "integer", "Microphone capture duration in milliseconds.", minimum: 1, maximum: 30000),
            P("language", "string", "BCP-47 language tag or auto.")),
        ["stt.listen"] = S(null,
            P("timeoutMs", "integer", "Maximum listening duration in milliseconds.", 30000, minimum: 1000, maximum: 120000),
            P("language", "string", "BCP-47 language tag or auto.", "auto")),
        ["stt.status"] = S(),

        ["tts.speak"] = S(new[] { "text" },
            P("text", "string", "Text to speak.", maxLength: 5000, minLength: 1),
            P("provider", "string", "Speech provider.", enumValues: new[] { "piper", "windows", "elevenlabs" }),
            P("voiceId", "string", "Provider-specific voice id."),
            P("model", "string", "Provider-specific model id."),
            P("interrupt", "boolean", "Interrupt any in-progress playback.", false)),

        ["app.navigate"] = S(new[] { "page" },
            P("page", "string", "Application page name to navigate to.")),
        ["app.status"] = S(),
        ["app.sessions"] = S(null,
            P("agentId", "string", "Optional agent id filter.")),
        ["app.agents"] = S(),
        ["app.nodes"] = S(),
        ["app.config.get"] = S(null,
            P("path", "string", "Optional gateway config dot-path.")),
        ["app.settings.get"] = S(new[] { "name" },
            P("name", "string", "Setting name to read.")),
        ["app.settings.set"] = S(new[] { "name", "value" },
            P("name", "string", "Setting name to write."),
            P("value", "string", "Setting value to write.")),
        ["app.menu"] = S(),
        ["app.search"] = S(new[] { "query" },
            P("query", "string", "Command palette search query.")),

        ["device.info"] = S(),
        ["device.status"] = S(null,
            P("sections", "array", "Status sections to include; omit for all sections.", items: P("section", "string", enumValues: new[] { "os", "cpu", "memory", "disk", "battery" }))),

        ["location.get"] = S(null,
            P("accuracy", "string", "Requested location accuracy.", "default"),
            P("maxAge", "integer", "Maximum cached location age in milliseconds.", 30000, minimum: 0),
            P("locationTimeout", "integer", "Location request timeout in milliseconds.", 10000, minimum: 1)),

        ["browser.proxy"] = S(new[] { "path" },
            P("method", "string", "HTTP method for the browser-control request.", "GET", enumValues: new[] { "GET", "POST", "DELETE" }),
            P("path", "string", "Local browser-control path, not a full URL."),
            P("timeoutMs", "integer", "Proxy timeout in milliseconds.", 20000, minimum: 1, maximum: 120000),
            P("query", "object", "Query-string parameters.", additionalProperties: true),
            P("profile", "string", "Browser profile selector."),
            P("body", new[] { "object", "array", "string", "number", "boolean" }, "JSON request body for POST/DELETE.")),
    };

    private static object GetInputSchema(string command)
        => (CommandInputSchemas.TryGetValue(command, out var schema) ? schema : UnknownInputSchema).ToJsonObject();

    private static bool IsInputValidationEnabled()
        => string.Equals(Environment.GetEnvironmentVariable("OPENCLAW_MCP_VALIDATE_INPUT"), "1", StringComparison.Ordinal);

    private static void ValidateToolArguments(string command, JsonElement args)
    {
        if (!CommandInputSchemas.TryGetValue(command, out var schema))
            return;

        if (!schema.TryValidate(args, out var error))
            throw new McpToolException(error);
    }

    private static InputSchema S(string[]? required = null, params PropertySpec[] properties)
        => new(required ?? Array.Empty<string>(), properties);

    private static PropertySpec P(
        string name,
        string type,
        string? description = null,
        object? defaultValue = null,
        string[]? enumValues = null,
        double? minimum = null,
        double? maximum = null,
        int? minLength = null,
        int? maxLength = null,
        int? minItems = null,
        PropertySpec? items = null,
        IReadOnlyList<PropertySpec>? properties = null,
        bool additionalProperties = false)
        => P(name, new[] { type }, description, defaultValue, enumValues, minimum, maximum, minLength, maxLength, minItems, items, properties, additionalProperties);

    private static PropertySpec P(
        string name,
        string[] types,
        string? description = null,
        object? defaultValue = null,
        string[]? enumValues = null,
        double? minimum = null,
        double? maximum = null,
        int? minLength = null,
        int? maxLength = null,
        int? minItems = null,
        PropertySpec? items = null,
        IReadOnlyList<PropertySpec>? properties = null,
        bool additionalProperties = false)
        => new(name, types)
        {
            Description = description,
            DefaultValue = defaultValue,
            EnumValues = enumValues,
            Minimum = minimum,
            Maximum = maximum,
            MinLength = minLength,
            MaxLength = maxLength,
            MinItems = minItems,
            Items = items,
            Properties = properties,
            AdditionalProperties = additionalProperties,
        };

    private sealed class InputSchema
    {
        private readonly Dictionary<string, PropertySpec> _properties;
        private readonly HashSet<string> _required;

        public InputSchema(IReadOnlyList<string> required, IReadOnlyList<PropertySpec> properties)
        {
            _required = new HashSet<string>(required, StringComparer.Ordinal);
            _properties = new Dictionary<string, PropertySpec>(StringComparer.Ordinal);
            foreach (var property in properties)
                _properties[property.Name] = property;
        }

        public object ToJsonObject()
        {
            var properties = new Dictionary<string, object>(StringComparer.Ordinal);
            foreach (var property in _properties)
                properties[property.Key] = property.Value.ToJsonObject();

            var schema = new Dictionary<string, object>(StringComparer.Ordinal)
            {
                ["type"] = "object",
                ["additionalProperties"] = false,
                ["properties"] = properties,
            };

            if (_required.Count > 0)
                schema["required"] = _required.ToArray();

            return schema;
        }

        public bool TryValidate(JsonElement args, out string error)
        {
            error = "";
            if (args.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
            {
                if (_required.Count == 0)
                    return true;

                error = $"Missing required argument: {_required.First()}";
                return false;
            }

            if (args.ValueKind != JsonValueKind.Object)
            {
                error = "'arguments' must be a JSON object if present";
                return false;
            }

            foreach (var required in _required)
            {
                if (!args.TryGetProperty(required, out var value) || value.ValueKind == JsonValueKind.Null)
                {
                    error = $"Missing required argument: {required}";
                    return false;
                }
            }

            foreach (var arg in args.EnumerateObject())
            {
                if (!_properties.TryGetValue(arg.Name, out var property))
                {
                    error = $"Unexpected argument for tool: {arg.Name}";
                    return false;
                }

                if (!property.TryValidate(arg.Value, arg.Name, out error))
                    return false;
            }

            return true;
        }
    }

    private sealed class PropertySpec
    {
        public PropertySpec(string name, string[] types)
        {
            Name = name;
            Types = types;
        }

        public string Name { get; }
        public string[] Types { get; }
        public string? Description { get; init; }
        public object? DefaultValue { get; init; }
        public string[]? EnumValues { get; init; }
        public double? Minimum { get; init; }
        public double? Maximum { get; init; }
        public int? MinLength { get; init; }
        public int? MaxLength { get; init; }
        public int? MinItems { get; init; }
        public PropertySpec? Items { get; init; }
        public IReadOnlyList<PropertySpec>? Properties { get; init; }
        public bool AdditionalProperties { get; init; }

        public object ToJsonObject()
        {
            var schema = new Dictionary<string, object>(StringComparer.Ordinal)
            {
                ["type"] = Types.Length == 1 ? Types[0] : Types,
            };

            if (!string.IsNullOrWhiteSpace(Description))
                schema["description"] = Description;
            if (DefaultValue != null)
                schema["default"] = DefaultValue;
            if (EnumValues is { Length: > 0 })
                schema["enum"] = EnumValues;
            if (Minimum.HasValue)
                schema["minimum"] = Minimum.Value;
            if (Maximum.HasValue)
                schema["maximum"] = Maximum.Value;
            if (MinLength.HasValue)
                schema["minLength"] = MinLength.Value;
            if (MaxLength.HasValue)
                schema["maxLength"] = MaxLength.Value;
            if (MinItems.HasValue)
                schema["minItems"] = MinItems.Value;
            if (Items != null)
                schema["items"] = Items.ToJsonObject();

            if (Properties is { Count: > 0 })
            {
                var nested = new Dictionary<string, object>(StringComparer.Ordinal);
                foreach (var property in Properties)
                    nested[property.Name] = property.ToJsonObject();
                schema["properties"] = nested;
                schema["additionalProperties"] = AdditionalProperties;
            }
            else if (HasType("object"))
            {
                schema["additionalProperties"] = AdditionalProperties;
            }

            return schema;
        }

        public bool TryValidate(JsonElement value, string path, out string error)
        {
            error = "";
            if (!MatchesAnyType(value))
            {
                error = $"Invalid argument '{path}': expected {FormatTypes()}";
                return false;
            }

            if (EnumValues is { Length: > 0 } && value.ValueKind == JsonValueKind.String)
            {
                var actual = value.GetString();
                var found = false;
                foreach (var allowed in EnumValues)
                {
                    if (string.Equals(actual, allowed, StringComparison.OrdinalIgnoreCase))
                    {
                        found = true;
                        break;
                    }
                }

                if (!found)
                {
                    error = $"Invalid argument '{path}': expected one of {string.Join(", ", EnumValues)}";
                    return false;
                }
            }

            if (value.ValueKind == JsonValueKind.Number && (Minimum.HasValue || Maximum.HasValue))
            {
                var number = value.GetDouble();
                if (Minimum.HasValue && number < Minimum.Value)
                {
                    error = $"Invalid argument '{path}': must be >= {Minimum.Value}";
                    return false;
                }
                if (Maximum.HasValue && number > Maximum.Value)
                {
                    error = $"Invalid argument '{path}': must be <= {Maximum.Value}";
                    return false;
                }
            }

            if (value.ValueKind == JsonValueKind.String && (MinLength.HasValue || MaxLength.HasValue))
            {
                var length = value.GetString()?.Length ?? 0;
                if (MinLength.HasValue && length < MinLength.Value)
                {
                    error = $"Invalid argument '{path}': length must be >= {MinLength.Value}";
                    return false;
                }
                if (MaxLength.HasValue && length > MaxLength.Value)
                {
                    error = $"Invalid argument '{path}': length must be <= {MaxLength.Value}";
                    return false;
                }
            }

            if (value.ValueKind == JsonValueKind.Array)
            {
                if (MinItems.HasValue && value.GetArrayLength() < MinItems.Value)
                {
                    error = $"Invalid argument '{path}': array must contain at least {MinItems.Value} item(s)";
                    return false;
                }

                if (Items != null)
                {
                    var index = 0;
                    foreach (var item in value.EnumerateArray())
                    {
                        if (!Items.TryValidate(item, $"{path}[{index}]", out error))
                            return false;
                        index++;
                    }
                }
            }

            if (value.ValueKind == JsonValueKind.Object && Properties is { Count: > 0 })
            {
                var nested = new Dictionary<string, PropertySpec>(StringComparer.Ordinal);
                foreach (var property in Properties)
                    nested[property.Name] = property;

                foreach (var property in value.EnumerateObject())
                {
                    if (!nested.TryGetValue(property.Name, out var spec))
                    {
                        if (AdditionalProperties)
                            continue;

                        error = $"Unexpected argument for '{path}': {property.Name}";
                        return false;
                    }

                    if (!spec.TryValidate(property.Value, $"{path}.{property.Name}", out error))
                        return false;
                }
            }

            return true;
        }

        private bool MatchesAnyType(JsonElement value)
        {
            foreach (var type in Types)
            {
                if (type switch
                    {
                        "array" => value.ValueKind == JsonValueKind.Array,
                        "boolean" => value.ValueKind is JsonValueKind.True or JsonValueKind.False,
                        "integer" => value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out _),
                        "number" => value.ValueKind == JsonValueKind.Number,
                        "object" => value.ValueKind == JsonValueKind.Object,
                        "string" => value.ValueKind == JsonValueKind.String,
                        _ => false
                    })
                {
                    return true;
                }
            }

            return false;
        }

        private bool HasType(string type)
        {
            foreach (var candidate in Types)
            {
                if (string.Equals(candidate, type, StringComparison.Ordinal))
                    return true;
            }

            return false;
        }

        private string FormatTypes()
            => Types.Length == 1 ? Types[0] : string.Join(" or ", Types);
    }
}

using System.IO;
using System.Text;
using System.Text.Json;
using Disorderly.Commands;
using Disorderly.Routing;

namespace Disorderly.Configuration;
/// <summary>Strict bounded v1 mapping, without CLR type activation, filesystem lookup or domain-rule duplication.</summary>
public static class ConfigurationCodec
{
    public const int MaximumBytes = 1024 * 1024;
    public const int MaximumDepth = 32;
    public static ConfigurationDecodeResult Decode(ReadOnlyMemory<byte> utf8)
    {
        if (utf8.Length > MaximumBytes)
        {
            return new(null, [new("DocumentTooLarge", "$", "Configuration exceeds the 1 MiB limit.")]);
        }

        try
        {
            if (utf8.Span.StartsWith(new byte[] { 0xEF, 0xBB, 0xBF }))
            {
                utf8 = utf8[3..];
            }

            new UTF8Encoding(false, true).GetString(utf8.Span); // Reject malformed UTF-8 rather than replacing user text.
            using JsonDocument json = JsonDocument.Parse(utf8, new JsonDocumentOptions { MaxDepth = MaximumDepth });
            JsonElement top = json.RootElement;
            CheckDuplicates(top, "$");
            if (top.ValueKind != JsonValueKind.Object)
            {
                throw Error("WrongKind", "$", "Expected an object.");
            }

            if (!top.TryGetProperty("version", out JsonElement version))
            {
                throw Error("MissingField", "$.version", "Version is required.");
            }

            if (version.ValueKind != JsonValueKind.Number || !version.TryGetInt32(out int number))
            {
                throw Error("InvalidVersion", "$.version", "Version must be an integer.");
            }

            if (number != 1)
            {
                return new(null, [new("UnsupportedVersion", "$.version", "Only configuration version 1 is supported; no migration was attempted.")], true);
            }

            Object(top, "$", ["version", "rootContextId", "contexts", "commands"], ["version", "rootContextId", "contexts", "commands"]);
            List<ContextDefinition> contexts = [];
            int index = 0;
            foreach (JsonElement element in Array(top.GetProperty("contexts"), "$.contexts", 512))
            {
                string path = $"$.contexts[{index++}]";
                Object(element, path, ["id", "displayName", "bindings"], ["id", "displayName", "bindings"]);
                string id = Text(element.GetProperty("id"), path + ".id");
                List<BindingDefinition> bindings = [];
                int bindingIndex = 0;
                foreach (JsonElement binding in Array(element.GetProperty("bindings"), path + ".bindings"))
                {
                    string bindingPath = $"{path}.bindings[{bindingIndex++}]";
                    Object(binding, bindingPath, ["id", "gesture", "destination"], ["id", "gesture", "destination"]);
                    bindings.Add(new(Text(binding.GetProperty("id"), bindingPath + ".id"), id, Gesture(binding.GetProperty("gesture"), bindingPath + ".gesture"), Destination(binding.GetProperty("destination"), bindingPath + ".destination")));
                }

                contexts.Add(new(id, Text(element.GetProperty("displayName"), path + ".displayName"), bindings.ToArray()));
            }

            List<CommandDefinition> commands = [];
            index = 0;
            foreach (JsonElement command in Array(top.GetProperty("commands"), "$.commands", 1024))
            {
                string path = $"$.commands[{index++}]";
                Object(command, path, ["id", "displayName", "description", "parameters", "defaultCompletionPolicy", "icon", "execution"], ["id", "displayName", "parameters"]);
                List<ParameterDefinition> parameters = [];
                int parameterIndex = 0;
                foreach (JsonElement parameter in Array(command.GetProperty("parameters"), path + ".parameters", 128))
                {
                    string parameterPath = $"{path}.parameters[{parameterIndex++}]";
                    Object(parameter, parameterPath, ["id", "name", "type", "required", "description"], ["id", "name", "type"]);
                    parameters.Add(new(Text(parameter.GetProperty("id"), parameterPath + ".id"), Text(parameter.GetProperty("name"), parameterPath + ".name"), ValueType(Text(parameter.GetProperty("type"), parameterPath + ".type"), parameterPath + ".type"), parameter.TryGetProperty("required", out JsonElement required) && Boolean(required, parameterPath + ".required"), OptionalText(parameter, "description", parameterPath)));
                }

                commands.Add(new(Text(command.GetProperty("id"), path + ".id"), Text(command.GetProperty("displayName"), path + ".displayName"), parameters.ToArray(), OptionalText(command, "description", path), Policy(command, "defaultCompletionPolicy", path), Present(command, "icon", out JsonElement icon) ? Icon(icon, path + ".icon") : null, Present(command, "execution", out JsonElement execution) ? Execution(execution, path + ".execution") : null));
            }

            return new(new(1, new(Text(top.GetProperty("rootContextId"), "$.rootContextId"), contexts.ToArray()), commands.ToArray()), []);
        }
        catch (ConfigurationDocumentException error)
        {
            return new(null, [new(error.Code, error.Location, error.Message)]);
        }
        catch (JsonException error)
        {
            return new(null, [new("InvalidJson", "$", $"Invalid JSON or excessive nesting near line {error.LineNumber}, byte {error.BytePositionInLine}.")]);
        }
        catch (InvalidOperationException)
        {
            return new(null, [new("InvalidUnicode", "$", "Invalid Unicode in a JSON string or property name.")]);
        }
        catch (DecoderFallbackException)
        {
            return new(null, [new("InvalidUtf8", "$", "Configuration must use valid UTF-8.")]);
        }
    }

    private static void Object(JsonElement element, string path, string[] allowed, string[] required)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            throw Error("WrongKind", path, "Expected an object.");
        }

        HashSet<string> seen = new(StringComparer.Ordinal);
        foreach (JsonProperty property in element.EnumerateObject())
        {
            if (!seen.Add(property.Name))
            {
                throw Error("DuplicateProperty", path, "Duplicate JSON properties are not allowed.");
            }

            if (!allowed.Contains(property.Name, StringComparer.Ordinal))
            {
                throw Error("UnknownField", path, "Unrecognized fields are not supported in version 1.");
            }
        }

        foreach (string name in required)
        {
            if (!seen.Contains(name))
            {
                throw Error("MissingField", path + "." + name, "Required field is missing.");
            }
        }
    }

    private static void CheckDuplicates(JsonElement value, string path)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            HashSet<string> names = new(StringComparer.Ordinal);
            foreach (JsonProperty property in value.EnumerateObject())
            {
                if (!names.Add(property.Name))
                {
                    throw Error("DuplicateProperty", path, "Duplicate JSON properties are not allowed.");
                }

                CheckDuplicates(property.Value, path + "." + property.Name);
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
        {
            int index = 0;
            foreach (JsonElement item in value.EnumerateArray())
            {
                CheckDuplicates(item, $"{path}[{index++}]");
            }
        }
    }

    private static JsonElement.ArrayEnumerator Array(JsonElement value, string path, int limit = 4096)
    {
        if (value.ValueKind != JsonValueKind.Array)
        {
            throw Error("WrongKind", path, "Expected an array.");
        }

        if (value.GetArrayLength() > limit)
        {
            throw Error("CollectionTooLarge", path, "This collection exceeds the supported configuration limit.");
        }

        return value.EnumerateArray();
    }

    private static string Text(JsonElement value, string path) => value.ValueKind == JsonValueKind.String ? value.GetString()! : throw Error("WrongKind", path, "Expected a non-null string.");
    private static bool Boolean(JsonElement value, string path) => value.ValueKind is JsonValueKind.True or JsonValueKind.False ? value.GetBoolean() : throw Error("WrongKind", path, "Expected a Boolean.");
    private static bool Present(JsonElement parent, string name, out JsonElement value) => parent.TryGetProperty(name, out value) && value.ValueKind != JsonValueKind.Null;
    private static string? OptionalText(JsonElement parent, string name, string path) => Present(parent, name, out JsonElement value) ? Text(value, path + "." + name) : null;
    private static ConfigurationDocumentException Error(string code, string path, string message) => new(code, path, message);
    private static LogicalGesture Gesture(JsonElement value, string path)
    {
        Object(value, path, ["key", "modifiers"], ["key", "modifiers"]);
        GestureModifiers modifiers = GestureModifiers.None;
        foreach (JsonElement modifier in Array(value.GetProperty("modifiers"), path + ".modifiers", 4))
        {
            GestureModifiers flag = Text(modifier, path + ".modifiers") switch
            {
                "Control" => GestureModifiers.Control,
                "Shift" => GestureModifiers.Shift,
                "Alt" => GestureModifiers.Alt,
                "Meta" => GestureModifiers.Meta,
                _ => throw Error("InvalidModifier", path + ".modifiers", "Expected Control, Shift, Alt or Meta."),
            };
            if ((modifiers & flag) != 0)
            {
                throw Error("DuplicateModifier", path + ".modifiers", "A modifier may appear only once.");
            }

            modifiers |= flag;
        }

        LogicalGesture gesture = new(Text(value.GetProperty("key"), path + ".key"), modifiers);
        return gesture.IsValid ? gesture : throw Error("InvalidGesture", path, "Expected a supported logical key and modifiers.");
    }

    private static BindingDestination Destination(JsonElement value, string path)
    {
        string kind = Kind(value, path, "kind");
        if (kind == "context")
        {
            Object(value, path, ["kind", "contextId"], ["kind", "contextId"]);
            return new BindingDestination.Context(Text(value.GetProperty("contextId"), path + ".contextId"));
        }

        if (kind != "command")
        {
            throw Error("UnknownDestination", path + ".kind", "Expected context or command.");
        }

        Object(value, path, ["kind", "commandId", "presets", "completionOverride"], ["kind", "commandId"]);
        ArgumentSet? presets = null;
        if (value.TryGetProperty("presets", out JsonElement assignments))
        {
            List<ArgumentAssignment> values = [];
            int index = 0;
            foreach (JsonElement assignment in Array(assignments, path + ".presets", 128))
            {
                string itemPath = $"{path}.presets[{index++}]";
                Object(assignment, itemPath, ["parameterId", "type", "value"], ["parameterId", "type", "value"]);
                ParameterValueType type = ValueType(Text(assignment.GetProperty("type"), itemPath + ".type"), itemPath + ".type");
                JsonElement raw = assignment.GetProperty("value");
                ArgumentValue typed = type switch
                {
                    ParameterValueType.Text => new ArgumentValue.Text(Text(raw, itemPath + ".value")),
                    ParameterValueType.Path => new ArgumentValue.Path(Text(raw, itemPath + ".value")),
                    ParameterValueType.Boolean => new ArgumentValue.Boolean(Boolean(raw, itemPath + ".value")),
                    ParameterValueType.Integer => new ArgumentValue.Integer(raw.ValueKind == JsonValueKind.Number && raw.TryGetInt64(out long number) ? number : throw Error("InvalidInteger", itemPath + ".value", "Expected a signed 64-bit integer without coercion.")),
                    _ => throw Error("UnknownType", itemPath, "Unsupported argument type."),
                };
                values.Add(new(Text(assignment.GetProperty("parameterId"), itemPath + ".parameterId"), typed));
            }

            presets = new(values);
        }

        CompletionPolicy? policy = Policy(value, "completionOverride", path);
        return new BindingDestination.Command(Text(value.GetProperty("commandId"), path + ".commandId"))
        {
            Metadata = presets is null && policy is null ? null : new(presets, policy),
        };
    }

    private static string Kind(JsonElement value, string path, string name)
    {
        if (value.ValueKind != JsonValueKind.Object || !value.TryGetProperty(name, out JsonElement kind))
        {
            throw Error("MissingDiscriminator", path, "A typed discriminator is required.");
        }

        return Text(kind, path + "." + name);
    }

    private static ParameterValueType ValueType(string value, string path) => value switch
    {
        "text" => ParameterValueType.Text,
        "boolean" => ParameterValueType.Boolean,
        "integer" => ParameterValueType.Integer,
        "path" => ParameterValueType.Path,
        _ => throw Error("UnknownType", path, "Expected text, boolean, integer or path."),
    };
    private static CompletionPolicy? Policy(JsonElement value, string name, string path) => !Present(value, name, out JsonElement policy) ? null : Text(policy, path + "." + name) switch
    {
        "dismiss" => CompletionPolicy.Dismiss,
        "stay-open" => CompletionPolicy.StayOpen,
        _ => throw Error("InvalidPolicy", path + "." + name, "Expected dismiss or stay-open."),
    };
    private static CommandIcon Icon(JsonElement value, string path)
    {
        switch (Kind(value, path, "kind"))
        {
            case "automatic":
                Object(value, path, ["kind"], ["kind"]);
                return new CommandIcon.Automatic();
            case "custom-file":
                Object(value, path, ["kind", "path"], ["kind", "path"]);
                return new CommandIcon.CustomFile(Text(value.GetProperty("path"), path + ".path"));
            case "built-in":
                Object(value, path, ["kind", "id"], ["kind", "id"]);
                return new CommandIcon.BuiltIn(Text(value.GetProperty("id"), path + ".id"));
            default:
                throw Error("UnknownIcon", path + ".kind", "Expected automatic, custom-file or built-in.");
        }
    }

    private static CommandExecutionSpecification Execution(JsonElement value, string path)
    {
        if (Kind(value, path, "kind") != "launch-executable")
        {
            throw Error("UnknownExecution", path + ".kind", "Only launch-executable is supported.");
        }

        Object(value, path, ["kind", "executableParameterId"], ["kind", "executableParameterId"]);
        return new CommandExecutionSpecification.LaunchExecutable(Text(value.GetProperty("executableParameterId"), path + ".executableParameterId"));
    }

    public static byte[] Encode(ConfigurationDocument document)
    {
        if (document.Version != 1)
        {
            throw Error("UnsupportedVersion", "$.version", "Only version 1 can be written.");
        }

        using MemoryStream stream = new();
        using (Utf8JsonWriter writer = new(stream, new JsonWriterOptions { Indented = true, MaxDepth = MaximumDepth }))
        {
            writer.WriteStartObject();
            writer.WriteNumber("version", 1);
            WriteString(writer, "rootContextId", document.Routing.RootContextId);
            writer.WriteStartArray("contexts");
            foreach (ContextDefinition context in document.Routing.Contexts.OrderBy(context => context.Id, StringComparer.Ordinal))
            {
                writer.WriteStartObject();
                WriteString(writer, "id", context.Id);
                WriteString(writer, "displayName", context.DisplayName);
                writer.WriteStartArray("bindings");
                foreach (BindingDefinition binding in context.Bindings.OrderBy(binding => binding.Gesture.ToString(), StringComparer.Ordinal).ThenBy(binding => binding.Id, StringComparer.Ordinal))
                {
                    if (binding.ContextId != context.Id || !binding.Gesture.IsValid)
                    {
                        throw Error("UnrepresentableBinding", "$.contexts", "Binding ownership and gesture must be valid before serialization.");
                    }

                    writer.WriteStartObject();
                    WriteString(writer, "id", binding.Id);
                    writer.WriteStartObject("gesture");
                    WriteString(writer, "key", binding.Gesture.Key);
                    writer.WriteStartArray("modifiers");
                    foreach (GestureModifiers modifier in new[]
                    {
                        GestureModifiers.Control,
                        GestureModifiers.Shift,
                        GestureModifiers.Alt,
                        GestureModifiers.Meta
                    }

                    )
                    {
                        if (binding.Gesture.Modifiers.HasFlag(modifier))
                        {
                            writer.WriteStringValue(modifier.ToString());
                        }
                    }

                    writer.WriteEndArray();
                    writer.WriteEndObject();
                    writer.WriteStartObject("destination");
                    switch (binding.Destination)
                    {
                        case BindingDestination.Context target:
                            WriteString(writer, "kind", "context");
                            WriteString(writer, "contextId", target.ContextId);
                            break;
                        case BindingDestination.Command target:
                            WriteString(writer, "kind", "command");
                            WriteString(writer, "commandId", target.CommandId);
                            if (target.Metadata?.Presets is ArgumentSet presets)
                            {
                                if (!presets.IsWellFormed)
                                {
                                    throw Error("InvalidPresets", "$.contexts", "Malformed presets cannot be serialized.");
                                }

                                writer.WriteStartArray("presets");
                                foreach (ArgumentAssignment assignment in presets.Assignments)
                                {
                                    writer.WriteStartObject();
                                    WriteString(writer, "parameterId", assignment.ParameterId);
                                    switch (assignment.Value)
                                    {
                                        case ArgumentValue.Text text:
                                            WriteString(writer, "type", "text");
                                            WriteString(writer, "value", text.Value);
                                            break;
                                        case ArgumentValue.Path path:
                                            WriteString(writer, "type", "path");
                                            WriteString(writer, "value", path.Value);
                                            break;
                                        case ArgumentValue.Boolean boolean:
                                            WriteString(writer, "type", "boolean");
                                            writer.WriteBoolean("value", boolean.Value);
                                            break;
                                        case ArgumentValue.Integer integer:
                                            WriteString(writer, "type", "integer");
                                            writer.WriteNumber("value", integer.Value);
                                            break;
                                        default:
                                            throw Error("UnknownType", "$.contexts", "Unsupported preset value cannot be serialized.");
                                    }

                                    writer.WriteEndObject();
                                }

                                writer.WriteEndArray();
                            }

                            WritePolicy(writer, "completionOverride", target.Metadata?.CompletionOverride);
                            break;
                        default:
                            throw Error("UnknownDestination", "$.contexts", "Unsupported destination cannot be serialized.");
                    }

                    writer.WriteEndObject();
                    writer.WriteEndObject();
                }

                writer.WriteEndArray();
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteStartArray("commands");
            foreach (CommandDefinition command in document.Commands.OrderBy(command => command.Id, StringComparer.Ordinal))
            {
                writer.WriteStartObject();
                WriteString(writer, "id", command.Id);
                WriteString(writer, "displayName", command.DisplayName);
                if (command.Description is not null)
                {
                    WriteString(writer, "description", command.Description);
                }

                writer.WriteStartArray("parameters");
                foreach (ParameterDefinition parameter in command.Parameters)
                {
                    writer.WriteStartObject();
                    WriteString(writer, "id", parameter.Id);
                    WriteString(writer, "name", parameter.Name);
                    WriteString(writer, "type", parameter.ValueType switch
                    {
                        ParameterValueType.Text => "text",
                        ParameterValueType.Boolean => "boolean",
                        ParameterValueType.Integer => "integer",
                        ParameterValueType.Path => "path",
                        _ => throw Error("UnknownType", "$.commands", "Unsupported parameter type cannot be serialized."),
                    });
                    writer.WriteBoolean("required", parameter.Required);
                    if (parameter.Description is not null)
                    {
                        WriteString(writer, "description", parameter.Description);
                    }

                    writer.WriteEndObject();
                }

                writer.WriteEndArray();
                WritePolicy(writer, "defaultCompletionPolicy", command.DefaultCompletionPolicy);
                if (command.Icon is not null)
                {
                    writer.WriteStartObject("icon");
                    switch (command.Icon)
                    {
                        case CommandIcon.Automatic:
                            WriteString(writer, "kind", "automatic");
                            break;
                        case CommandIcon.CustomFile icon:
                            WriteString(writer, "kind", "custom-file");
                            WriteString(writer, "path", icon.Path);
                            break;
                        case CommandIcon.BuiltIn icon:
                            WriteString(writer, "kind", "built-in");
                            WriteString(writer, "id", icon.Id);
                            break;
                        default:
                            throw Error("UnknownIcon", "$.commands", "Unsupported icon cannot be serialized.");
                    }

                    writer.WriteEndObject();
                }

                if (command.Execution is not null)
                {
                    if (command.Execution is not CommandExecutionSpecification.LaunchExecutable launch)
                    {
                        throw Error("UnknownExecution", "$.commands", "Unsupported execution specification cannot be serialized.");
                    }

                    writer.WriteStartObject("execution");
                    WriteString(writer, "kind", "launch-executable");
                    WriteString(writer, "executableParameterId", launch.ExecutableParameterId);
                    writer.WriteEndObject();
                }

                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        if (stream.Length > MaximumBytes)
        {
            throw Error("DocumentTooLarge", "$", "Configuration exceeds the 1 MiB limit.");
        }

        return stream.ToArray();
    }

    private static void WriteString(Utf8JsonWriter writer, string name, string? value)
    {
        if (value is not null)
        {
            try
            {
                new UTF8Encoding(false, true).GetByteCount(value);
            }
            catch (EncoderFallbackException)
            {
                throw Error("InvalidUnicode", "$", "A string contains unrepresentable Unicode; it was not silently replaced.");
            }
        }

        writer.WriteString(name, value);
    }

    private static void WritePolicy(Utf8JsonWriter writer, string name, CompletionPolicy? policy)
    {
        if (policy is not null)
        {
            WriteString(writer, name, policy switch
            {
                CompletionPolicy.Dismiss => "dismiss",
                CompletionPolicy.StayOpen => "stay-open",
                _ => throw Error("InvalidPolicy", "$.commands", "Unsupported completion policy cannot be serialized."),
            });
        }
    }
}
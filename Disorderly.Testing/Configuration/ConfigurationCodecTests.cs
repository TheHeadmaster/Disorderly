using System.IO;
using System.Text;
using System.Text.Json.Nodes;
using Disorderly.Commands;
using Disorderly.Configuration;
using Disorderly.Routing;
using Disorderly.Services;
using NUnit.Framework;

namespace Disorderly.Testing.Configuration;

[TestFixture]
public class ConfigurationCodecTests
{
    [Test]
    public void FullTypedRoundTripRetainsValuesAndAllMetadataDeterministically()
    {
        ConfigurationDocument original = ConfigurationFixture.Full();
        byte[] encoded = ConfigurationCodec.Encode(original);
        ConfigurationDecodeResult result = ConfigurationCodec.Decode(encoded);
        Assert.That(result.Diagnostics, Is.Empty);
        ConfigurationDocument document = result.Document!;
        Assert.That(ConfigurationCodec.Encode(document), Is.EqualTo(encoded));
        Assert.That(PaletteConfiguration.CreateStore().Publish(document.Routing, document.Commands).IsValid, Is.True);
        CommandDefinition command = document.Commands.Single(command => command.Id == "launch");
        Assert.That(command.DisplayName, Is.EqualTo(original.Commands[0].DisplayName));
        Assert.That(command.Description, Is.EqualTo(original.Commands[0].Description));
        Assert.That(command.Parameters, Is.EqualTo(original.Commands[0].Parameters));
        Assert.That(command.Execution, Is.EqualTo(original.Commands[0].Execution));
        Assert.That(command.Icon, Is.EqualTo(original.Commands[0].Icon));
        Assert.That(command.DefaultCompletionPolicy, Is.EqualTo(CompletionPolicy.StayOpen));
        ContextDefinition child = document.Routing.Contexts.Single(context => context.Id == "child");
        BindingDestination.Command destination = (BindingDestination.Command)child.Bindings.Single().Destination;
        Assert.That(destination.Metadata!.CompletionOverride, Is.EqualTo(CompletionPolicy.Dismiss));
        Assert.That(destination.Metadata.Presets!.Assignments, Is.EqualTo(((BindingDestination.Command)original.Routing.Contexts[1].Bindings[0].Destination).Metadata!.Presets!.Assignments));
        Assert.That(destination.Metadata.Presets.Assignments.Single(value => value.ParameterId == "text").Value, Is.EqualTo(new ArgumentValue.Text("")));
        Assert.That(destination.Metadata.Presets.Assignments.Single(value => value.ParameterId == "flag").Value, Is.EqualTo(new ArgumentValue.Boolean(false)));
        Assert.That(destination.Metadata.Presets.Assignments.Single(value => value.ParameterId == "count").Value, Is.EqualTo(new ArgumentValue.Integer(0)));
        Assert.That(document.Commands.Single(command => command.Id == "automatic").Icon, Is.TypeOf<CommandIcon.Automatic>());
        Assert.That(document.Commands.Single(command => command.Id == "built-in").Icon, Is.TypeOf<CommandIcon.BuiltIn>());
    }

    [TestCase("Enter")]
    [TestCase("F24")]
    [TestCase("+")]
    [TestCase("😀")]
    [TestCase("é")]
    public void LogicalKeysAndModifiersRoundTripWithoutPhysicalLayoutAssumptions(string key)
    {
        ConfigurationDocument document = ConfigurationFixture.Full();
        ContextDefinition context = document.Routing.Contexts[0];
        document = document with { Routing = document.Routing with { Contexts = [context with { Bindings = [context.Bindings[0] with { Gesture = new(key, GestureModifiers.Control | GestureModifiers.Shift) }] }, document.Routing.Contexts[1]] } };
        ConfigurationDocument decoded = ConfigurationCodec.Decode(ConfigurationCodec.Encode(document)).Document!;
        Assert.That(decoded.Routing.Contexts.Single(context => context.Id == "root").Bindings[0].Gesture, Is.EqualTo(new LogicalGesture(key, GestureModifiers.Shift | GestureModifiers.Control)));
    }

    [TestCase("{", "InvalidJson")]
    [TestCase("[]", "WrongKind")]
    [TestCase("null", "WrongKind")]
    [TestCase("{\"version\":2,\"rootContextId\":\"root\",\"contexts\":[],\"commands\":[]}", "UnsupportedVersion")]
    public void InvalidRootOrVersionHasStructuredDiagnostics(string json, string code)
    {
        ConfigurationDecodeResult result = ConfigurationCodec.Decode(ConfigurationFixture.Bytes(json));
        Assert.That(result.Document, Is.Null);
        Assert.That(result.Diagnostics.Single().Code, Is.EqualTo(code));
        Assert.That(result.Diagnostics[0].Location, Does.StartWith("$"));
    }

    [TestCase("version", "\"1\"", "InvalidVersion")]
    [TestCase("version", "1.5", "InvalidVersion")]
    [TestCase("version", "null", "InvalidVersion")]
    [TestCase("contexts", "null", "WrongKind")]
    [TestCase("commands", "{}", "WrongKind")]
    [TestCase("rootContextId", "false", "WrongKind")]
    public void WrongRootFieldKindsAreRejected(string field, string raw, string code)
    {
        JsonObject json = JsonNode.Parse(ConfigurationFixture.Minimal)!.AsObject();
        json[field] = JsonNode.Parse(raw);
        Assert.That(ConfigurationCodec.Decode(ConfigurationFixture.Bytes(json.ToJsonString())).Diagnostics.Single().Code, Is.EqualTo(code));
    }

    [TestCase("version")]
    [TestCase("rootContextId")]
    [TestCase("contexts")]
    [TestCase("commands")]
    public void EveryRequiredRootFieldMustBePresent(string field)
    {
        JsonObject json = JsonNode.Parse(ConfigurationFixture.Minimal)!.AsObject(); json.Remove(field);
        Assert.That(ConfigurationCodec.Decode(ConfigurationFixture.Bytes(json.ToJsonString())).Diagnostics.Single().Code, Is.EqualTo("MissingField"));
    }

    [TestCase("{\"version\":1,\"version\":1,\"rootContextId\":\"root\",\"contexts\":[],\"commands\":[]}")]
    [TestCase("{\"version\":1,\"rootContextId\":\"root\",\"contexts\":[{\"id\":\"root\",\"id\":\"other\",\"displayName\":\"Root\",\"bindings\":[]}],\"commands\":[]}")]
    public void DuplicatePropertiesNeverBecomeLastValueWins(string json) => Assert.That(ConfigurationCodec.Decode(ConfigurationFixture.Bytes(json)).Diagnostics.Single().Code, Is.EqualTo("DuplicateProperty"));

    [TestCase("contexts", "activeInvocation")]
    [TestCase("commands", "operationRecords")]
    public void UnsupportedRootFieldsAreRejected(string ignored, string field)
    {
        JsonObject json = JsonNode.Parse(ConfigurationFixture.Minimal)!.AsObject(); json[field] = ignored;
        Assert.That(ConfigurationCodec.Decode(ConfigurationFixture.Bytes(json.ToJsonString())).Diagnostics.Single().Code, Is.EqualTo("UnknownField"));
    }

    [TestCase("destination", "kind", "\"unknown\"", "UnknownDestination")]
    [TestCase("destination", "presets", "null", "WrongKind")]
    [TestCase("destination", "completionOverride", "\"StayOpen\"", "InvalidPolicy")]
    [TestCase("execution", "kind", "\"shell\"", "UnknownExecution")]
    [TestCase("icon", "kind", "\"clr-type\"", "UnknownIcon")]
    [TestCase("gesture", "modifiers", "[\"Unknown\"]", "InvalidModifier")]
    [TestCase("gesture", "modifiers", "[\"Control\",\"Control\"]", "DuplicateModifier")]
    [TestCase("gesture", "key", "\"not-a-key\"", "InvalidGesture")]
    public void TypedObjectsRejectUnsupportedDefinitions(string target, string field, string raw, string code)
    {
        JsonObject json = JsonNode.Parse(ConfigurationCodec.Encode(ConfigurationFixture.Full()))!.AsObject();
        JsonObject command = json["commands"]!.AsArray().Single(item => (string?)item!["id"] == "launch")!.AsObject();
        JsonObject binding = json["contexts"]!.AsArray().Single(item => (string?)item!["id"] == "child")!["bindings"]![0]!.AsObject();
        JsonObject subject = (target is "destination" or "gesture" ? binding[target] : command[target])!.AsObject();
        subject[field] = JsonNode.Parse(raw);
        Assert.That(ConfigurationCodec.Decode(ConfigurationFixture.Bytes(json.ToJsonString())).Diagnostics.Single().Code, Is.EqualTo(code));
    }

    [TestCase("integer", "9223372036854775808", "InvalidInteger")]
    [TestCase("integer", "1.25", "InvalidInteger")]
    [TestCase("integer", "\"0\"", "InvalidInteger")]
    [TestCase("boolean", "0", "WrongKind")]
    [TestCase("text", "false", "WrongKind")]
    [TestCase("path", "null", "WrongKind")]
    [TestCase("object", "{}", "UnknownType")]
    public void TypedPresetValuesRejectCoercionOrOverflow(string type, string value, string code)
    {
        JsonObject json = JsonNode.Parse(ConfigurationCodec.Encode(ConfigurationFixture.Full()))!.AsObject();
        JsonObject preset = json["contexts"]!.AsArray().Single(item => (string?)item!["id"] == "child")!["bindings"]![0]!["destination"]!["presets"]![0]!.AsObject();
        preset["type"] = type; preset["value"] = JsonNode.Parse(value);
        Assert.That(ConfigurationCodec.Decode(ConfigurationFixture.Bytes(json.ToJsonString())).Diagnostics.Single().Code, Is.EqualTo(code));
    }

    [Test]
    public void SizeDepthCollectionBoundsUtf8AndBomAreExplicit()
    {
        Assert.That(ConfigurationCodec.Decode(new byte[ConfigurationCodec.MaximumBytes + 1]).Diagnostics.Single().Code, Is.EqualTo("DocumentTooLarge"));
        Assert.That(ConfigurationCodec.Decode(ConfigurationFixture.Bytes(new string('[', 40) + new string(']', 40))).Document, Is.Null);
        Assert.That(ConfigurationCodec.Decode(new byte[] { 0xFF }).Diagnostics.Single().Code, Is.EqualTo("InvalidUtf8"));
        Assert.That(ConfigurationCodec.Decode(new byte[] { 0xEF, 0xBB, 0xBF }.Concat(ConfigurationFixture.Bytes(ConfigurationFixture.Minimal)).ToArray()).Document, Is.Not.Null);
        JsonObject json = JsonNode.Parse(ConfigurationFixture.Minimal)!.AsObject();
        json["contexts"] = new JsonArray(Enumerable.Range(0, 513).Select(_ => JsonNode.Parse("{}")!).ToArray());
        Assert.That(ConfigurationCodec.Decode(ConfigurationFixture.Bytes(json.ToJsonString())).Diagnostics.Single().Code, Is.EqualTo("CollectionTooLarge"));
    }

    [Test]
    public void DocumentExampleActuallyLoadsAndRoundTrips()
    {
        using Stream resource = typeof(ConfigurationCodecTests).Assembly.GetManifestResourceStream("Disorderly.ExampleConfiguration.json")!;
        using MemoryStream bytes = new(); resource.CopyTo(bytes);
        ConfigurationDocument document = ConfigurationCodec.Decode(bytes.ToArray()).Document!;
        Assert.That(PaletteConfiguration.CreateStore().Publish(document.Routing, document.Commands).IsValid, Is.True);
        byte[] encoded = ConfigurationCodec.Encode(document);
        Assert.That(ConfigurationCodec.Encode(ConfigurationCodec.Decode(encoded).Document!), Is.EqualTo(encoded));
    }
}
using System.Text.Json.Nodes;
using Disorderly.Configuration;
using NUnit.Framework;

namespace Disorderly.Testing.Configuration;

[TestFixture]
public class ConfigurationRobustnessTests
{
    [TestCase("gesture", "unknown", false, "UnknownField")]
    [TestCase("destination", "kind", true, "MissingDiscriminator")]
    [TestCase("execution", "kind", true, "MissingDiscriminator")]
    [TestCase("icon", "kind", true, "MissingDiscriminator")]
    [TestCase("icon", "unknown", false, "UnknownField")]
    [TestCase("command", "parameters", true, "MissingField")]
    [TestCase("parameter", "type", true, "MissingField")]
    public void NestedShapesAreStrict(string target, string field, bool remove, string code)
    {
        JsonObject root = JsonNode.Parse(ConfigurationCodec.Encode(ConfigurationFixture.Full()))!.AsObject();
        JsonObject command = root["commands"]!.AsArray().Single(item => (string?)item!["id"] == "launch")!.AsObject();
        JsonObject binding = root["contexts"]!.AsArray().Single(item => (string?)item!["id"] == "child")!["bindings"]![0]!.AsObject();
        JsonObject subject = target switch
        {
            "gesture" or "destination" => binding[target]!.AsObject(),
            "parameter" => command["parameters"]![0]!.AsObject(),
            "command" => command,
            _ => command[target]!.AsObject(),
        };
        if (remove) { subject.Remove(field); } else { subject[field] = "not supported"; }
        Assert.That(ConfigurationCodec.Decode(ConfigurationFixture.Bytes(root.ToJsonString())).Diagnostics.Single().Code, Is.EqualTo(code));
    }

    [Test]
    public void UnsupportedFutureVersionsAreRecognizedBeforeAttemptingTheirSchema()
    {
        ConfigurationDecodeResult result = ConfigurationCodec.Decode(ConfigurationFixture.Bytes("""{"version":9,"new-schema-field":{}}"""));
        Assert.That(result.UnsupportedVersion, Is.True);
        Assert.That(result.Diagnostics.Single().Code, Is.EqualTo("UnsupportedVersion"));
    }

    [Test]
    public async Task InvalidUnicodeCannotCrashDecodeOrSilentlyChangeSavedText()
    {
        string invalid = ConfigurationFixture.Minimal.Replace("\"Root\"", "\"\\uD800\"");
        Assert.That(ConfigurationCodec.Decode(ConfigurationFixture.Bytes(invalid)).Document, Is.Null);
        ConfigurationDocument document = ConfigurationFixture.Full("\uD800");
        ConfigurationFixture.Files files = new();
        ConfigurationService service = ConfigurationFixture.Service(files);
        ConfigurationResult result = await service.SaveAsync(document, 0);
        Assert.That(result.Status, Is.EqualTo(ConfigurationStatus.InvalidDocument));
        Assert.That(result.Diagnostics.Single().Code, Is.EqualTo("InvalidUnicode"));
        Assert.That(files.Writes, Is.Zero);
    }

    [Test]
    public async Task ExternalPublicationDuringSaveIsPreservedAndSavedButUnpublishedStateIsExplicit()
    {
        TaskCompletionSource started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        ConfigurationFixture.Files files = new() { BeforeWrite = () => { started.TrySetResult(); return release.Task; } };
        ConfigurationService service = ConfigurationFixture.Service(files);
        Task<ConfigurationResult> pending = service.SaveAsync(ConfigurationFixture.Full(), 0);
        await started.Task;
        service.Store.Publish(ConfigurationFixture.Empty.Routing, []);
        object external = service.Store.Current!;
        release.SetResult();
        ConfigurationResult result = await pending;
        Assert.That(result.Status, Is.EqualTo(ConfigurationStatus.Conflict));
        Assert.That(result.Replaced, Is.True);
        Assert.That(service.Store.Current, Is.SameAs(external));
        Assert.That(files.Content, Is.Not.Null);
    }
}
using Disorderly.Commands;
using Disorderly.Configuration;
using NUnit.Framework;

namespace Disorderly.Testing.Configuration;

[TestFixture]
public class ConfigurationSaveTests
{
    [Test]
    public async Task InitialAndReplacementSavesPublishOnlySavedValidatedVersionsAndRoundTrip()
    {
        ConfigurationFixture.Files files = new();
        ConfigurationService service = ConfigurationFixture.Service(files);
        ConfigurationDocument document = ConfigurationFixture.Full();
        ConfigurationResult saved = await service.SaveAsync(document, 0);
        Assert.That(saved.Status, Is.EqualTo(ConfigurationStatus.Saved));
        Assert.That(saved.Replaced, Is.True);
        Assert.That(saved.Revision, Is.EqualTo(1));
        byte[] first = files.Content!.ToArray();
        Assert.That((await service.SaveAsync(document, 1)).Success, Is.True);
        Assert.That(files.Content, Is.EqualTo(first));
        Assert.That(service.Revision, Is.EqualTo(2));
        Assert.That(ConfigurationCodec.Encode(ConfigurationDocument.FromSnapshot(service.Store.Current!)), Is.EqualTo(first));
        ConfigurationService reloaded = ConfigurationFixture.Service(files);
        Assert.That((await reloaded.ReloadAsync()).Success, Is.True);
        Assert.That(ConfigurationCodec.Encode(ConfigurationDocument.FromSnapshot(reloaded.Store.Current!)), Is.EqualTo(first));
    }

    [Test]
    public async Task StaleRevisionAndExternalContentChangesAreDetectedWithoutOverwriting()
    {
        ConfigurationFixture.Files files = new() { Content = ConfigurationCodec.Encode(ConfigurationFixture.Full("Old")) };
        ConfigurationService service = ConfigurationFixture.Service(files);
        Assert.That((await service.SaveAsync(ConfigurationFixture.Full(), 0)).Status, Is.EqualTo(ConfigurationStatus.Conflict), "Existing unobserved files are protected.");
        await service.ReloadAsync();
        long originalRevision = service.Revision;
        files.Content = ConfigurationCodec.Encode(ConfigurationFixture.Full("Changed externally"));
        byte[] external = files.Content.ToArray();
        CommandConfigurationSnapshot publication = service.Store.Current!;
        Assert.That((await service.SaveAsync(ConfigurationFixture.Full("Stale proposal"), originalRevision)).Status, Is.EqualTo(ConfigurationStatus.Conflict));
        Assert.That(files.Content, Is.EqualTo(external));
        Assert.That(service.Store.Current, Is.SameAs(publication));
        await service.ReloadAsync();
        int writes = files.Writes;
        Assert.That((await service.SaveAsync(ConfigurationFixture.Full(), originalRevision)).Status, Is.EqualTo(ConfigurationStatus.Conflict));
        Assert.That(files.Writes, Is.EqualTo(writes), "Stale in-process proposals never reach persistence.");
    }

    [TestCase(ConfigurationWriteStatus.Failed, ConfigurationStatus.IoFailure, false)]
    [TestCase(ConfigurationWriteStatus.Uncertain, ConfigurationStatus.ReplacementUncertain, null)]
    public async Task FailedOrAmbiguousWritesNeverPublishUnsavedProposals(ConfigurationWriteStatus status, ConfigurationStatus expected, bool? replaced)
    {
        ConfigurationFixture.Files files = new() { Content = ConfigurationCodec.Encode(ConfigurationFixture.Empty) };
        ConfigurationService service = ConfigurationFixture.Service(files);
        await service.ReloadAsync();
        CommandConfigurationSnapshot old = service.Store.Current!;
        byte[] before = files.Content!.ToArray();
        files.WriteResult = new(status, replaced, "InjectedFailure");
        ConfigurationResult result = await service.SaveAsync(ConfigurationFixture.Full(), service.Revision);
        Assert.That(result.Status, Is.EqualTo(expected));
        Assert.That(result.Replaced, Is.EqualTo(replaced));
        Assert.That(service.Store.Current, Is.SameAs(old));
        Assert.That(files.Content, Is.EqualTo(before));
        Assert.That(service.Revision, Is.EqualTo(1));
    }

    [Test]
    public async Task TwoConcurrentSavesFromOneRevisionHaveOnlyOneWinner()
    {
        TaskCompletionSource started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        ConfigurationFixture.Files files = new() { BeforeWrite = () => { started.TrySetResult(); return release.Task; } };
        ConfigurationService service = ConfigurationFixture.Service(files);
        Task<ConfigurationResult> first = service.SaveAsync(ConfigurationFixture.Full("First"), 0);
        await started.Task;
        Task<ConfigurationResult> second = service.SaveAsync(ConfigurationFixture.Full("Second"), 0);
        release.SetResult();
        ConfigurationResult[] results = await Task.WhenAll(first, second);
        Assert.That(results.Select(result => result.Status), Is.EqualTo(new[] { ConfigurationStatus.Saved, ConfigurationStatus.Conflict }));
        Assert.That(files.Writes, Is.EqualTo(1));
        Assert.That(service.Store.Current!.Catalog.Find("launch")!.DisplayName, Is.EqualTo("First"));
    }

    [Test]
    public async Task FailedValidationNeverWritesAndInvalidExistingFileIsNotSilentlyReplaced()
    {
        ConfigurationFixture.Files files = new() { Content = ConfigurationFixture.Bytes("{") };
        ConfigurationService service = ConfigurationFixture.Service(files);
        await service.ReloadAsync();
        ConfigurationDocument invalid = ConfigurationFixture.Full() with { Routing = new("missing", []) };
        Assert.That((await service.SaveAsync(invalid, 0)).Status, Is.EqualTo(ConfigurationStatus.DomainInvalid));
        Assert.That(files.Writes, Is.Zero);
        Assert.That((await service.SaveAsync(ConfigurationFixture.Full(), 0)).Status, Is.EqualTo(ConfigurationStatus.Conflict));
        Assert.That(files.Content, Is.EqualTo(ConfigurationFixture.Bytes("{")));
    }

    [Test]
    public async Task ExternalInProcessPublicationIsNotSilentlyOverwrittenBySave()
    {
        ConfigurationFixture.Files files = new();
        ConfigurationService service = ConfigurationFixture.Service(files);
        service.Store.Publish(ConfigurationFixture.Empty.Routing, []);
        Assert.That((await service.SaveAsync(ConfigurationFixture.Full(), 0)).Status, Is.EqualTo(ConfigurationStatus.Conflict));
        Assert.That(files.Writes, Is.Zero);
    }
}
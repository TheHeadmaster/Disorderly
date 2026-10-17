using System.IO;
using Disorderly.Configuration;
using Disorderly.Services;
using NUnit.Framework;

namespace Disorderly.Testing.Configuration;

[TestFixture]
public class ConfigurationFileTests
{
    private string directory = null!;
    private string path = null!;
    [SetUp]
    public void SetUp()
    {
        this.directory = Path.Combine(Path.GetTempPath(), "Disorderly.Configuration.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(this.directory);
        this.path = Path.Combine(this.directory, "config.json");
    }
    [TearDown]
    public void TearDown() => Directory.Delete(this.directory, true);

    private sealed class FailingReplacement : IConfigurationReplacement
    {
        public bool Existing { get; private set; }
        public string? Staged { get; private set; }
        public void Replace(string temporary, string destination, bool existing)
        {
            this.Existing = existing;
            this.Staged = File.ReadAllText(temporary);
            throw new IOException("Simulated replacement failure");
        }
    }

    [Test]
    public async Task RealInitialSaveReplacementReloadAndDigestConflictWorkInAnIsolatedDirectory()
    {
        ConfigurationService service = new(PaletteConfiguration.CreateEmptyStore(), this.path);
        Assert.That((await service.ReloadAsync()).Status, Is.EqualTo(ConfigurationStatus.Missing));
        Assert.That(File.Exists(this.path), Is.False);
        Assert.That((await service.SaveAsync(ConfigurationFixture.Full("First"), 0)).Status, Is.EqualTo(ConfigurationStatus.Saved));
        Assert.That((await service.SaveAsync(ConfigurationFixture.Full("Second"), 1)).Status, Is.EqualTo(ConfigurationStatus.Saved));
        Assert.That(Directory.GetFiles(this.directory).Select(Path.GetFileName), Is.EqualTo(new[] { "config.json" }));
        byte[] bytes = await File.ReadAllBytesAsync(this.path);
        Assert.That(ConfigurationCodec.Decode(bytes).Document!.Commands.Single(command => command.Id == "launch").DisplayName, Is.EqualTo("Second"));
        await File.WriteAllTextAsync(this.path, ConfigurationFixture.Minimal);
        File.SetLastWriteTimeUtc(this.path, DateTime.UtcNow.AddYears(-1)); // Digest, not modification time, controls conflict detection.
        Assert.That((await service.SaveAsync(ConfigurationFixture.Full("Stale"), 2)).Status, Is.EqualTo(ConfigurationStatus.Conflict));
        Assert.That(await File.ReadAllTextAsync(this.path), Is.EqualTo(ConfigurationFixture.Minimal));
        Assert.That((await service.ReloadAsync()).Status, Is.EqualTo(ConfigurationStatus.Loaded));
        Assert.That(service.Store.Current!.Catalog.Definitions, Is.Empty);
    }

    [Test]
    public async Task FailedReplacementKeepsOldContentCleansStagingAndDoesNotPublish()
    {
        await File.WriteAllTextAsync(this.path, ConfigurationFixture.Minimal);
        FailingReplacement replacement = new();
        ConfigurationService service = new(PaletteConfiguration.CreateEmptyStore(), this.path, new ConfigurationFiles(replacement));
        await service.ReloadAsync();
        object old = service.Store.Current!;
        ConfigurationResult result = await service.SaveAsync(ConfigurationFixture.Full(), 1);
        Assert.That(result.Status, Is.EqualTo(ConfigurationStatus.ReplacementUncertain));
        Assert.That(result.Replaced, Is.Null);
        Assert.That(replacement.Existing, Is.True);
        Assert.That(ConfigurationCodec.Decode(ConfigurationFixture.Bytes(replacement.Staged!)).Document, Is.Not.Null);
        Assert.That(await File.ReadAllTextAsync(this.path), Is.EqualTo(ConfigurationFixture.Minimal));
        Assert.That(Directory.GetFiles(this.directory).Length, Is.EqualTo(1));
        Assert.That(service.Store.Current, Is.SameAs(old));
    }

    [Test]
    public async Task TemporaryCreationFailureCannotTruncateDestination()
    {
        string parentFile = Path.Combine(this.directory, "not-a-directory");
        await File.WriteAllTextAsync(parentFile, "preserved");
        ConfigurationService service = new(PaletteConfiguration.CreateEmptyStore(), Path.Combine(parentFile, "config.json"));
        object old = service.Store.Current!;
        ConfigurationResult result = await service.SaveAsync(ConfigurationFixture.Full(), 0);
        Assert.That(result.Success, Is.False);
        Assert.That(await File.ReadAllTextAsync(parentFile), Is.EqualTo("preserved"));
        Assert.That(service.Store.Current, Is.SameAs(old));
        Assert.That(Directory.GetFiles(this.directory).Length, Is.EqualTo(1));
    }

    [Test]
    public async Task InvalidAndOversizedFilesAreNeverOverwrittenByLoad()
    {
        await File.WriteAllTextAsync(this.path, "{");
        ConfigurationService service = new(PaletteConfiguration.CreateEmptyStore(), this.path);
        Assert.That((await service.ReloadAsync()).Status, Is.EqualTo(ConfigurationStatus.InvalidDocument));
        Assert.That(await File.ReadAllTextAsync(this.path), Is.EqualTo("{"));
        await File.WriteAllBytesAsync(this.path, new byte[ConfigurationCodec.MaximumBytes + 1]);
        Assert.That((await service.ReloadAsync()).Diagnostics.Single().Code, Is.EqualTo("DocumentTooLarge"));
        Assert.That(new FileInfo(this.path).Length, Is.EqualTo(ConfigurationCodec.MaximumBytes + 1));
    }
}
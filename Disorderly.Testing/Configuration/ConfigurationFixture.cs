using System.Text;
using Disorderly.Commands;
using Disorderly.Configuration;
using Disorderly.Routing;
using Disorderly.Services;

namespace Disorderly.Testing.Configuration;

internal static class ConfigurationFixture
{
    internal const string Minimal = """{"version":1,"rootContextId":"root","contexts":[{"id":"root","displayName":"Root","bindings":[]}],"commands":[]}""";
    internal static byte[] Bytes(string json) => Encoding.UTF8.GetBytes(json);
    internal static ConfigurationDocument Empty => ConfigurationCodec.Decode(Bytes(Minimal)).Document!;
    internal static ConfigurationDocument Full(string name = "Application 日本語")
    {
        ParameterDefinition[] parameters = [new("executable", "Executable", ParameterValueType.Path, true, "Opaque path"),
            new("text", "Label", ParameterValueType.Text), new("flag", "Flag", ParameterValueType.Boolean),
            new("count", "Count", ParameterValueType.Integer), new("minimum", "Minimum", ParameterValueType.Integer), new("maximum", "Maximum", ParameterValueType.Integer)];
        CommandDefinition command = new("launch", name, parameters, "Unicode Ω description", CompletionPolicy.StayOpen,
            new CommandIcon.CustomFile(@"Z:\not-present\icon.ico"), new CommandExecutionSpecification.LaunchExecutable("executable"));
        BindingDestination.Command destination = new("launch")
        {
            Metadata = new(new ArgumentSet([new("executable", new ArgumentValue.Path(@"C:\Program Files\Example\app.exe")), new("text", new ArgumentValue.Text("")),
                new("flag", new ArgumentValue.Boolean(false)), new("count", new ArgumentValue.Integer(0)), new("minimum", new ArgumentValue.Integer(long.MinValue)),
                new("maximum", new ArgumentValue.Integer(long.MaxValue))]), CompletionPolicy.Dismiss),
        };
        return new(1, new("root", [new("root", "Root", [new("enter", "root", new("+", GestureModifiers.Control | GestureModifiers.Shift), new BindingDestination.Context("child"))]),
            new("child", "Child", [new("run", "child", new("Ω", GestureModifiers.Alt | GestureModifiers.Meta), destination)])]),
            [command, new("automatic", "Automatic", [], Icon: new CommandIcon.Automatic()), new("built-in", "Built in", [], Icon: new CommandIcon.BuiltIn("generic"))]);
    }

    internal sealed class Files : IConfigurationFiles
    {
        internal byte[]? Content { get; set; }
        internal Exception? ReadError { get; set; }
        internal ConfigurationWriteResult? WriteResult { get; set; }
        internal Func<Task>? BeforeWrite { get; set; }
        internal int Writes { get; private set; }
        internal byte[]? Written { get; private set; }
        public Task<byte[]?> ReadAsync(string path) => this.ReadError is null ? Task.FromResult(this.Content?.ToArray()) : Task.FromException<byte[]?>(this.ReadError);
        public async Task<ConfigurationWriteResult> WriteAsync(string path, ReadOnlyMemory<byte> bytes, string? expectedFingerprint)
        {
            this.Writes++;
            if (this.BeforeWrite is not null) { await this.BeforeWrite(); }
            if (this.WriteResult is not null) { return this.WriteResult; }
            if ((this.Content is null ? null : ConfigurationFiles.Fingerprint(this.Content)) != expectedFingerprint)
            {
                return new(ConfigurationWriteStatus.Conflict, false, "ExternalConflict");
            }

            this.Content = bytes.ToArray();
            this.Written = bytes.ToArray();
            return new(ConfigurationWriteStatus.Saved, true, "Saved");
        }
    }

    internal static ConfigurationService Service(Files files) => new(PaletteConfiguration.CreateEmptyStore(), System.IO.Path.Combine(System.IO.Path.GetTempPath(), "unused-config.json"), files);
}
using Progmasoft.Tazel.PluginApi;
using Progmasoft.Tazel.PluginApi.Steps;

namespace PluginProject;

/// <summary>The plugin: Tazel creates it and applies it before the BUILD.csx files of a module run.</summary>
public sealed class Plugin : ITazelPlugin
{
    /// <inheritdoc/>
    public void Apply(IPluginContext context)
    {
        // BUILD.csx files see PluginProject.Greetings without a using.
        context.Import("PluginProject");
    }
}

/// <summary>What BUILD.csx files of a module with this plugin can write: <c>Greetings.File("hello", "Hello!")</c>.</summary>
public static class Greetings
{
    /// <summary>A step that writes <paramref name="text"/> to <c>greeting.txt</c> in its output directory.</summary>
    public static Progmasoft.Tazel.Dsl.Common.Target File(string name, string text) =>
        Step.Declare(name, new()
        {
            Outputs = ["greeting.txt"],
            Key = text,
            Run = async (step, cancel) =>
                await System.IO.File.WriteAllTextAsync(Path.Combine(step.OutputDirectory, "greeting.txt"), text + "\n", cancel),
        });
}

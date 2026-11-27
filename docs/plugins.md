<!-- SPDX-FileCopyrightText: 2026 Progmasoft <support@progmasoft.com> -->
<!-- SPDX-License-Identifier: MIT -->

# Plugins

A plugin adds to what `BUILD.csx` files can declare. It is a .NET 10 class library made with the project SDK
`Progmasoft.Tazel.PluginSdk` and packed by `dotnet pack` into `$group.$name-$version.tazel.tar.xz`.

## Making a plugin

```text
dotnet new install Progmasoft.Tazel.PluginTemplates
dotnet new tazel-plugin -n Greeter --group com.example.tools --pluginName greeter
cd Greeter
dotnet pack -c Release -o out
```

```xml
<Project Sdk="Progmasoft.Tazel.PluginSdk/0.2.0">
  <PropertyGroup>
    <TazelPluginGroup>com.example.tools</TazelPluginGroup>
    <TazelPluginName>greeter</TazelPluginName>
    <Version>0.1.0</Version>
  </PropertyGroup>
</Project>
```

The SDK:

- targets `net10.0`, the runtime of Tazel;
- references `Progmasoft.Tazel.PluginApi` of its own version with `ExcludeAssets="runtime"` and `PrivateAssets="all"`:
  the plugin compiles against it, never ships it, and does not pass it on;
- stops the build when the group, the name or the SemVer version is missing or wrong;
- writes the group, name and version into the assembly as `TazelPluginAttribute`;
- makes `dotnet pack` write the archive and its `.sha256sums` instead of a `.nupkg`. The archive holds the plugin's
  assemblies and those of its `ProjectReference`s and `PackageReference`s, and `tazel-plugin.json`; the plugin API and
  the DSL are left out, whatever brought them.

## The plugin

```cs
using Progmasoft.Tazel.PluginApi;
using Progmasoft.Tazel.PluginApi.Steps;

namespace Greeter;

public sealed class Plugin : ITazelPlugin
{
    public void Apply(IPluginContext context) => context.Import("Greeter");
}

public static class Greetings
{
    public static Progmasoft.Tazel.Dsl.Common.Target File(string name, string text) =>
        Step.Declare(name, new()
        {
            Outputs = ["greeting.txt"],
            Key = text,
            Run = async (step, cancel) =>
                await System.IO.File.WriteAllTextAsync(Path.Combine(step.OutputDirectory, "greeting.txt"), text, cancel),
        });
}
```

- The entry assembly has one public class that implements `ITazelPlugin`, with a public constructor without parameters.
  Tazel creates it and calls `Apply` before the `BUILD.csx` files of the module run.
- `IPluginContext.Import` lets every `BUILD.csx` of the module use a namespace without a `using`; `Status` prints a line.
- The public types of the plugin's assemblies are there for `BUILD.csx` files to call.

## Steps

`Step.Declare(name, new() { ... })` declares a build step; a `BUILD.csx` may call it directly too, with
`using Progmasoft.Tazel.PluginApi.Steps;`.

| Setting | What it is |
| --- | --- |
| `Inputs` | files below the project file the step reads |
| `Outputs` | files the step writes, relative to its output directory |
| `Key` | text that changes what the step makes, such as its options |
| `Run` | the work: `(IStepContext step, CancellationToken cancel) => Task` |
| `Depends`, `Visibility`, `TargetConstraints`, `Tags` | as for every target |

A step runs again only when its key, an input, an output or what it depends on changed. A step may depend on an
executable the build makes; `step.Dependencies["//:gen"]` is its path, and `step.RunAsync` runs a program directly with
the environment of the actions. A C-family or Zig target that depends on a step runs after it and finds headers in its
output directory. A step fails by throwing `PluginException` with a message; that message, the lines of `step.Log`, and
an output the step did not write are reported with it.

## Loading

```cs
// MODULE.csx
plugins = Plugins.Define(LocalPlugin.From("plugins/com.example.tools.greeter-0.1.0.tazel.tar.xz"));
```

Tazel unpacks the archive once below `.tazel/plugins`, checks `tazel-plugin.json` (group, name, SemVer version, and a
plugin API with the MAJOR and, before 1.0.0, the MINOR of this Tazel's), and loads the plugin's assemblies into a load
context of their own, so two plugins' libraries do not clash; `Progmasoft.Tazel.PluginApi` and
`Progmasoft.Tazel.Dsl` are Tazel's own copies, shared by every plugin. `Plugin.From`, a plugin of Tazel Portal, is
refused by this Tazel, which fetches nothing.

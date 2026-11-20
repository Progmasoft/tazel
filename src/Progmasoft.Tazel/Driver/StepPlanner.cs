// SPDX-FileCopyrightText: 2026 Progmasoft <support@progmasoft.com>
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Progmasoft.Tazel.Dsl.Common;
using Progmasoft.Tazel.PluginApi;
using Progmasoft.Tazel.PluginApi.Steps;
using Progmasoft.Tazel.Project;

namespace Progmasoft.Tazel.Driver;

/// <summary>The build steps of plugins: work that runs in Tazel, cached by its inputs, outputs and key.</summary>
internal sealed partial class Planner
{
    private TargetProducts PlanStep(ProjectTarget target)
    {
        var settings = (StepSettings)target.Settings;
        var products = new TargetProducts();
        var directory = $"{Layout.PackageOut(target)}/_steps/{target.Label.Name}";
        var stamp = $"{directory}.step";
        var inputs = FileSets.Resolve(settings.Inputs, target.PackageDirectory, Layout.MainRoot, target.Place);
        var outputs = settings.Outputs.Select(output => $"{directory}/{output}").ToList();
        var dependencies = new Dictionary<string, string>(StringComparer.Ordinal);
        var prerequisites = new List<BuildAction>();
        foreach (var dependency in target.Dependencies)
        {
            var planned = Plan(dependency);
            if (planned.Main is { } main)
            {
                prerequisites.Add(main);
            }

            prerequisites.AddRange(planned.HeaderActions);
            if ((planned.Executable ?? planned.Library) is { } made)
            {
                dependencies[dependency.Label.ToString()] = Layout.Full(made);
            }
        }

        // The command is the step's key: what it is apart from its files.
        var command = new List<string> { "tazel-step", target.Label.ToString(), settings.Key };
        command.AddRange(settings.Outputs);
        var action = new BuildAction
        {
            Description = $"Running {target.Label}",
            Command = command,
            Directory = Layout.MainRoot,
            Inputs = inputs.Concat(dependencies.Values.Select(full => FileSets.ToRoot(full, Layout.MainRoot))).ToList(),
            Outputs = [stamp, .. outputs],
            Environment = ActionEnvironment,
            Work = cancel => RunStepAsync(target, settings, directory, stamp, outputs, inputs, dependencies, cancel),
        };
        action.Prerequisites.AddRange(prerequisites);
        products.Main = action;
        products.HeaderActions.Add(action);
        products.PublicIncludes.Add(directory);
        return products;
    }

    private async Task<ProcessResult> RunStepAsync(
        ProjectTarget target, StepSettings settings, string directory, string stamp, IReadOnlyList<string> outputs,
        IReadOnlyList<string> inputs, IReadOnlyDictionary<string, string> dependencies, CancellationToken cancel)
    {
        var full = Layout.Full(directory);
        Directory.CreateDirectory(full);
        var context = new StepContext(this, target, full, inputs.Select(Layout.Full).ToList(), dependencies);
        try
        {
            await settings.Run!(context, cancel).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested)
        {
            return new ProcessResult(-1, context.Text, Canceled: true);
        }
        catch (PluginException error)
        {
            return new ProcessResult(1, context.Text + error.Message + "\n");
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            return new ProcessResult(1, context.Text + $"{target.Place}: the step threw {error.GetType().Name}: {error.Message}\n");
        }

        var missing = outputs.FirstOrDefault(output => !File.Exists(Layout.Full(output)));
        if (missing is not null)
        {
            return new ProcessResult(1, context.Text + $"the step said it writes {missing}, and did not\n");
        }

        await File.WriteAllTextAsync(Layout.Full(stamp), $"{target.Label}\n", cancel).ConfigureAwait(false);
        return new ProcessResult(0, context.Text);
    }

    private sealed class StepContext(
        Planner planner, ProjectTarget target, string directory, IReadOnlyList<string> inputs,
        IReadOnlyDictionary<string, string> dependencies) : IStepContext
    {
        private readonly StringBuilder _log = new();

        public string Label => target.Label.ToString();

        public string ModuleRoot => target.Module.Root;

        public string PackageDirectory => target.PackageDirectory;

        public string OutputDirectory => directory;

        public IReadOnlyList<string> Inputs => inputs;

        /// <summary>The program or library each dependency made, by its label.</summary>
        public IReadOnlyDictionary<string, string> Dependencies => dependencies;

        public Platform Platform => planner.Options.Platform;

        public BuildMode Mode => planner.Options.Mode;

        public string Text
        {
            get
            {
                lock (_log)
                {
                    return _log.ToString();
                }
            }
        }

        public string? FindProgram(string name) => Host.Which(name);

        public async Task<StepRun> RunAsync(
            string program, IReadOnlyList<string> arguments, string? workingDirectory,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(arguments);
            var found = Host.Which(program) ?? throw new PluginException($"{Label}: the program '{program}' is not found");
            var result = await Processes.RunAsync([found, .. arguments], workingDirectory ?? ModuleRoot,
                planner.ActionEnvironment, null, cancellationToken).ConfigureAwait(false);
            return new StepRun(result.ExitCode, result.Output);
        }

        public void Log(string message)
        {
            lock (_log)
            {
                _log.Append(message).Append('\n');
            }
        }
    }
}

// SPDX-FileCopyrightText: 2026 Progmasoft <support@progmasoft.com>
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Progmasoft.Tazel.Archive;
using Progmasoft.Tazel.Driver;
using Progmasoft.Tazel.Dsl;
using Progmasoft.Tazel.Dsl.Build.Prelude;
using Progmasoft.Tazel.Dsl.Common;
using Progmasoft.Tazel.Dsl.Custom;
using Progmasoft.Tazel.Dsl.Custom.Parser;
using Progmasoft.Tazel.Dsl.Options;
using Progmasoft.Tazel.Output;
using Progmasoft.Tazel.Project;

namespace Progmasoft.Tazel.Cli;

/// <summary>Carries out a command line read by <see cref="CommandLine"/>.</summary>
internal sealed partial class Commands(Reporter reporter, string workingDirectory)
{
    /// <summary>The version of this Tazel.</summary>
    public static Version TazelVersion { get; } =
        typeof(Commands).Assembly.GetName().Version is { } version ? new Version(version.Major, version.Minor, version.Build) : new Version(0, 1, 0);

    /// <summary>Runs <paramref name="command"/>; the result is the exit status of tazel.</summary>
    public async Task<int> RunAsync(Command command, CancellationToken cancel)
    {
        ArgumentNullException.ThrowIfNull(command);
        switch (command)
        {
            case VersionCommand:
                reporter.Plain($"tazel {TazelVersion.ToString(3)} (.NET {Environment.Version}, {Host.Platform.Name()})");
                return 0;
            case HelpCommand help:
                reporter.Plain(Help.Text(help.Topic, help.Long));
                return 0;
            case CoverageCommand coverage:
                return await BuildAsync(coverage.Labels, coverage.Flags, BuildKind.Coverage, null, cancel).ConfigureAwait(false);
            case QueryCommand query:
                return await QueryAsync(query, cancel).ConfigureAwait(false);
            case ExplainCommand explain:
                Explain(explain);
                return 0;
            case FetchCommand:
                return await FetchAsync().ConfigureAwait(false);
            case VendorCommand:
                return await VendorAsync(cancel).ConfigureAwait(false);
            case PortalModulesCommand portal:
                return await PortalModulesAsync(portal, cancel).ConfigureAwait(false);
            case DaemonCommand daemon:
                reporter.Error(ErrorCodes.NoDaemon, $"tazel dotnet {daemon.Action}: this Tazel runs every command in a process of its own and has no daemon", null,
                    "Tazel is a morning person: awake at once, every time, no daemon needed.");
                return 1;
            case CleanCommand clean:
                return Clean(clean);
            case PackageCommand:
                return await PackageAsync().ConfigureAwait(false);
            case CustomCommandLine custom:
                return Custom(custom);
            case BuildCommand build:
                return await BuildAsync(build.Labels, build.Flags, BuildKind.Build, null, cancel).ConfigureAwait(false);
            case TestCommand test:
                return await BuildAsync(test.Labels, test.Flags, BuildKind.Test, null, cancel).ConfigureAwait(false);
            case RunCommand run:
                return await BuildAsync(run.Label is null ? [] : [run.Label], run.Flags, BuildKind.Run, run.Arguments, cancel)
                    .ConfigureAwait(false);
            default:
                throw new InvalidOperationException($"no handler for {command.GetType().Name}");
        }
    }

    private enum BuildKind
    {
        Build,
        Test,
        Coverage,
        Run,
    }

    /// <summary>
    /// Stops a command that would run what a project brings when a sandbox is asked for: this Tazel has no MicroVM to
    /// start, so nothing runs rather than run on the host.
    /// </summary>
    private void RefuseWithoutSandbox()
    {
        var (_, file) = TazelOptionsFile.Effective(workingDirectory);
        if (file is not null)
        {
            throw new ProjectException(
                $"{file} asks for sandboxing-with-microvm, and this Tazel has no MicroVM to start, so nothing ran; " +
                "set it to false to run without the sandbox");
        }
    }

    /// <summary>Says which settings of MODULE.csx this Tazel reads but does not act on, so none is taken for granted.</summary>
    private void WarnAboutWhatThisTazelDoesNotDo(Dsl.Module.Prelude.ModuleGlobals globals)
    {
        if (globals.cache?.Settings.Remote is { } cache)
        {
            reporter.Warning($"MODULE.csx names the remote cache {cache.Url}; this Tazel uses the cache on this machine only");
        }

        if (globals.execution?.Settings.Remote is { } remote)
        {
            reporter.Warning($"MODULE.csx names the remote execution {remote.Url}; this Tazel runs every action on this machine");
        }

        if (globals.execution?.Settings.Memory is not null)
        {
            reporter.Warning("MODULE.csx sets Execution.Memory; this Tazel limits the actions by Jobs only");
        }
    }

    private int Clean(CleanCommand clean)
    {
        var root = ModuleFiles.FindRoot(workingDirectory);
        var state = Path.Combine(root, ModuleFiles.StateDirectory);
        var removed = clean.Expunge
            ? [state]
            : new[] { "out", "testlogs", "coverage", "zig-cache", "package", "downloads" }.Select(name => Path.Combine(state, name)).ToArray();
        foreach (var directory in removed.Where(Directory.Exists))
        {
            Directory.Delete(directory, recursive: true);
        }

        reporter.Success(clean.Expunge ? $"removed {state}" : "removed the outputs");
        return 0;
    }

    private async Task<int> PackageAsync()
    {
        RefuseWithoutSandbox();
        var module = await Workspace.LoadMainModuleAsync(workingDirectory, TazelVersion).ConfigureAwait(false);
        var exclude = module.Globals.portalModules?.Settings.Exclude ?? Files.None;
        var files = ModuleArchive.Files(module.Root, relative => exclude.Items.Any(item =>
            item.Path is { } path ? path == relative : FileSets.Matches(item.Glob!.Pattern, relative) &&
                !item.Glob.Exclude.Any(pattern => FileSets.Matches(pattern, relative))));
        var info = module.Info;
        var baseName = ModuleArchive.BaseName(info.Group, info.Name, info.Version.ToNormalizedString());
        var archive = Path.Combine(module.Root, ModuleFiles.StateDirectory, "package", baseName + ModuleArchive.Extension);
        ModuleArchive.Write(module.Root, baseName, archive, files);
        await File.WriteAllTextAsync(archive + ".sha256sums", Sha256Sums.SumsLine(archive)).ConfigureAwait(false);
        reporter.Success($"wrote {FileSets.ToRoot(archive, module.Root)} ({files.Count} files) and its .sha256sums");
        return 0;
    }

    private int Custom(CustomCommandLine custom)
    {
        RefuseWithoutSandbox();
        var file = Path.Combine(workingDirectory, CustomCommandFiles.FileName);
        if (!File.Exists(file))
        {
            throw new CustomCommandsException($"there is no {CustomCommandFiles.FileName} in {workingDirectory}");
        }

        var commands = CustomParser.Parse(File.ReadAllText(file));
        if (custom.Words.Count > CustomCommandFiles.MaxArguments)
        {
            throw new CommandLineException($"tazel custom takes at most {CustomCommandFiles.MaxArguments} words");
        }

        var match = commands.Matching(custom.Words) ?? throw new CustomCommandsException(
            $"{CustomCommandFiles.FileName} has no command '{string.Join(' ', custom.Words.TakeWhile(w => w != CustomCommandFiles.NameEnd))}'" +
            (Suggestions.Nearest(string.Join(' ', custom.Words), commands.Select(c => c.Name)) is { } near ? $"; did you mean '{near}'?" : ""));
        var (command, arguments) = match;
        if (arguments.Count < command.Required)
        {
            throw new CommandLineException($"'{command.Name}' needs {command.Required} arguments, and {arguments.Count} were given");
        }

        if (arguments.Count > command.Required && !command.TakesAll)
        {
            throw new CommandLineException($"'{command.Name}' takes {command.Required} arguments, and {arguments.Count} were given");
        }

        var environment = Processes.Inherited();
        foreach (var step in command.Steps)
        {
            var started = step switch
            {
                CustomStep.Program program => program.Expanded(arguments),
                CustomStep.Shell shell => shell.Started(OperatingSystem.IsWindows()),
                _ => throw new InvalidOperationException("a step of no kind"),
            };
            var found = Host.Which(started[0]) ?? throw new CustomCommandsException(
                $"'{command.Name}': the program '{started[0]}' is not found");
            var status = Processes.RunAttached([found, .. started.Skip(1)], workingDirectory, environment);
            if (status != 0)
            {
                return status;
            }
        }

        return 0;
    }

    private async Task<int> BuildAsync(
        IReadOnlyList<string> labels, BuildFlags flags, BuildKind kind, IReadOnlyList<string>? programArguments,
        CancellationToken cancel)
    {
        RefuseWithoutSandbox();
        var (options, _) = TazelOptionsFile.Effective(workingDirectory);
        var platform = flags.Platform ?? options.Platform ?? Host.Platform;
        var mode = flags.Mode ?? BuildMode.Debug;
        reporter.Status($"loading the module for {platform.Name()}, {mode.ToString().ToLowerInvariant()}");
        var workspace = await Workspace.LoadAsync(workingDirectory, new BuildContext(mode, platform), TazelVersion,
            reporter.Status).ConfigureAwait(false);
        WarnAboutWhatThisTazelDoesNotDo(workspace.Main.Globals);
        var execution = workspace.Main.Globals.execution?.Settings;
        var buildOptions = new BuildOptions
        {
            Mode = mode,
            Platform = platform,
            Jobs = flags.Jobs ?? execution?.Jobs ?? Environment.ProcessorCount,
            KeepGoing = flags.KeepGoing || (execution?.KeepGoing ?? false),
            FeaturesOn = flags.FeaturesOn,
            FeaturesOff = flags.FeaturesOff,
            CompilerOptions = flags.CompilerOptions,
            CCompilerOptions = flags.CCompilerOptions,
            CppCompilerOptions = flags.CppCompilerOptions,
            LinkerOptions = flags.LinkerOptions,
            ZigCompilerOptions = flags.ZigCompilerOptions,
            Coverage = kind == BuildKind.Coverage,
        };

        var selector = new Selector(workspace, platform);
        var selected = kind switch
        {
            BuildKind.Test or BuildKind.Coverage =>
                selector.Select(labels, workingDirectory, target => target.Kind == TargetKind.Test, "a test"),
            BuildKind.Run => selector.Select(labels, workingDirectory, target => target.IsRunnable, "an executable or a test"),
            _ => selector.Select(labels, workingDirectory, _ => true, "a target"),
        };
        if (kind == BuildKind.Run && selected.Count != 1)
        {
            throw new ProjectException(selected.Count == 0
                ? "tazel run found no executable to run; name one: tazel run //dir:name"
                : $"tazel run runs one program, and these are {selected.Count}: {string.Join(", ", selected.Select(t => t.Label))}");
        }

        if (selector.Skipped > 0)
        {
            reporter.Status($"left out {selector.Skipped} target{(selector.Skipped == 1 ? "" : "s")} that do not build for {platform.Name()}");
        }

        var chooser = new ToolchainChooser(workspace, platform);
        var planner = new Planner(workspace, buildOptions, chooser);
        var planned = selected.Select(target => (Target: target, Products: planner.Plan(target))).ToList();
        planner.CheckCommandLineFeatures(chooser.UsedCC);
        var goals = planned.Select(entry => entry.Products.Main).OfType<BuildAction>().Distinct().ToList();
        reporter.Status($"building {selected.Count} target{(selected.Count == 1 ? "" : "s")}");
        var digests = new Digests();
        var cacheSettings = workspace.Main.Globals.cache?.Settings;
        var executor = new Executor(buildOptions.Jobs, buildOptions.KeepGoing || kind is BuildKind.Test or BuildKind.Coverage,
            new ActionCache(digests, enabled: cacheSettings?.Local ?? true), reporter);
        var built = await executor.RunAsync(goals, cancel).ConfigureAwait(false);
        switch (kind)
        {
            case BuildKind.Test or BuildKind.Coverage:
                var tests = planned.Select(entry => (entry.Target, entry.Products,
                    Built: entry.Products.Main is { } main && executor.Succeeded(main))).ToList();
                var outcomes = await new TestRunner(planner.Layout, buildOptions.Jobs, reporter, buildOptions.Coverage)
                    .RunAsync(tests, cancel).ConfigureAwait(false);
                reporter.TestSummary(outcomes);
                var passed = outcomes.All(outcome => outcome.Status is TestStatus.Passed or TestStatus.Flaky);
                if (kind == BuildKind.Coverage)
                {
                    var reported = await CoverageReport.WriteAsync(planner, chooser, tests, reporter, cancel).ConfigureAwait(false);
                    return passed && reported ? 0 : 1;
                }

                return passed ? 0 : 1;
            case BuildKind.Run:
                if (!built)
                {
                    return 1;
                }

                var (target, products) = planned[0];
                var runfiles = Runfiles.Prepare(target, products, planner.Layout);
                var environment = Processes.Inherited();
                var settings = (IRunSettings)target.Settings;
                foreach (var (name, value) in settings.EnvironmentVariables)
                {
                    environment[name] = value;
                }

                Runfiles.AddLibraryPath(environment, products, planner.Layout);
                var command = new List<string> { planner.Layout.Full(products.Executable!) };
                command.AddRange(settings.Arguments);
                command.AddRange(programArguments ?? []);
                return Processes.RunAttached(command, runfiles, environment);
            default:
                if (!built)
                {
                    reporter.Error(ErrorCodes.ActionsFailed, $"{executor.Failed} action{(executor.Failed == 1 ? "" : "s")} failed", "the FAILED lines above say what each one printed; --keep-going shows every failure at once",
                        Dsl.Custom.Jokes.ForProject($"{executor.Failed}"));
                    return 1;
                }

                reporter.Success($"built {selected.Count} target{(selected.Count == 1 ? "" : "s")}");
                return 0;
        }
    }
}

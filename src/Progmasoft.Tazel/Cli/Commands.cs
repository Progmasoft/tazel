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
    public Task<int> RunAsync(Command command, CancellationToken cancel)
    {
        ArgumentNullException.ThrowIfNull(command);
        return command switch
        {
            BuildCommand build => BuildAsync(build.Labels, build.Flags, BuildKind.Build, null, cancel),
            TestCommand test => BuildAsync(test.Labels, test.Flags, BuildKind.Test, null, cancel),
            CoverageCommand coverage => BuildAsync(coverage.Labels, coverage.Flags, BuildKind.Coverage, null, cancel),
            RunCommand run => BuildAsync(run.Label is null ? [] : [run.Label], run.Flags, BuildKind.Run, run.Arguments, cancel),
            QueryCommand query => QueryAsync(query),
            FetchCommand => FetchAsync(),
            VendorCommand => VendorAsync(cancel),
            PortalModulesCommand portal => PortalModulesAsync(portal, cancel),
            PackageCommand => PackageAsync(),
            CustomCommandLine custom => CustomAsync(custom, cancel),
            _ => Task.FromResult(RunAtOnce(command)),
        };
    }

    /// <summary>The commands that only print or remove, and need nothing to wait for.</summary>
    private int RunAtOnce(Command command)
    {
        switch (command)
        {
            case VersionCommand:
                reporter.Plain($"tazel {TazelVersion.ToString(3)} (.NET {Environment.Version}, {Host.Platform.Name()})");
                return 0;
            case HelpCommand help:
                reporter.Plain(Help.Text(help.Topic, help.Long));
                return 0;
            case ExplainCommand explain:
                Explain(explain);
                return 0;
            case MigrateCommand:
                return Migrate();
            case CleanCommand clean:
                return Clean(clean.Expunge);
            case CacheKeygenCommand keygen:
            {
                var (publicKey, signingKey) = Cache.CacheKey.Generate(keygen.Algorithm);
                reporter.Plain($"PublicKey:  {publicKey}");
                reporter.Plain($"SigningKey: {signingKey}");
                reporter.Status("give the PublicKey to every machine that reads; keep the SigningKey secret, for the machines that write");
                return 0;
            }

            case DaemonCommand daemon:
                reporter.Error(ErrorCodes.NoDaemon,
                    $"tazel dotnet {daemon.Action}: this Tazel runs every command in a process of its own and has no daemon", null,
                    "Tazel is a morning person: awake at once, every time, no daemon needed.");
                return 1;
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
    /// Stops a command that would run what a project brings when a sandbox is asked for: this Tazel runs tazel custom
    /// inside its MicroVM, and builds, tests and plugins not, so nothing runs rather than run on the host.
    /// </summary>
    private void RefuseWithoutSandbox()
    {
        var (_, file) = TazelOptionsFile.Effective(workingDirectory);
        if (file is not null)
        {
            throw new ProjectException(
                $"{file} asks for sandboxing-with-microvm; this Tazel runs tazel custom inside its MicroVM, and not " +
                "builds, tests or plugins, so nothing ran; set it to false to run them without the sandbox");
        }
    }

    /// <summary>Says which settings of MODULE.csx this Tazel reads but does not act on, so none is taken for granted.</summary>
    private void WarnAboutWhatThisTazelDoesNotDo(Dsl.Module.Prelude.ModuleGlobals globals)
    {
        if (globals.execution?.Settings.Remote is { } remote)
        {
            reporter.Warning($"MODULE.csx names the remote execution {remote.Url}; this Tazel runs every action on this machine");
        }

        if (globals.execution?.Settings.Memory is not null)
        {
            reporter.Warning("MODULE.csx sets Execution.Memory; this Tazel limits the actions by Jobs only");
        }
    }

    /// <summary>
    /// tazel limited-time C#: renames the project files of earlier names below the working directory to BUILD.csx and
    /// writes the targets Depends names by their variables as labels; nothing is written when anything is wrong.
    /// </summary>
    private int Migrate()
    {
        var result = Project.CsxMigration.Run(workingDirectory);
        if (result.Problems.Count > 0)
        {
            reporter.Error(ErrorCodes.Project, $"nothing was changed:\n{string.Join('\n', result.Problems)}",
                "fix these and run tazel limited-time C# again", "Measure twice, cut once; Tazel measured and put the saw down.");
            return 1;
        }

        foreach (var renamed in result.Renamed)
        {
            reporter.Plain($"renamed {renamed}");
        }

        foreach (var rewritten in result.Rewritten)
        {
            reporter.Plain($"wrote the Depends of {rewritten} as labels");
        }

        reporter.Success(result.Renamed.Count + result.Rewritten.Count == 0
            ? "nothing to change: every project file is BUILD.csx and names its dependencies by label"
            : $"changed {result.Renamed.Count + result.Rewritten.Count} project file{(result.Renamed.Count + result.Rewritten.Count == 1 ? "" : "s")}");
        return 0;
    }

    private int Clean(bool expunge)
    {
        var root = ModuleFiles.FindRoot(workingDirectory);
        var state = Path.Combine(root, ModuleFiles.StateDirectory);
        var removed = expunge
            ? [state]
            : new[] { "out", "testlogs", "coverage", "zig-cache", "package", "downloads", Project.ScriptCache.Directory }.Select(name => Path.Combine(state, name)).ToArray();
        foreach (var directory in removed.Where(Directory.Exists))
        {
            Directory.Delete(directory, recursive: true);
        }

        reporter.Success(expunge ? $"removed {state}" : "removed the outputs");
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

    private async Task<int> CustomAsync(CustomCommandLine custom, CancellationToken cancel)
    {
        var (options, sandboxFile) = TazelOptionsFile.Effective(workingDirectory);
        var (command, arguments) = FindCustom(custom.Words);
        return sandboxFile is null
            ? CustomOnHost(command, arguments)
            : await CustomInMicroVMAsync(command, arguments, options, sandboxFile, cancel).ConfigureAwait(false);
    }

    /// <summary>The command of tazel_custom.txt the words name, and its arguments, which must fit what it takes.</summary>
    private (CustomCommand Command, IReadOnlyList<string> Arguments) FindCustom(IReadOnlyList<string> words)
    {
        var file = Path.Combine(workingDirectory, CustomCommandFiles.FileName);
        if (!File.Exists(file))
        {
            throw new CustomCommandsException($"there is no {CustomCommandFiles.FileName} in {workingDirectory}");
        }

        var commands = CustomParser.Parse(File.ReadAllText(file));
        if (words.Count > CustomCommandFiles.MaxArguments)
        {
            throw new CommandLineException($"tazel custom takes at most {CustomCommandFiles.MaxArguments} words");
        }

        var (command, arguments) = commands.Matching(words) ?? throw new CustomCommandsException(
            $"{CustomCommandFiles.FileName} has no command '{string.Join(' ', words.TakeWhile(w => w != CustomCommandFiles.NameEnd))}'" +
            (Suggestions.Nearest(string.Join(' ', words), commands.Select(c => c.Name)) is { } near ? $"; did you mean '{near}'?" : ""));
        var fits = arguments.Count == command.Required || (arguments.Count > command.Required && command.TakesAll);
        return fits
            ? (command, arguments)
            : throw new CommandLineException(
                $"'{command.Name}' {(arguments.Count < command.Required ? "needs" : "takes")} {command.Required} arguments, and {arguments.Count} were given");
    }

    /// <summary>Runs the steps on this machine, in order, until one fails.</summary>
    private int CustomOnHost(CustomCommand command, IReadOnlyList<string> arguments)
    {
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

    /// <summary>What a build command works on: its options, its targets, and the planner that planned them.</summary>
    private sealed record Built(
        BuildOptions Options, Planner Planner, ToolchainChooser Chooser, Executor Executor, bool Succeeded,
        List<(ProjectTarget Target, TargetProducts Products)> Planned);

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
        var buildOptions = OptionsOf(flags, workspace.Main.Globals.execution?.Settings, kind, platform, mode);
        var selected = Select(new Selector(workspace, platform), labels, kind, platform);
        var built = await RunBuildAsync(workspace, buildOptions, selected, kind, cancel).ConfigureAwait(false);
        return kind switch
        {
            BuildKind.Test or BuildKind.Coverage => await FinishTestsAsync(built, kind, cancel).ConfigureAwait(false),
            BuildKind.Run => built.Succeeded ? RunProgram(built, programArguments) : 1,
            _ => FinishBuild(built, selected.Count),
        };
    }

    private static BuildOptions OptionsOf(
        BuildFlags flags, Dsl.Module.Prelude.ExecutionSettings? execution, BuildKind kind, Platform platform, BuildMode mode) => new()
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

    /// <summary>The targets of a command: tests for test and coverage, one program for run, anything for build.</summary>
    private List<ProjectTarget> Select(Selector selector, IReadOnlyList<string> labels, BuildKind kind, Platform platform)
    {
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

        return selected;
    }

    private async Task<Built> RunBuildAsync(
        Workspace workspace, BuildOptions buildOptions, List<ProjectTarget> selected, BuildKind kind, CancellationToken cancel)
    {
        var chooser = new ToolchainChooser(workspace, buildOptions.Platform);
        var planner = new Planner(workspace, buildOptions, chooser);
        var planned = selected.Select(target => (Target: target, Products: planner.Plan(target))).ToList();
        planner.CheckCommandLineFeatures(chooser.UsedCC);
        var goals = planned.Select(entry => entry.Products.Main).OfType<BuildAction>().Distinct().ToList();
        reporter.Status($"building {selected.Count} target{(selected.Count == 1 ? "" : "s")}");
        var cacheSettings = workspace.Main.Globals.cache?.Settings;
        var digests = new Digests();
        using var remoteCache = cacheSettings?.Remote is { } remote
            ? new Cache.RemoteCache(remote.Url, remote.Settings.Token)
            : null;
        var remoteActions = remoteCache is null ? null : RemoteActionsOf(remoteCache, cacheSettings!.Remote!.Settings, digests);
        var executor = new Executor(buildOptions.Jobs, buildOptions.KeepGoing || kind is BuildKind.Test or BuildKind.Coverage,
            new ActionCache(digests, enabled: cacheSettings?.Local ?? true), reporter, remoteActions);
        var succeeded = await executor.RunAsync(goals, cancel).ConfigureAwait(false);
        return new Built(buildOptions, planner, chooser, executor, succeeded, planned);
    }

    /// <summary>The remote cache as MODULE.csx sets it up, with its keys read.</summary>
    private Cache.RemoteActions RemoteActionsOf(Cache.RemoteCache cache, Dsl.Module.Prelude.RemoteSettings settings, Digests digests)
    {
        Cache.CacheKey? trusted, signing;
        try
        {
            trusted = settings.PublicKey is { Length: > 0 } text ? Cache.CacheKey.ParsePublic(text, "the PublicKey of the remote cache") : null;
            signing = settings.SigningKey is { Length: > 0 } secret
                ? Cache.CacheKey.ParseSigning(secret, "the SigningKey of the remote cache")
                : null;
        }
        catch (FormatException error)
        {
            throw new Cache.CacheKeyException(error.Message);
        }

        if (settings.Write && trusted is not null && signing is null)
        {
            reporter.Warning("MODULE.csx trusts a PublicKey for the remote cache and writes to it without a SigningKey; " +
                "no one who trusts that key takes what this build writes");
        }

        return new Cache.RemoteActions(cache, settings.Read, settings.Write, digests, reporter.Warning, trusted, signing);
    }

    private async Task<int> FinishTestsAsync(Built built, BuildKind kind, CancellationToken cancel)
    {
        var tests = built.Planned.Select(entry => (entry.Target, entry.Products,
            Built: entry.Products.Main is { } main && built.Executor.Succeeded(main))).ToList();
        var outcomes = await new TestRunner(built.Planner.Layout, built.Options.Jobs, reporter, built.Options.Coverage)
            .RunAsync(tests, cancel).ConfigureAwait(false);
        reporter.TestSummary(outcomes);
        var passed = outcomes.All(outcome => outcome.Status is TestStatus.Passed or TestStatus.Cached or TestStatus.Flaky);
        var reported = kind != BuildKind.Coverage ||
            await CoverageReport.WriteAsync(built.Planner, built.Chooser, tests, reporter, cancel).ConfigureAwait(false);
        return passed && reported ? 0 : 1;
    }

    private static int RunProgram(Built built, IReadOnlyList<string>? programArguments)
    {
        var (target, products) = built.Planned[0];
        var layout = built.Planner.Layout;
        var runfiles = Runfiles.Prepare(target, products, layout);
        var environment = Processes.Inherited();
        var settings = (IRunSettings)target.Settings;
        foreach (var (name, value) in settings.EnvironmentVariables)
        {
            environment[name] = value;
        }

        Runfiles.AddLibraryPath(environment, products, layout);
        List<string> command = [layout.Full(products.Executable!), .. settings.Arguments, .. programArguments ?? []];
        return Processes.RunAttached(command, runfiles, environment);
    }

    private int FinishBuild(Built built, int targets)
    {
        if (!built.Succeeded)
        {
            var failed = built.Executor.Failed;
            reporter.Error(ErrorCodes.ActionsFailed, $"{failed} action{(failed == 1 ? "" : "s")} failed",
                "the FAILED lines above say what each one printed; --keep-going shows every failure at once",
                Dsl.Custom.Jokes.ForProject($"{failed}"));
            return 1;
        }

        reporter.Success($"built {targets} target{(targets == 1 ? "" : "s")}");
        return 0;
    }
}

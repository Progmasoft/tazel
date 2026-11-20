// SPDX-FileCopyrightText: 2026 Progmasoft <support@progmasoft.com>
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Progmasoft.Tazel.Dsl;
using Progmasoft.Tazel.Dsl.Build.Prelude;
using Progmasoft.Tazel.Dsl.Common;
using Progmasoft.Tazel.Project;

namespace Progmasoft.Tazel.Driver;

/// <summary>The Zig part of <see cref="Planner"/>: zig build-exe, build-lib and test on a root file.</summary>
internal sealed partial class Planner
{
    /// <summary>The target of zig's <c>-target</c> for a platform.</summary>
    public static string ZigTriple(Platform platform) => platform switch
    {
        Platform.LinuxAmd64 => "x86_64-linux",
        Platform.LinuxAarch64 => "aarch64-linux",
        Platform.WindowsX64 => "x86_64-windows",
        Platform.WindowsArm64 => "aarch64-windows",
        Platform.MacOSX86_64 => "x86_64-macos",
        Platform.MacOSArm64 => "aarch64-macos",
        Platform.FreeBSDAmd64 => "x86_64-freebsd",
        Platform.FreeBSDAarch64 => "aarch64-freebsd",
        Platform.OpenBSDAmd64 => "x86_64-openbsd",
        Platform.OpenBSDAarch64 => "aarch64-openbsd",
        _ => "x86_64-dragonfly",
    };

    private static string OptimizationName(ZigOptimization optimization) => optimization.ToString();

    private TargetProducts PlanZig(ProjectTarget target)
    {
        var products = new TargetProducts();
        if (target.Kind == TargetKind.Module)
        {
            // A module is compiled by what imports it; it makes nothing of its own.
            return products;
        }

        var settings = (ZigCompiledSettings)target.Settings;
        var zig = Toolchains.Zig();
        var directory = Layout.PackageOut(target);
        var defaultOptimization = Options.Mode == BuildMode.Debug ? ZigOptimization.Debug : ZigOptimization.ReleaseSafe;
        var command = new List<string>(zig.Zig.Command)
        {
            target.Kind switch
            {
                TargetKind.Executable => "build-exe",
                TargetKind.Test => "test",
                _ => "build-lib",
            },
        };
        if (target.Kind == TargetKind.SharedLibrary)
        {
            command.Add("-dynamic");
        }

        command.AddRange(["--cache-dir", Layout.ZigCache]);
        string output;
        Action? after = null;
        var outputs = new List<string>();
        switch (target.Kind)
        {
            case TargetKind.StaticLibrary:
                output = $"{directory}/{StaticLibraryName(target.Label.Name)}";
                products.Library = output;
                break;
            case TargetKind.SharedLibrary:
                var version = ((ZigSharedLibrarySettings)settings).LibraryVersion;
                var (file, linkWith, soname) = SharedLibraryNames(directory, target.Label.Name, version);
                output = file;
                products.Library = linkWith;
                products.IsShared = true;
                if (soname is not null && Options.Platform.GetOS() != OS.Windows)
                {
                    command.Add($"-fsoname={soname}");
                }

                after = SonameLinks(file, soname, target.Label.Name);
                break;
            default:
                output = $"{directory}/{ExecutableName(target.Label.Name)}";
                products.Executable = output;
                break;
        }

        command.Add($"-femit-bin={output}");
        outputs.Add(output);
        var emitHeader = settings switch
        {
            ZigStaticLibrarySettings library => library.EmitHeader,
            ZigSharedLibrarySettings library => library.EmitHeader,
            _ => null,
        };
        if (emitHeader is not null)
        {
            var header = $"{directory}/{FileSets.PathIn(emitHeader, target.PackageDirectory, target.PackageDirectory, target.Place, mustExist: false)}";
            command.Add($"-femit-h={header}");
            outputs.Add(header);
            products.PublicIncludes.Add(Path.GetDirectoryName(header)!.Replace('\\', '/'));
        }

        if (settings.LinkCStandardLibrary)
        {
            command.Add("-lc");
        }

        if (settings.StripSymbols)
        {
            command.Add("-fstrip");
        }

        if (settings.LinkTimeOptimization)
        {
            command.Add("-flto");
        }

        if (settings is ZigTestSettings test)
        {
            if (test.TestFilter is { } filter)
            {
                command.AddRange(["--test-filter", filter]);
            }

            command.Add("--test-no-exec");
        }

        command.AddRange(settings.ZigCompilerOptions);
        command.AddRange(Options.ZigCompilerOptions);

        // The libraries this target links, and the headers of C-family ones for @cImport.
        // zig takes libraries as files and has no -Wl, so a library is linked as it is, AlwaysLink or not.
        var (arguments, libraryInputs, libraryActions, sharedDirectories) = LinkLibraries(target, directory, forZig: true);
        var includeDirectories = new List<string>();
        foreach (var dependency in Closure(target))
        {
            Add(includeDirectories, Plan(dependency).PublicIncludes);
        }

        command.AddRange(arguments);
        foreach (var shared in sharedDirectories)
        {
            command.AddRange(["-rpath", Options.Platform.GetOS() == OS.MacOS ? "@loader_path/" + Relative(directory, shared)
                : "$ORIGIN/" + Relative(directory, shared)]);
        }

        command.AddRange(settings.LinkerOptions);
        command.AddRange(Options.LinkerOptions);

        // Per-module options come before the -M of the module they belong to: first the root module.
        var inputs = new List<string>();
        command.AddRange(ModuleOptions(target, settings, settings.OptimizationMode ?? defaultOptimization, inputs, includeDirectories));
        var cSources = FileSets.Resolve(settings.CSources, target.PackageDirectory, Layout.MainRoot, target.Place);
        AddCSources(command, settings, cSources);
        inputs.AddRange(cSources);
        var modules = ImportedModules(target);
        var names = ModuleNames(modules);
        foreach (var (import, module) in target.ZigImports)
        {
            command.AddRange(["--dep", $"{import}={names[module]}"]);
        }

        command.Add($"-Mroot={RootFile(target)}");
        foreach (var module in modules)
        {
            var moduleSettings = (ZigModuleSettings)module.Settings;
            command.AddRange(ModuleOptions(module, moduleSettings,
                moduleSettings.OptimizationMode ?? settings.OptimizationMode ?? defaultOptimization, inputs, []));
            foreach (var (import, imported) in module.ZigImports)
            {
                command.AddRange(["--dep", $"{import}={names[imported]}"]);
            }

            command.Add($"-M{names[module]}={RootFile(module)}");
        }

        var action = new BuildAction
        {
            Description = $"{(target.Kind == TargetKind.Test ? "Building test" : "Compiling")} {target.Label}",
            Command = command,
            Directory = Layout.MainRoot,
            Inputs = inputs.Concat(libraryInputs).Distinct(StringComparer.Ordinal).ToList(),
            Outputs = outputs,
            Environment = ActionEnvironment,
            After = after,
            MissingOutputHints = outputs.Skip(1).ToDictionary(header => header, _ =>
                "zig accepts -femit-h, and the zig compilers since 0.11 do not write the header; write it by hand and add it to Headers of a CC library"),
        };
        action.Prerequisites.AddRange(libraryActions);
        foreach (var dependency in Closure(target))
        {
            action.Prerequisites.AddRange(Plan(dependency).HeaderActions);
        }

        products.Main = action;
        products.SharedLibraryDirectories.AddRange(sharedDirectories);
        if (emitHeader is not null)
        {
            products.HeaderActions.Add(action);
        }

        if (target.Kind == TargetKind.StaticLibrary)
        {
            products.LinkTail.AddRange(settings.LinkerOptions);
        }

        return products;
    }

    private static string Relative(string from, string to) =>
        Path.GetRelativePath(from, to).Replace('\\', '/') is var relative && relative == "." ? "" : relative;

    private string RootFile(ProjectTarget target) =>
        FileSets.PathIn(((ZigModuleSettings)target.Settings).RootSourceFile, target.PackageDirectory, Layout.MainRoot,
            target.Place, mustExist: true);

    /// <summary>The options of one module: optimization, target, PIC and include directories; adds its files to the inputs.</summary>
    private List<string> ModuleOptions(
        ProjectTarget target, ZigModuleSettings settings, ZigOptimization optimization, List<string> inputs,
        IReadOnlyList<string> includeDirectories)
    {
        // The target is always named, so the output is the same on every machine that builds it: zig's baseline CPU
        // of the platform, never the features of the processor that runs the build.
        var options = new List<string> { "-O", OptimizationName(optimization), "-target", ZigTriple(Options.Platform) };

        if (settings is ZigCompiledSettings { PositionIndependentCode: true })
        {
            options.Add("-fPIC");
        }

        options.AddRange(includeDirectories.Select(include => "-I" + include));
        var root = RootFile(target);
        inputs.Add(root);
        if (settings.Sources.Items.Count > 0)
        {
            inputs.AddRange(FileSets.Resolve(settings.Sources, target.PackageDirectory, Layout.MainRoot, target.Place));
        }
        else
        {
            var rootDirectory = Path.GetDirectoryName(settings.RootSourceFile)!.Replace('\\', '/');
            var pattern = rootDirectory.Length == 0 ? "**/*.zig" : $"{rootDirectory}/**/*.zig";
            inputs.AddRange(FileSets.Resolve(new Glob(pattern), target.PackageDirectory, Layout.MainRoot, target.Place));
        }

        return options;
    }

    private void AddCSources(List<string> command, ZigCompiledSettings settings, IReadOnlyList<string> cSources)
    {
        foreach (var cxx in new[] { false, true })
        {
            var files = cSources.Where(file => LanguageOf(file) is { } language &&
                (language is SourceLanguage.Cpp) == cxx && language != SourceLanguage.Header).ToList();
            if (files.Count == 0)
            {
                continue;
            }

            command.Add("-cflags");
            command.AddRange(ModeFlags().Where(flag => flag != "-O2"));
            command.AddRange(settings.CompilerOptions);
            command.AddRange(cxx ? settings.CppCompilerOptions : settings.CCompilerOptions);
            command.AddRange(Options.CompilerOptions);
            command.AddRange(cxx ? Options.CppCompilerOptions : Options.CCompilerOptions);
            command.Add("--");
            command.AddRange(files);
        }
    }

    /// <summary>Every Zig module <paramref name="target"/> imports, near or far, each once and in a fixed order.</summary>
    private static List<ProjectTarget> ImportedModules(ProjectTarget target)
    {
        var order = new List<ProjectTarget>();
        var seen = new HashSet<ProjectTarget>();
        Visit(target);
        return order;

        void Visit(ProjectTarget from)
        {
            foreach (var module in from.ZigImports.OrderBy(entry => entry.Key, StringComparer.Ordinal).Select(entry => entry.Value))
            {
                if (seen.Add(module))
                {
                    order.Add(module);
                    Visit(module);
                }
            }
        }
    }

    /// <summary>The names the modules have on zig's command line: their target names, made unique when two clash.</summary>
    private static Dictionary<ProjectTarget, string> ModuleNames(List<ProjectTarget> modules)
    {
        var names = new Dictionary<ProjectTarget, string>();
        var clashing = modules.GroupBy(module => module.Label.Name).Where(group => group.Count() > 1)
            .SelectMany(group => group).ToHashSet();
        foreach (var module in modules)
        {
            names[module] = clashing.Contains(module) ? Sanitized(module.Label.ToString().TrimStart('/', '@')) : module.Label.Name;
            if (names[module] == "root")
            {
                names[module] = "root_" + Sanitized(module.Label.Package.Path);
            }
        }

        return names;
    }
}

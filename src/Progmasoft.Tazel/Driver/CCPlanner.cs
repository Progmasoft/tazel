// SPDX-FileCopyrightText: 2026 Progmasoft <support@progmasoft.com>
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Progmasoft.Tazel.Dsl;
using Progmasoft.Tazel.Dsl.Build.Prelude;
using Progmasoft.Tazel.Dsl.Build.Toolchain.Prelude;
using Progmasoft.Tazel.Dsl.Common;
using Progmasoft.Tazel.Project;

namespace Progmasoft.Tazel.Driver;

/// <summary>The languages of the files of a C-family target, from their extensions.</summary>
internal enum SourceLanguage
{
    C,
    Cpp,
    ObjectiveC,
    ObjectiveCpp,
    Assembly,
    Header,
}

/// <summary>The C, C++, Objective-C and Objective-C++ part of <see cref="Planner"/>.</summary>
internal sealed partial class Planner
{
    /// <summary>The language of <paramref name="file"/>, or null when no C-family target compiles it.</summary>
    public static SourceLanguage? LanguageOf(string file) => Path.GetExtension(file) switch
    {
        ".c" => SourceLanguage.C,
        ".cc" or ".cpp" or ".cxx" or ".c++" => SourceLanguage.Cpp,
        ".m" => SourceLanguage.ObjectiveC,
        ".mm" => SourceLanguage.ObjectiveCpp,
        ".s" or ".S" => SourceLanguage.Assembly,
        ".h" or ".hh" or ".hpp" or ".hxx" or ".h++" or ".inc" or ".inl" or ".ipp" or ".tpp" => SourceLanguage.Header,
        _ => null,
    };

    private TargetProducts PlanCC(ProjectTarget target)
    {
        var settings = (CCSettings)target.Settings;
        var toolchain = Toolchains.CC(target.Family);
        var products = new TargetProducts();
        var moduleRoot = FileSets.ToRoot(target.Module.Root, Layout.MainRoot);
        var sources = FileSets.Resolve(settings.Sources, target.PackageDirectory, Layout.MainRoot, target.Place);
        var headers = FileSets.Resolve(settings.Headers, target.PackageDirectory, Layout.MainRoot, target.Place)
            .Concat(FileSets.Resolve(settings.TextualHeaders, target.PackageDirectory, Layout.MainRoot, target.Place))
            .ToList();
        var ownIncludes = settings.IncludeDirectories
            .Select(directory => FileSets.PathIn(directory, target.PackageDirectory, Layout.MainRoot, target.Place, mustExist: true))
            .ToList();

        // Dependents see this target's headers below the root of its module, and below its include directories.
        products.PublicIncludes.Add(moduleRoot);
        products.PublicIncludes.AddRange(ownIncludes);
        products.PublicDefines.AddRange(settings.PreprocessorDefinitions);
        products.PublicHeaders.AddRange(headers);
        var includes = new List<string>(products.PublicIncludes);
        var defines = new List<string>(settings.PreprocessorDefinitions);
        var headerInputs = new List<string>(headers);
        var headerActions = new List<BuildAction>();
        foreach (var dependency in Closure(target))
        {
            var planned = Plan(dependency);
            Add(products.PublicIncludes, planned.PublicIncludes);
            Add(products.PublicDefines, planned.PublicDefines);
            Add(products.PublicHeaders, planned.PublicHeaders);
            products.HeaderActions.AddRange(planned.HeaderActions);
        }

        Add(includes, products.PublicIncludes);
        Add(defines, products.PublicDefines);
        Add(headerInputs, products.PublicHeaders);
        headerActions.AddRange(products.HeaderActions);
        defines.AddRange(settings.LocalPreprocessorDefinitions);
        if (settings is CCExecutableSettings { StampBuildInformation: true })
        {
            defines.Add($"TAZEL_BUILD_LABEL=\"{target.Label}\"");
            defines.Add($"TAZEL_BUILD_VERSION=\"{target.Module.Info.Version}\"");
        }

        var features = EnabledFeatures(target, toolchain, settings.Features);
        var directory = Layout.PackageOut(target);
        var objectsDirectory = $"{directory}/_objs/{target.Label.Name}";
        var packageRoot = FileSets.ToRoot(target.PackageDirectory, Layout.MainRoot);
        var pic = target.Kind != TargetKind.Executable && target.Kind != TargetKind.Test &&
            Options.Platform.GetOS() != OS.Windows;
        var objc = settings as IObjectiveCSettings;
        var nonArc = objc is null
            ? new HashSet<string>(StringComparer.Ordinal)
            : FileSets.Resolve(objc.NonAutomaticReferenceCountingSources, target.PackageDirectory, Layout.MainRoot, target.Place)
                .ToHashSet(StringComparer.Ordinal);
        var objects = new List<string>();
        var compiles = new List<BuildAction>();
        foreach (var source in sources.Concat(nonArc.Where(file => !sources.Contains(file))))
        {
            var language = LanguageOf(source) ?? throw new ProjectException(
                $"{target.Place}: {target.Label} cannot compile '{source}'; its extension is not C, C++, Objective-C or assembly");
            if (language == SourceLanguage.Header)
            {
                headerInputs.Add(source);
                continue;
            }

            if (language is SourceLanguage.ObjectiveC or SourceLanguage.ObjectiveCpp && target.Family != Family.ObjectiveC)
            {
                throw new ProjectException($"{target.Place}: '{source}' is Objective-C; declare {target.Label} with ObjectiveC");
            }

            var relative = source.StartsWith(packageRoot + "/", StringComparison.Ordinal) ? source[(packageRoot.Length + 1)..] : source;
            var objectFile = $"{objectsDirectory}/{relative}.o";
            var cxx = language is SourceLanguage.Cpp or SourceLanguage.ObjectiveCpp;
            var action = language switch
            {
                SourceLanguage.C => CCAction.CCompile,
                SourceLanguage.Cpp => CCAction.CppCompile,
                SourceLanguage.ObjectiveC => CCAction.ObjectiveCCompile,
                SourceLanguage.ObjectiveCpp => CCAction.ObjectiveCppCompile,
                _ => CCAction.Assemble,
            };
            var command = new List<string>((cxx ? toolchain.Cpp : toolchain.C).Command);
            command.AddRange(ModeFlags());
            command.AddRange(Flags(features, action));
            if (pic)
            {
                command.Add("-fPIC");
            }

            command.AddRange(includes.Select(include => "-I" + include));
            command.AddRange(defines.Select(define => "-D" + define));
            command.AddRange(settings.CompilerOptions);
            command.AddRange(cxx ? settings.CppCompilerOptions : settings.CCompilerOptions);
            if (objc is not null && language is SourceLanguage.ObjectiveC or SourceLanguage.ObjectiveCpp)
            {
                command.Add(nonArc.Contains(source) ? "-fno-objc-arc" : "-fobjc-arc");
            }

            if (objc is { EnableClangModules: true })
            {
                command.Add("-fmodules");
                if (objc.ModuleName is { } moduleName)
                {
                    command.Add($"-fmodule-name={moduleName}");
                }

                if (objc.ModuleMap is { } map)
                {
                    command.Add("-fmodule-map-file=" +
                        FileSets.PathIn(map, target.PackageDirectory, Layout.MainRoot, target.Place, mustExist: true));
                }
            }

            if (Options.Coverage)
            {
                RequireClang(toolchain, target);
                command.AddRange(["-fprofile-instr-generate", "-fcoverage-mapping"]);
            }

            command.AddRange(Options.CompilerOptions);
            command.AddRange(cxx ? Options.CppCompilerOptions : Options.CCompilerOptions);
            command.AddRange(["-MD", "-MF", objectFile + ".d", "-c", source, "-o", objectFile]);
            var compile = new BuildAction
            {
                Description = $"Compiling {target.Label} {relative}",
                Command = command,
                Directory = Layout.MainRoot,
                Inputs = headerInputs.Prepend(source).Distinct(StringComparer.Ordinal).ToList(),
                Outputs = [objectFile],
                Environment = ActionEnvironment,
                Depfile = objectFile + ".d",
            };
            compile.Prerequisites.AddRange(headerActions);
            compiles.Add(compile);
            objects.Add(objectFile);
        }

        products.LinkTail.AddRange(settings.LinkerOptions);
        products.LinkTail.AddRange(settings.SdkFrameworks.SelectMany(framework => new[] { "-framework", framework }));
        products.LinkTail.AddRange(settings.WeakSdkFrameworks.SelectMany(framework => new[] { "-weak_framework", framework }));
        products.LinkTail.AddRange(settings.SdkDynamicLibraries.Select(library => "-l" + library));
        switch (target.Kind)
        {
            case TargetKind.StaticLibrary:
                PlanArchive(target, toolchain, features, products, directory, objects, compiles);
                break;
            case TargetKind.SharedLibrary:
                PlanLink(target, toolchain, features, products, directory, objects, compiles, shared: true);
                break;
            default:
                PlanLink(target, toolchain, features, products, directory, objects, compiles, shared: false);
                break;
        }

        return products;
    }

    private void PlanArchive(
        ProjectTarget target, CCToolchain toolchain, IReadOnlyList<Feature> features, TargetProducts products,
        string directory, List<string> objects, List<BuildAction> compiles)
    {
        var settings = (CCStaticLibrarySettings)target.Settings;
        var library = $"{directory}/{StaticLibraryName(target.Label.Name)}";
        var command = new List<string>(toolchain.Archiver.Command);
        if (toolchain.ArchiverIsLibtool)
        {
            command.AddRange(Flags(features, CCAction.CppLinkStaticLibrary));
            command.AddRange(["-static", "-o", library]);
        }
        else
        {
            // Apple's ar has no D; ZERO_AR_DATE in the environment of the actions makes it deterministic instead.
            var appleAr = Host.Platform.GetOS() == OS.MacOS &&
                Path.GetFileNameWithoutExtension(toolchain.Archiver.Program) == "ar";
            command.Add(appleAr ? "rcs" : "rcsD");
            command.AddRange(Flags(features, CCAction.CppLinkStaticLibrary));
            command.Add(library);
        }

        command.AddRange(objects);
        var archive = new BuildAction
        {
            Description = $"Archiving {target.Label}",
            Command = command,
            Directory = Layout.MainRoot,
            Inputs = objects,
            Outputs = [library],
            Environment = ActionEnvironment,
        };
        archive.Prerequisites.AddRange(compiles);
        products.Main = archive;
        products.Library = library;
        products.AlwaysLink = settings.AlwaysLink;
    }

    private void PlanLink(
        ProjectTarget target, CCToolchain toolchain, IReadOnlyList<Feature> features, TargetProducts products,
        string directory, List<string> objects, List<BuildAction> compiles, bool shared)
    {
        var settings = (CCSettings)target.Settings;
        var command = new List<string>(toolchain.Linker.Command);
        command.AddRange(ModeFlags());
        command.AddRange(Flags(features, shared ? CCAction.CppLinkSharedLibrary : CCAction.CppLinkExecutable));
        string output;
        Action? after = null;
        if (shared)
        {
            var version = ((CCSharedLibrarySettings)settings).LibraryVersion;
            var (file, linkWith, soname) = SharedLibraryNames(directory, target.Label.Name, version);
            output = file;
            var os = Options.Platform.GetOS();
            command.Add(os == OS.MacOS ? "-dynamiclib" : "-shared");
            if (os == OS.MacOS)
            {
                command.AddRange(["-install_name", $"@rpath/{soname}"]);
                if (version is not null)
                {
                    command.AddRange(["-current_version", version.ToString(Math.Min(3, FieldCount(version))),
                        "-compatibility_version", $"{version.Major}.0.0"]);
                }
            }
            else if (os != OS.Windows && soname is not null)
            {
                command.Add($"-Wl,-soname,{soname}");
            }

            products.Library = linkWith;
            products.IsShared = true;
            after = SonameLinks(file, soname, target.Label.Name);
        }
        else
        {
            output = $"{directory}/{ExecutableName(target.Label.Name)}";
            products.Executable = output;
            if (settings is CCExecutableSettings { LinkingMode: LinkingMode.FullyStatic })
            {
                command.Add("-static");
            }
        }

        command.AddRange(["-o", output]);
        command.AddRange(objects);
        var (arguments, inputs, actions, directories) = LinkLibraries(target, directory);
        command.AddRange(arguments);
        command.AddRange(settings.LinkerOptions);
        command.AddRange(settings.SdkFrameworks.SelectMany(framework => new[] { "-framework", framework }));
        command.AddRange(settings.WeakSdkFrameworks.SelectMany(framework => new[] { "-weak_framework", framework }));
        command.AddRange(settings.SdkDynamicLibraries.Select(library => "-l" + library));
        var extra = settings switch
        {
            CCSharedLibrarySettings library => library.AdditionalLinkerInputs,
            CCExecutableSettings program => program.AdditionalLinkerInputs,
            _ => Files.None,
        };
        var extraFiles = FileSets.Resolve(extra, target.PackageDirectory, Layout.MainRoot, target.Place);
        if (Options.Coverage)
        {
            command.Add("-fprofile-instr-generate");
        }

        command.AddRange(Options.LinkerOptions);
        var link = new BuildAction
        {
            Description = $"Linking {target.Label}",
            Command = command,
            Directory = Layout.MainRoot,
            Inputs = objects.Concat(inputs).Concat(extraFiles).Distinct(StringComparer.Ordinal).ToList(),
            Outputs = shared && products.Library != output ? [output, products.Library!] : [output],
            Environment = ActionEnvironment,
            After = after,
        };
        link.Prerequisites.AddRange(compiles);
        link.Prerequisites.AddRange(actions);
        products.Main = link;
        products.SharedLibraryDirectories.AddRange(directories);
        if (shared)
        {
            // What links this library still needs the shared libraries it loads.
            products.LinkTail.Clear();
        }
    }

    /// <summary>The features of <paramref name="toolchain"/> that are on for <paramref name="target"/>.</summary>
    private List<Feature> EnabledFeatures(ProjectTarget target, CCToolchain toolchain, IReadOnlyList<FeatureToggle> toggles)
    {
        var on = toolchain.Features.Where(feature => feature.Settings.Enabled).Select(feature => feature.Name)
            .ToHashSet(StringComparer.Ordinal);
        on.UnionWith(Options.FeaturesOn);
        on.ExceptWith(Options.FeaturesOff);
        foreach (var toggle in toggles)
        {
            if (toolchain.Features.All(feature => feature.Name != toggle.Name))
            {
                throw new ProjectException(
                    $"{target.Place}: the toolchain {toolchain.Name} has no feature '{toggle.Name}'");
            }

            if (toggle.Enabled)
            {
                on.Add(toggle.Name);
            }
            else
            {
                on.Remove(toggle.Name);
            }
        }

        return toolchain.Features.Where(feature => on.Contains(feature.Name)).ToList();
    }

    /// <summary>LLVM source-based coverage needs clang; gcc and zig cc write another format.</summary>
    private static void RequireClang(CCToolchain toolchain, ProjectTarget target)
    {
        if (!Path.GetFileNameWithoutExtension(toolchain.C.Program).Contains("clang", StringComparison.Ordinal) ||
            toolchain.C.Prefix.Count > 0)
        {
            throw new ProjectException(
                $"tazel coverage needs clang, and {target.Label} is built with {toolchain.C} of {toolchain.Name}");
        }
    }

    private static IEnumerable<string> Flags(IReadOnlyList<Feature> features, CCAction action) =>
        features.SelectMany(feature => feature.Settings.FlagSets)
            .Where(set => set.Actions.Contains(action))
            .SelectMany(set => set.Flags);

    private IEnumerable<string> ModeFlags() =>
        Options.Mode == BuildMode.Debug ? ["-g"] : ["-O2", "-DNDEBUG"];

    /// <summary>Every target <paramref name="target"/> depends on, near or far, each once.</summary>
    private static List<ProjectTarget> Closure(ProjectTarget target)
    {
        var seen = new HashSet<ProjectTarget>();
        var order = new List<ProjectTarget>();
        var stack = new Stack<ProjectTarget>(target.Dependencies.AsEnumerable().Reverse());
        while (stack.Count > 0)
        {
            var next = stack.Pop();
            if (!seen.Add(next))
            {
                continue;
            }

            order.Add(next);
            foreach (var deeper in next.Dependencies.AsEnumerable().Reverse())
            {
                stack.Push(deeper);
            }
        }

        return order;
    }

    private static void Add(List<string> into, IEnumerable<string> items)
    {
        foreach (var item in items)
        {
            if (!into.Contains(item))
            {
                into.Add(item);
            }
        }
    }
}

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

    /// <summary>What every compilation of one target shares.</summary>
    private sealed record CompileContext(
        ProjectTarget Target, CCSettings Settings, CCToolchain Toolchain, IReadOnlyList<Feature> Features,
        List<string> Includes, List<string> Defines, List<string> HeaderInputs, List<BuildAction> HeaderActions,
        string ObjectsDirectory, string PackageRoot, bool Pic, IObjectiveCSettings? ObjC, HashSet<string> NonArc);

    private TargetProducts PlanCC(ProjectTarget target)
    {
        var settings = (CCSettings)target.Settings;
        var toolchain = Toolchains.CC(target.Family);
        var products = new TargetProducts();
        var sources = FileSets.Resolve(settings.Sources, target.PackageDirectory, Layout.MainRoot, target.Place);
        var context = ContextOf(target, settings, toolchain, products);
        var objects = new List<string>();
        var compiles = new List<BuildAction>();
        foreach (var source in sources.Concat(context.NonArc.Where(file => !sources.Contains(file))))
        {
            if (Compile(context, source) is { } compile)
            {
                compiles.Add(compile);
                objects.Add(compile.Outputs[0]);
            }
        }

        products.LinkTail.AddRange(settings.LinkerOptions);
        products.LinkTail.AddRange(settings.SdkFrameworks.SelectMany(framework => new[] { "-framework", framework }));
        products.LinkTail.AddRange(settings.WeakSdkFrameworks.SelectMany(framework => new[] { "-weak_framework", framework }));
        products.LinkTail.AddRange(settings.SdkDynamicLibraries.Select(library => "-l" + library));
        var directory = Layout.PackageOut(target);
        if (target.Kind == TargetKind.StaticLibrary)
        {
            PlanArchive(target, toolchain, context.Features, products, directory, objects, compiles);
        }
        else
        {
            PlanLink(target, toolchain, context.Features, products, directory, objects, compiles,
                shared: target.Kind == TargetKind.SharedLibrary);
        }

        return products;
    }

    /// <summary>
    /// The includes, macros and headers of a target and of what it depends on; dependents see its headers below the
    /// root of its module and below its include directories.
    /// </summary>
    private CompileContext ContextOf(ProjectTarget target, CCSettings settings, CCToolchain toolchain, TargetProducts products)
    {
        var headers = FileSets.Resolve(settings.Headers, target.PackageDirectory, Layout.MainRoot, target.Place)
            .Concat(FileSets.Resolve(settings.TextualHeaders, target.PackageDirectory, Layout.MainRoot, target.Place))
            .ToList();
        products.PublicIncludes.Add(FileSets.ToRoot(target.Module.Root, Layout.MainRoot));
        // "." is the package's own directory, as Bazel's includes = ["."]; every other entry is a path below it.
        products.PublicIncludes.AddRange(settings.IncludeDirectories
            .Select(directory => directory == "."
                ? FileSets.ToRoot(target.PackageDirectory, Layout.MainRoot)
                : FileSets.PathIn(directory, target.PackageDirectory, Layout.MainRoot, target.Place, mustExist: true)));
        products.PublicDefines.AddRange(settings.PreprocessorDefinitions);
        products.PublicHeaders.AddRange(headers);
        var includes = new List<string>(products.PublicIncludes);
        var defines = new List<string>(settings.PreprocessorDefinitions);
        var headerInputs = new List<string>(headers);
        foreach (var planned in Closure(target).Select(Plan))
        {
            Add(products.PublicIncludes, planned.PublicIncludes);
            Add(products.PublicDefines, planned.PublicDefines);
            Add(products.PublicHeaders, planned.PublicHeaders);
            products.HeaderActions.AddRange(planned.HeaderActions);
        }

        Add(includes, products.PublicIncludes);
        Add(defines, products.PublicDefines);
        Add(headerInputs, products.PublicHeaders);
        defines.AddRange(settings.LocalPreprocessorDefinitions);
        if (settings is CCExecutableSettings { StampBuildInformation: true })
        {
            defines.Add($"TAZEL_BUILD_LABEL=\"{target.Label}\"");
            defines.Add($"TAZEL_BUILD_VERSION=\"{target.Module.Info.Version}\"");
        }

        var objc = settings as IObjectiveCSettings;
        var nonArc = objc is null
            ? new HashSet<string>(StringComparer.Ordinal)
            : FileSets.Resolve(objc.NonAutomaticReferenceCountingSources, target.PackageDirectory, Layout.MainRoot, target.Place)
                .ToHashSet(StringComparer.Ordinal);
        var pic = target.Kind is not (TargetKind.Executable or TargetKind.Test) && Options.Platform.GetOS() != OS.Windows;
        return new CompileContext(target, settings, toolchain, EnabledFeatures(target, toolchain, settings.Features), includes, defines,
            headerInputs, [.. products.HeaderActions], $"{Layout.PackageOut(target)}/_objs/{target.Label.Name}",
            FileSets.ToRoot(target.PackageDirectory, Layout.MainRoot), pic, objc, nonArc);
    }

    /// <summary>The compilation of one source, or null for a header, which later compilations read.</summary>
    private BuildAction? Compile(CompileContext context, string source)
    {
        var target = context.Target;
        var language = LanguageOf(source) ?? throw new ProjectException(
            $"{target.Place}: {target.Label} cannot compile '{source}'; its extension is not C, C++, Objective-C or assembly");
        if (language == SourceLanguage.Header)
        {
            context.HeaderInputs.Add(source);
            return null;
        }

        if (language is SourceLanguage.ObjectiveC or SourceLanguage.ObjectiveCpp && target.Family != Family.ObjectiveC)
        {
            throw new ProjectException($"{target.Place}: '{source}' is Objective-C; declare {target.Label} with ObjectiveC");
        }

        var relative = source.StartsWith(context.PackageRoot + "/", StringComparison.Ordinal) ? source[(context.PackageRoot.Length + 1)..] : source;
        var objectFile = $"{context.ObjectsDirectory}/{relative}.o";
        var cxx = language is SourceLanguage.Cpp or SourceLanguage.ObjectiveCpp;
        var settings = context.Settings;
        var compiler = cxx ? context.Toolchain.Cpp : context.Toolchain.C;
        var command = new List<string>(compiler.Command);
        command.AddRange(ModeFlags());
        command.AddRange(ReproducibleCompileFlags(compiler));
        command.AddRange(Flags(context.Features, ActionOf(language)));
        if (context.Pic)
        {
            command.Add("-fPIC");
        }

        command.AddRange(context.Includes.Select(include => "-I" + include));
        command.AddRange(context.Defines.Select(define => "-D" + define));
        command.AddRange(settings.CompilerOptions);
        command.AddRange(cxx ? settings.CppCompilerOptions : settings.CCompilerOptions);
        command.AddRange(ObjectiveCFlags(context, language, source));
        command.AddRange(CoverageFlags(context));
        command.AddRange(Options.CompilerOptions);
        command.AddRange(cxx ? Options.CppCompilerOptions : Options.CCompilerOptions);
        command.AddRange(["-MD", "-MF", objectFile + ".d", "-c", source, "-o", objectFile]);
        var compile = new BuildAction
        {
            Kind = ActionKind.Compile,
            Description = $"Compiling {target.Label} {relative}",
            Command = command,
            Directory = Layout.MainRoot,
            Inputs = context.HeaderInputs.Prepend(source).Distinct(StringComparer.Ordinal).ToList(),
            Outputs = [objectFile],
            Environment = ActionEnvironment,
            Depfile = objectFile + ".d",
        };
        compile.Prerequisites.AddRange(context.HeaderActions);
        return compile;
    }

    private static CCAction ActionOf(SourceLanguage language) => language switch
    {
        SourceLanguage.C => CCAction.CCompile,
        SourceLanguage.Cpp => CCAction.CppCompile,
        SourceLanguage.ObjectiveC => CCAction.ObjectiveCCompile,
        SourceLanguage.ObjectiveCpp => CCAction.ObjectiveCppCompile,
        _ => CCAction.Assemble,
    };

    /// <summary>Automatic reference counting per file, and clang modules, for an Objective-C target.</summary>
    private List<string> ObjectiveCFlags(CompileContext context, SourceLanguage language, string source)
    {
        var flags = new List<string>();
        var objc = context.ObjC;
        if (objc is null)
        {
            return flags;
        }

        if (language is SourceLanguage.ObjectiveC or SourceLanguage.ObjectiveCpp)
        {
            flags.Add(context.NonArc.Contains(source) ? "-fno-objc-arc" : "-fobjc-arc");
        }

        if (!objc.EnableClangModules)
        {
            return flags;
        }

        flags.Add("-fmodules");
        if (objc.ModuleName is { } moduleName)
        {
            flags.Add($"-fmodule-name={moduleName}");
        }

        if (objc.ModuleMap is { } map)
        {
            var target = context.Target;
            flags.Add("-fmodule-map-file=" + FileSets.PathIn(map, target.PackageDirectory, Layout.MainRoot, target.Place, mustExist: true));
        }

        return flags;
    }

    private string[] CoverageFlags(CompileContext context)
    {
        if (!Options.Coverage)
        {
            return [];
        }

        RequireClang(context.Toolchain, context.Target);
        return ["-fprofile-instr-generate", "-fcoverage-mapping"];
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
            Kind = ActionKind.Archive,
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

    /// <summary>The flags and names of a shared library: its file, what dependents link with, its soname links.</summary>
    private (string Output, Action? After) SharedLibraryOutput(
        ProjectTarget target, CCSharedLibrarySettings settings, TargetProducts products, string directory, List<string> command)
    {
        var version = settings.LibraryVersion;
        var (file, linkWith, soname) = SharedLibraryNames(directory, target.Label.Name, version);
        var os = Options.Platform.GetOS();
        if (os == OS.MacOS)
        {
            command.AddRange(["-dynamiclib", "-install_name", $"@rpath/{soname}"]);
            if (version is not null)
            {
                command.AddRange(["-current_version", version.ToString(Math.Min(3, FieldCount(version))),
                    "-compatibility_version", $"{version.Major}.0.0"]);
            }
        }
        else
        {
            command.Add("-shared");
            if (os != OS.Windows && soname is not null)
            {
                command.Add($"-Wl,-soname,{soname}");
            }
        }

        products.Library = linkWith;
        products.IsShared = true;
        return (file, SonameLinks(file, soname, target.Label.Name));
    }

    /// <summary>The file and flags of an executable.</summary>
    private string ExecutableOutput(
        ProjectTarget target, CCSettings settings, TargetProducts products, string directory, List<string> command)
    {
        var output = $"{directory}/{ExecutableName(target.Label.Name)}";
        products.Executable = output;
        if (settings is CCExecutableSettings { LinkingMode: LinkingMode.FullyStatic })
        {
            command.Add("-static");
        }

        return output;
    }

    private void PlanLink(
        ProjectTarget target, CCToolchain toolchain, IReadOnlyList<Feature> features, TargetProducts products,
        string directory, List<string> objects, List<BuildAction> compiles, bool shared)
    {
        var settings = (CCSettings)target.Settings;
        var command = new List<string>(toolchain.Linker.Command);
        command.AddRange(ModeFlags());
        command.AddRange(ReproducibleLinkFlags(toolchain.Linker));
        command.AddRange(Flags(features, shared ? CCAction.CppLinkSharedLibrary : CCAction.CppLinkExecutable));
        var (output, after) = shared
            ? SharedLibraryOutput(target, (CCSharedLibrarySettings)settings, products, directory, command)
            : (ExecutableOutput(target, settings, products, directory, command), null);

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
            Kind = ActionKind.Link,
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

    /// <summary>
    /// Keeps a compiler from writing the time into an object. Clang targeting Windows stamps every COFF object for
    /// incremental linking unless told not to, so the same source would give another object, and the archive, the
    /// program and its tests after it would run again. GCC takes SOURCE_DATE_EPOCH from the action environment.
    /// </summary>
    private IEnumerable<string> ReproducibleCompileFlags(Tool compiler) =>
        Options.Platform.GetOS() == OS.Windows && IsClang(compiler) ? ["-mno-incremental-linker-compatible"] : [];

    /// <summary>Keeps a linker from writing the time into a Windows program or library.</summary>
    private IEnumerable<string> ReproducibleLinkFlags(Tool linker)
    {
        if (Options.Platform.GetOS() != OS.Windows)
        {
            return [];
        }

        if (IsClang(linker))
        {
            // link.exe and lld-link take /Brepro; the MinGW driver links with ld, which has its own switch.
            return TargetsMinGW(linker) ? ["-Wl,--no-insert-timestamp"] : ["-Wl,/Brepro"];
        }

        var name = Path.GetFileNameWithoutExtension(linker.Program);
        return name.EndsWith("gcc", StringComparison.Ordinal) || name.EndsWith("g++", StringComparison.Ordinal)
            ? ["-Wl,--no-insert-timestamp"]
            : [];
    }

    private static bool IsClang(Tool tool) =>
        Path.GetFileNameWithoutExtension(tool.Program).Contains("clang", StringComparison.Ordinal);

    private static bool TargetsMinGW(Tool tool) =>
        Path.GetFileNameWithoutExtension(tool.Program).Contains("mingw", StringComparison.Ordinal) ||
        tool.Prefix.Any(argument => argument.Contains("-gnu", StringComparison.Ordinal) ||
            argument.Contains("mingw", StringComparison.Ordinal));

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

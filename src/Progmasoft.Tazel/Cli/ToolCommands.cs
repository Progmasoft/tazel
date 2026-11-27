// SPDX-FileCopyrightText: 2026 Progmasoft <support@progmasoft.com>
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using Progmasoft.Tazel.Driver;

namespace Progmasoft.Tazel.Cli;

/// <summary><c>tazel tool launch &lt;name&gt; [arguments]</c>: runs <c>tazel-&lt;name&gt;</c> with the arguments as given.</summary>
internal sealed record ToolLaunchCommand(OutputOptions Output, string Name, IReadOnlyList<string> Arguments) : Command(Output);

/// <summary><c>tazel tool create-wrapper &lt;name&gt; [-o dir]</c>: writes <c>tazel-&lt;name&gt;</c>, which runs <c>&lt;name&gt;</c>.</summary>
internal sealed record ToolWrapperCommand(OutputOptions Output, string Name, string? OutputDirectory) : Command(Output);

/// <summary>A tool Tazel cannot find or start, or an argument it cannot pass on as written.</summary>
internal sealed class ToolException : Exception
{
    public ToolException(string message)
        : base(message)
    {
    }

    public ToolException()
    {
    }

    public ToolException(string message, Exception inner)
        : base(message, inner)
    {
    }
}

/// <summary>
/// External tools: programs named <c>tazel-&lt;name&gt;</c> on <c>PATH</c>, or the one <c>TAZEL_TOOL_EXECUTABLE</c>
/// names. A tool runs as a process of its own, with Tazel's directory, environment and terminal, and its exit status is
/// Tazel's. No shell line is ever built from its arguments.
/// </summary>
internal static class Tools
{
    /// <summary>The variable that names the executable to run instead of searching <c>PATH</c>.</summary>
    public static string ExecutableVariable { get; } = "TAZEL_TOOL_EXECUTABLE";

    /// <summary>
    /// Whether <paramref name="name"/> can name a tool: a letter or digit, then letters, digits, '.', '_' or '-',
    /// with no '..', at most 64 characters, so <c>tazel-&lt;name&gt;</c> is one plain file name and a safe word in a
    /// shell or batch line.
    /// </summary>
    public static bool IsName(string name) =>
        name.Length is > 0 and <= 64 && char.IsAsciiLetterOrDigit(name[0]) && !name.Contains("..", StringComparison.Ordinal) &&
        name.All(letter => char.IsAsciiLetterOrDigit(letter) || letter is '.' or '_' or '-');

    private static void CheckName(string name)
    {
        if (!IsName(name))
        {
            throw new ToolException(
                $"'{name}' is not a tool name: a letter or digit, then letters, digits, '.', '_' or '-', with no '..' and no path");
        }
    }

    /// <summary>
    /// The executable of the tool <paramref name="name"/>: <c>TAZEL_TOOL_EXECUTABLE</c> when it is set, with no
    /// search after it, else the first <c>tazel-&lt;name&gt;</c> of <c>PATH</c> that this user may run. Empty entries,
    /// <c>.</c> and entries that are the working directory are skipped, and nothing else is searched.
    /// </summary>
    public static string Find(string name, string workingDirectory)
    {
        CheckName(name);
        var chosen = Environment.GetEnvironmentVariable(ExecutableVariable);
        if (chosen is not null)
        {
            return Chosen(chosen, workingDirectory);
        }

        var here = Path.TrimEndingDirectorySeparator(Path.GetFullPath(workingDirectory));
        foreach (var entry in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            if (entry.Length == 0 || entry == "." || !Path.IsPathFullyQualified(entry) ||
                string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(entry)), here, PathComparison))
            {
                continue;
            }

            foreach (var file in Candidates(Path.Combine(entry, $"tazel-{name}")))
            {
                if (IsRunnable(file))
                {
                    return file;
                }
            }
        }

        throw new ToolException(
            $"no tazel-{name} on PATH that this user may run; put one in a directory of PATH, make one with " +
            $"tazel tool create-wrapper {name}, or name it with {ExecutableVariable}");
    }

    private static StringComparison PathComparison =>
        OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    // TAZEL_TOOL_EXECUTABLE: a path, absolute or from the working directory; anything wrong with it is an error.
    private static string Chosen(string chosen, string workingDirectory)
    {
        if (chosen.Trim().Length == 0)
        {
            throw new ToolException($"{ExecutableVariable} is set but empty; name an executable, or unset it to search PATH");
        }

        string file;
        try
        {
            file = Path.GetFullPath(chosen, workingDirectory);
        }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new ToolException($"{ExecutableVariable}='{chosen}' is not a path: {error.Message}", error);
        }

        if (!File.Exists(file))
        {
            throw new ToolException($"{ExecutableVariable}='{chosen}' is not a file ({file})");
        }

        return IsRunnable(file) ? file
            : throw new ToolException($"{ExecutableVariable}='{chosen}' is not a program this user may run ({file})");
    }

    // The names a program may have: as written on Unix; with each extension of PATHEXT on Windows.
    private static IEnumerable<string> Candidates(string stem)
    {
        if (!OperatingSystem.IsWindows())
        {
            return [stem];
        }

        var extensions = (Environment.GetEnvironmentVariable("PATHEXT") ?? ".COM;.EXE;.BAT;.CMD")
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(extension => extension.StartsWith('.'));
        return extensions.Select(extension => stem + extension.ToLowerInvariant());
    }

    /// <summary>
    /// Whether this user may run <paramref name="file"/>: on Unix what <c>access(X_OK)</c> says, which weighs owner,
    /// group, others and the user's rights; on Windows a native program or batch file by its extension.
    /// </summary>
    internal static bool IsRunnable(string file)
    {
        try
        {
            if ((File.GetAttributes(file) & FileAttributes.Directory) != 0)
            {
                return false;
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return false;
        }

        return OperatingSystem.IsWindows()
            ? Path.GetExtension(file).ToLowerInvariant() is ".exe" or ".com" or ".bat" or ".cmd"
            : Access(file, 1) == 0;
    }

    [DllImport("libc", EntryPoint = "access", SetLastError = true, CharSet = CharSet.Ansi, BestFitMapping = false, ThrowOnUnmappableChar = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static extern int Access(string path, int mode);

    /// <summary>
    /// The command that runs <paramref name="file"/> with <paramref name="arguments"/>: the program itself, or for a
    /// batch file cmd.exe with a line in which every argument is quoted. cmd.exe expands <c>%</c> and ends a quoted
    /// word at <c>"</c> whatever stands around them, so an argument with either, or with a line break or NUL, is
    /// refused rather than changed.
    /// </summary>
    public static IReadOnlyList<string> Command(string file, IReadOnlyList<string> arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        if (!IsBatch(file))
        {
            return [file, .. arguments];
        }

        var unsafeArgument = arguments.Select((argument, index) => (argument, index))
            .FirstOrDefault(entry => !IsBatchSafe(entry.argument));
        if (unsafeArgument.argument is not null)
        {
            throw new ToolException(
                $"argument {unsafeArgument.index + 1} ('{unsafeArgument.argument}') holds \", %, a line break or NUL, which a batch " +
                "file cannot be given as written; Tazel does not change it, so nothing ran");
        }

        if (!IsBatchSafe(file))
        {
            throw new ToolException($"the path of the batch file {file} holds \" or %, which cmd.exe cannot be given as written");
        }

        var line = new StringBuilder("\"\"").Append(file).Append('"');
        foreach (var argument in arguments)
        {
            line.Append(" \"").Append(argument).Append('"');
        }

        line.Append('"');
        var cmd = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe");
        return [cmd, "/d", "/s", "/c", line.ToString()];
    }

    private static bool IsBatch(string file) =>
        OperatingSystem.IsWindows() && Path.GetExtension(file).ToLowerInvariant() is ".bat" or ".cmd";

    internal static bool IsBatchSafe(string text) => text.IndexOfAny(['"', '%', '\r', '\n', '\0']) < 0;

    /// <summary>Writes the wrapper of <paramref name="name"/> in <paramref name="directory"/>, which must exist, and returns it.</summary>
    public static string CreateWrapper(string name, string directory, bool windows)
    {
        CheckName(name);
        var full = Path.GetFullPath(directory);
        if (!Directory.Exists(full))
        {
            throw new ToolException($"{full} is not a directory; make it first, Tazel does not");
        }

        var file = Path.Join(full, windows ? $"tazel-{name}.bat" : $"tazel-{name}");
        var text = windows
            ? $"@echo off\r\n{name} %*\r\nexit /b %errorlevel%\r\n"
            : $"#!/bin/sh\nexec {name} \"$@\"\n";
        try
        {
            // CreateNew: an existing file, or one made in the meantime, is never overwritten.
            using var stream = new FileStream(file, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            stream.Write(new UTF8Encoding(false).GetBytes(text));
        }
        catch (IOException error) when (File.Exists(file) || Directory.Exists(file))
        {
            throw new ToolException($"{file} is already there; Tazel does not overwrite it", error);
        }

        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(file, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        }

        return file;
    }
}

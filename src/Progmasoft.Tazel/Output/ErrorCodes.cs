// SPDX-FileCopyrightText: 2026 Progmasoft <support@progmasoft.com>
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace Progmasoft.Tazel.Output;

/// <summary>What one error code means, for <c>tazel explain</c>.</summary>
/// <param name="Code">The code, <c>E</c> and four digits.</param>
/// <param name="Title">What went wrong, in a few words.</param>
/// <param name="Explanation">Why it is a problem and how to fix it.</param>
/// <param name="Wrong">Text that has the problem, or null.</param>
/// <param name="Right">The same text fixed, or null.</param>
/// <param name="Where">The file or part of Tazel the code belongs to.</param>
internal sealed record ErrorCode(string Code, string Title, string Explanation, string? Wrong, string? Right, string Where);

/// <summary>
/// Every error code of Tazel. The first two digits say where the error comes from: 00 the lexer of the text files,
/// 01 the command line, 02 <c>tazel_options.txt</c>, 03 <c>tazel_custom.txt</c>, 04 <c>tazel_envs.txt</c>, 05 project
/// files and builds, 06 labels. A code, once given, keeps its meaning.
/// </summary>
internal static partial class ErrorCodes
{
    public const string CharacterHasNoMeaning = "E0001";
    public const string CommentNotClosed = "E0002";
    public const string StringNotClosed = "E0003";
    public const string PlaceholderNotClosed = "E0004";
    public const string PlaceholderOutOfRange = "E0005";
    public const string DirectiveUnknown = "E0006";
    public const string ConditionalUnbalanced = "E0007";
    public const string ConditionInvalid = "E0008";
    public const string IncludePathRefused = "E0009";
    public const string IncludeMissing = "E0010";
    public const string IncludeTooDeep = "E0011";
    public const string PlatformDependsOnTarget = "E0012";

    public const string CommandLine = "E0100";

    public const string OptionsUnknown = "E0201";
    public const string OptionsTwice = "E0202";
    public const string OptionsNoValue = "E0203";
    public const string OptionsNoSemicolon = "E0204";
    public const string OptionsNoColon = "E0205";
    public const string OptionsUnexpected = "E0206";
    public const string OptionsNotBoolean = "E0207";
    public const string OptionsStringNotAuto = "E0208";
    public const string OptionsNotANumber = "E0209";
    public const string OptionsOutOfRange = "E0210";
    public const string OptionsNotAPlatform = "E0211";
    public const string OptionsUnreadable = "E0212";
    public const string OptionsDotnetSetting = "E0213";

    public const string CustomNameEnd = "E0301";
    public const string CustomNameTooLong = "E0302";
    public const string CustomTwice = "E0303";
    public const string CustomTooManyArguments = "E0304";
    public const string CustomNoSemicolon = "E0305";
    public const string CustomNoColon = "E0306";
    public const string CustomNoOpen = "E0307";
    public const string CustomNoClose = "E0308";
    public const string CustomUnexpected = "E0309";
    public const string CustomShellArguments = "E0310";
    public const string CustomShellOneLine = "E0311";
    public const string CustomEmpty = "E0312";
    public const string CustomNoProgram = "E0313";

    public const string EnvsTwice = "E0401";
    public const string EnvsShellOneLine = "E0402";
    public const string EnvsEmptyProgram = "E0403";
    public const string EnvsNotAValue = "E0404";
    public const string EnvsNoValue = "E0405";
    public const string EnvsNoSemicolon = "E0406";
    public const string EnvsNoColon = "E0407";
    public const string EnvsNoClose = "E0408";
    public const string EnvsNoOpen = "E0409";
    public const string EnvsUnexpected = "E0410";
    public const string EnvsPlaceholder = "E0411";

    public const string Project = "E0500";
    public const string ActionsFailed = "E0501";
    public const string NoDaemon = "E0502";
    public const string Stopped = "E0503";
    public const string MicroVM = "E0504";
    public const string CacheKey = "E0505";
    public const string DotnetSettingRefused = "E0506";
    public const string Tool = "E0507";
    public const string Label = "E0600";

    private const string Lexer = "the text files: tazel_custom.txt, tazel_options.txt and tazel_envs.txt";
    private const string Options = "tazel_options.txt";
    private const string Custom = "tazel_custom.txt";
    private const string Envs = "tazel_envs.txt";

    private static readonly ErrorCode[] All =
    [
        new(CharacterHasNoMeaning, "a character that starts nothing",
            "The text files know names (letters, digits, - _ .), strings in ' \" or `, the punctuation ( ) : ; and the " +
            "placeholders <1> to <9> and <*>, besides comments. Any other character outside a string is an error. Put text " +
            "with other characters in quotes.",
            "message: hello @ world;", "message: 'hello @ world';", Lexer),
        new(CommentNotClosed, "a <# comment #> without its #>",
            "A comment that starts with <# goes on until #>, over as many lines as it likes. When the file ends first, the " +
            "comment swallowed everything after it. Close it with #>, or use # for a comment to the end of the line.",
            "<# the options of this machine\nplatform: linux-amd64;", "<# the options of this machine #>\nplatform: linux-amd64;", Lexer),
        new(StringNotClosed, "a string without its closing quote",
            "A string ends at the next quote of its own kind: ' ends at ', \" at \" and ` at `. There are no escapes, so a " +
            "string that needs one kind of quote inside is written with another kind around it.",
            "example-version: '0.1.0;", "example-version: '0.1.0';", Lexer),
        new(PlaceholderNotClosed, "a < that is not a placeholder",
            "Outside strings, < starts a placeholder of tazel_custom.txt: <1> for the first argument, up to <9>, or <*> for all " +
            "of them. A < with no > after it, or with spaces inside, is none of them.",
            "say: (\"echo\" <1);", "say: (\"echo\" <1>);", Lexer),
        new(PlaceholderOutOfRange, "a placeholder with no argument behind it",
            "A placeholder counts from 1, like a person: <1> to <9>, with no zero in front. <*> stands for every argument.",
            "say: (\"echo\" <0>);", "say: (\"echo\" <1>);", Lexer),

        new(DirectiveUnknown, "a directive Tazel does not know, or one written wrongly",
            "A line that starts with a dot and a letter is a directive: .include, .import, .pragma once, .if, .elif, .else " +
            "or .end. Directives take no ';'. .pragma once stands first in its file, before anything but comments, and once " +
            "is the only pragma. A name that starts with a dot at the start of a line is read as a directive, so it goes in " +
            "quotes.",
            ".pragma once;", ".pragma once", Lexer),
        new(ConditionalUnbalanced, "an .if without its .end, or a branch out of place",
            "An .if opens a block that .end closes; between them come any .elif and at most one .else, last. An .elif, .else " +
            "or .end with no .if open, a second .else, an .elif after .else, an .if left open at the end of the file, and " +
            "blocks nested deeper than 64 are errors. .else and .end take nothing after them.",
            ".if host_system.os == \"linux\"\nmicrovm-cpus: 4;", ".if host_system.os == \"linux\"\nmicrovm-cpus: 4;\n.end", Lexer),
        new(ConditionInvalid, "a condition Tazel cannot read",
            "A condition of .if and .elif is made of true and false, strings in quotes, host_system.os, .arch, .abi and " +
            ".platform, the same four of target_system, defined(\"NAME\") and env(\"NAME\") for the environment, ==, !=, !, && and " +
            "|| with the usual precedence, and parentheses. == and != compare two strings or two booleans; !, && and || take " +
            "booleans, and the whole condition is one. Any other name or operator is an error; a condition runs nothing.",
            ".if host_system.os = \"linux\"", ".if host_system.os == \"linux\"", Lexer),
        new(IncludePathRefused, "an include or import path Tazel does not take",
            "A path in quotes is a file relative to the directory of the file that names it; one in angle brackets, " +
            "<file>, is below the Include directory of TAZEL_HOME for the kind of file (Options, Custom or Envs) and may not " +
            "leave it, not through .. or a link either. Paths are taken as written: no wildcards, no URLs, no environment " +
            "variables, and a quoted path is not absolute.",
            ".include \"https://example.com/build.options.txt\"", ".include \"config/build.options.txt\"", Lexer),
        new(IncludeMissing, "a file to include that is not there or cannot be read",
            "The file a .include or .import names must exist and be readable. Nothing is included from a branch of .if that " +
            "is not taken, so only a file the configuration reaches has to exist.", null, null, Lexer),
        new(IncludeTooDeep, "includes that go round, nest too deep or grow too large",
            "A file that includes itself, directly or through others, is an error unless it says .pragma once or is " +
            "imported: then it is read once. Includes may nest 64 deep, and one configuration reads at most 4,096 files and " +
            "1,000,000 tokens. The message shows the chain of includes.", null, null, Lexer),
        new(PlatformDependsOnTarget, "a platform chosen by a condition on target_system",
            "Tazel chooses the platform first, from the command line and the platform entries that do not depend on " +
            "target_system, and then reads everything else for that platform. A platform set inside an .if or .elif on " +
            "target_system, or in a file such a branch includes, would choose the platform from itself, so it is an error.",
            ".if target_system.os == \"linux\"\nplatform: windows-x64;\n.end", ".if host_system.os == \"linux\"\nplatform: windows-x64;\n.end",
            Options),
        new(OptionsDotnetSetting, "a .NET setting Tazel does not take",
            "dotnet-settings: [ ... ]; holds strings of Name=Value, one per setting, without commas between them. The names " +
            "are the thirteen of tazel help dotnet --long; sizes take B, K, M, G or T, percentages and thread counts whole " +
            "numbers. A name given twice keeps its last value.",
            "dotnet-settings: GCHeapHardLimit=512M;", "dotnet-settings: [\"GCHeapHardLimit=512M\"];", Options),
        new(DotnetSettingRefused, "a .NET setting that cannot hold, or that .NET refused",
            "Some settings are two ways of saying one thing, and Tazel takes neither of them: GCHeapHardLimit with " +
            "GCHeapHardLimitPercent, a limit for the whole heap with limits per object heap, or object heap limits in bytes " +
            "and in percent. Limits per object heap need both SOH and LOH; their percentages are each below 100, and so is " +
            "their sum. .NET itself may refuse a heap limit below what this process has committed, a latency mode its GC " +
            "cannot take, or thread counts it does not allow. Nothing of the command ran.", null, null, "tazel"),
        new(Tool, "a tool Tazel cannot find, start or give its arguments",
            "tazel tool launch <name> runs TAZEL_TOOL_EXECUTABLE when it is set, and nothing else then: it must be a " +
            "program this user may run, its path absolute or from the working directory. Otherwise it runs the first " +
            "tazel-<name> of PATH this user may run, skipping empty entries, '.' and the working directory, and searching " +
            "nowhere else. A name is letters, digits, '.', '_' and '-'. On Windows a batch file runs through cmd.exe, which " +
            "cannot be given an argument with \", % or a line break as written, so such an argument is refused. " +
            "tazel tool create-wrapper writes tazel-<name> into a directory that exists and never overwrites a file.",
            "TAZEL_TOOL_EXECUTABLE= tazel tool launch fmt", "TAZEL_TOOL_EXECUTABLE=./bin/fmt tazel tool launch fmt", "tazel"),
        new(CommandLine, "a command line Tazel cannot take",
            "The command, an option or a value is not one Tazel knows, or a command got arguments it does not take. Tazel " +
            "suggests the nearest spelling when there is one. tazel help lists the commands, and tazel help <command> --long " +
            "says what each takes.",
            "tazel biuld //...", "tazel build //...", "the command line"),

        new(OptionsUnknown, "an option tazel_options.txt does not have",
            "tazel_options.txt sets platform, sandboxing-with-microvm, microvm-network, microvm-memory and microvm-cpus. Any " +
            "other name is a misspelling or an option of another tool.",
            "plaform: linux-amd64;", "platform: linux-amd64;", Options),
        new(OptionsTwice, "an option set twice",
            "Each option is set once in a file, so that nobody has to guess which one wins. Keep one of them.",
            "platform: linux-amd64;\nplatform: linux-aarch64;", "platform: linux-aarch64;", Options),
        new(OptionsNoValue, "an option without its value",
            "An option is a name, ':', a value and ';'. The value is missing.",
            "microvm-cpus: ;", "microvm-cpus: 2;", Options),
        new(OptionsNoSemicolon, "an option without its ';'",
            "Every option ends with ';'; only the last one of the file may leave it out.",
            "platform: linux-amd64\nmicrovm-cpus: 2;", "platform: linux-amd64;\nmicrovm-cpus: 2;", Options),
        new(OptionsNoColon, "an option without its ':'",
            "The name of an option and its value are joined by ':'.",
            "platform linux-amd64;", "platform: linux-amd64;", Options),
        new(OptionsUnexpected, "something tazel_options.txt does not expect here",
            "The file is a list of name: value; and nothing else.",
            "platform: (linux-amd64);", "platform: linux-amd64;", Options),
        new(OptionsNotBoolean, "a yes-or-no option without true or false",
            "sandboxing-with-microvm and microvm-network take true or false, bare, without quotes.",
            "microvm-network: yes;", "microvm-network: true;", Options),
        new(OptionsStringNotAuto, "a string where only \"auto\" is a string",
            "microvm-memory and microvm-cpus take a number, bare, or the string \"auto\", which lets Tazel decide.",
            "microvm-memory: \"2048\";", "microvm-memory: 2048;", Options),
        new(OptionsNotANumber, "something that is not a number",
            "A number is digits only, without a zero in front and without a unit: microvm-memory counts MiB, microvm-cpus " +
            "counts processors.",
            "microvm-memory: 2GiB;", "microvm-memory: 2048;", Options),
        new(OptionsOutOfRange, "a number outside what the option takes",
            "microvm-memory takes 256 MiB to what this machine has, microvm-cpus one processor to what this machine has.",
            "microvm-cpus: 4096;", "microvm-cpus: 4;", Options),
        new(OptionsNotAPlatform, "a platform Tazel does not know",
            "platform names one of the platforms of --platform: linux-amd64, linux-aarch64, macos-x86_64, macos-aarch64, " +
            "windows-amd64, windows-aarch64 and the others tazel help build --long lists.",
            "platform: linux-amd46;", "platform: linux-amd64;", Options),
        new(OptionsUnreadable, "a tazel_options.txt that cannot be read",
            "Tazel found a tazel_options.txt but could not read it, or could not tell whether it is there. Tazel stops " +
            "rather than skip it, as the file may turn the sandbox on. Check its permissions.", null, null, Options),

        new(CustomNameEnd, "'--' inside the name of a command",
            "On the command line, '--' ends the name of a custom command and starts its arguments, so a name cannot hold it.",
            "run -- all: (\"make\");", "run all: (\"make\");", Custom),
        new(CustomNameTooLong, "a name of more than nine words",
            "A name holds at most nine words, the most arguments tazel custom can tell apart.", null, null, Custom),
        new(CustomTwice, "a command defined twice",
            "Each name is defined once; rename one, or put the steps of both in one command.",
            "build: (\"make\");\nbuild: (\"ninja\");", "build: (\"make\") (\"ninja\");", Custom),
        new(CustomTooManyArguments, "a command whose arguments do not fit",
            "The words of a name and the arguments it needs share nine places on the command line.", null, null, Custom),
        new(CustomNoSemicolon, "a command without its ';'",
            "Every command ends with ';'; only the last one of the file may leave it out.",
            "build: (\"tazel\" \"build\" \"//...\")\ntest: (\"tazel\" \"test\" \"//...\");",
            "build: (\"tazel\" \"build\" \"//...\");\ntest: (\"tazel\" \"test\" \"//...\");", Custom),
        new(CustomNoColon, "a command without its ':'",
            "The name of a command and its steps are joined by ':'.",
            "build (\"tazel\" \"build\");", "build: (\"tazel\" \"build\");", Custom),
        new(CustomNoOpen, "a step without its '('",
            "A step is (\"program\" \"argument\"...) or s(\"a shell line\").",
            "build: \"tazel\" \"build\";", "build: (\"tazel\" \"build\");", Custom),
        new(CustomNoClose, "a step without its ')'",
            "A step closes with ')' after its strings and placeholders.",
            "build: (\"tazel\" \"build\";", "build: (\"tazel\" \"build\");", Custom),
        new(CustomUnexpected, "something tazel_custom.txt does not expect here",
            "The file is a list of name: steps; where a step is (\"program\" ...) or s(\"line\").",
            "build: tazel build;", "build: (\"tazel\" \"build\");", Custom),
        new(CustomShellArguments, "a placeholder inside s(...)",
            "s(...) hands one line to the shell as written; it takes no arguments. Use (\"program\" <1>) to pass them.",
            "say: s(\"echo\" <1>);", "say: (\"echo\" <1>);", Custom),
        new(CustomShellOneLine, "s(...) with more or less than one string",
            "s(...) takes the whole shell line as one string.",
            "dev: s(\"npm\" \"run\" \"dev\");", "dev: s(\"npm run dev\");", Custom),
        new(CustomEmpty, "a step with nothing in it",
            "A step names at least the program it runs.",
            "dev: ();", "dev: (\"npm\" \"run\" \"dev\");", Custom),
        new(CustomNoProgram, "a step that starts with a placeholder",
            "A step starts with the name of its program, a string; the arguments may follow.",
            "run: (<1>);", "run: (\"tazel\" \"run\" <1>);", Custom),

        new(EnvsTwice, "a value set twice",
            "Each name of tazel_envs.txt is set once; GetEnvironment.From could not know which one you meant.",
            "example-version: '0.1.0';\nexample-version: '0.2.0';", "example-version: '0.2.0';", Envs),
        new(EnvsShellOneLine, "s(...) with more or less than one string",
            "s(...) takes the whole shell line as one string; /bin/sh or cmd.exe reads it.",
            "home: s(`echo` `$HOME`);", "home: s(`echo $HOME`);", Envs),
        new(EnvsEmptyProgram, "a program with no name",
            "(\"program\" \"argument\"...) starts a program directly and needs at least its name.",
            "example-output: ();", "example-output: (\"rustc\" \"--version\");", Envs),
        new(EnvsNotAValue, "something that is not a value",
            "A value is a string, a program (\"program\" \"argument\") or a shell line s(\"line\"). A bare word is a name, " +
            "not a value; put it in quotes.",
            "example-version: 0.1.0;", "example-version: '0.1.0';", Envs),
        new(EnvsNoValue, "a name without its value",
            "Every entry is name: value; and the value is missing.",
            "example-version:", "example-version: '0.1.0';", Envs),
        new(EnvsNoSemicolon, "a value without its ';'",
            "Every value of tazel_envs.txt ends with ';'; only the last one of the file may leave it out.",
            "example-version: '0.1.0'\nexample-name: 'demo';", "example-version: '0.1.0';\nexample-name: 'demo';", Envs),
        new(EnvsNoColon, "a name without its ':'",
            "A name and its value are joined by ':'.",
            "example-version '0.1.0';", "example-version: '0.1.0';", Envs),
        new(EnvsNoClose, "a program or shell line without its ')'",
            "(\"program\" ...) and s(\"line\") close with ')'.",
            "example-output: (\"rustc\" \"--version\";", "example-output: (\"rustc\" \"--version\");", Envs),
        new(EnvsNoOpen, "s without its '('",
            "A shell line is s(\"line\"): s and '(' with nothing between.",
            "home: s `echo $HOME`;", "home: s(`echo $HOME`);", Envs),
        new(EnvsUnexpected, "something tazel_envs.txt does not expect here",
            "The file is a list of name: value; and nothing else.", null, null, Envs),
        new(EnvsPlaceholder, "a placeholder in tazel_envs.txt",
            "<1> and <*> stand for the arguments of a custom command; nothing passes arguments to tazel_envs.txt. Write the " +
            "argument itself.",
            "example-output: (\"rustc\" <1>);", "example-output: (\"rustc\" \"--version\");", Envs),

        new(Project, "a problem in the project files or the build",
            "A MODULE.csx or BUILD.csx did not compile or run, declared something Tazel refuses, or the build met " +
            "something it cannot go on with, such as a dependency that is not there. The message says which file and what; " +
            "a C# error of a project file names its line and column.", null, null, "MODULE.csx, BUILD.csx and builds"),
        new(ActionsFailed, "actions of the build failed",
            "A compiler, a linker, a test or a step ended with an error. Its command and its output are printed above, " +
            "marked FAILED; the problem is in what it says. With --keep-going, Tazel goes on with what does not depend on " +
            "the failed actions, so one build shows every failure.", null, null, "MODULE.csx, BUILD.csx and builds"),
        new(NoDaemon, "no daemon to start or stop",
            "This Tazel runs every command in a process of its own, so tazel dotnet start-daemon, restart-daemon and " +
            "stop-daemon have nothing to do.", null, null, "MODULE.csx, BUILD.csx and builds"),
        new(Stopped, "the command was stopped",
            "Ctrl+C or a signal stopped Tazel. The actions that were running were stopped too; what finished is kept, and " +
            "the next build goes on from there.", null, null, "MODULE.csx, BUILD.csx and builds"),
        new(MicroVM, "the MicroVM could not run the command",
            "tazel_options.txt asks for sandboxing-with-microvm, so tazel custom runs inside a MicroVM: a Linux guest " +
            "under Cloud Hypervisor that sees the module and the host's /usr, read-only, and nothing else of the host. It " +
            "needs x86_64 Linux with KVM (/dev/kvm), virtiofsd, and the image of the guest: Tazel fetches the image of its " +
            "release and Cloud Hypervisor once and checks them, and TAZEL_MICROVM_IMAGE names an image built with " +
            "dotnet run microvm/build.cs. The message says which of these failed. The MicroVM has no network, so " +
            "microvm-network: true is refused, and builds, tests and plugins do not run in it, so a sandbox stops them.",
            "microvm-network: true;", "microvm-network: false;", "MODULE.csx, BUILD.csx and builds"),
        new(CacheKey, "a key of the remote cache Tazel cannot read",
            "The PublicKey and the SigningKey of Remote.From in MODULE.csx name their algorithm: ed25519:, rsa-4096: or " +
            "ml-dsa-65:, then the key in base64. A public key is 32 bytes for Ed25519, the SubjectPublicKeyInfo of a " +
            "4096-bit key for RSA and 1952 bytes for ML-DSA-65; a signing key is a 32-byte seed for Ed25519 and " +
            "ML-DSA-65 and a PKCS #8 key for RSA. tazel cache keygen makes a pair.",
            "PublicKey = \"AAECAwQF...\"", "PublicKey = \"ed25519:AAECAwQF...\"", "MODULE.csx"),
        new(Label, "a label Tazel cannot read",
            "A label names a target: //dir:name, //:name, :name in the current package, @dependency//dir:name; //dir/... " +
            "and //... are patterns of packages. Names hold letters, digits and _ + . -.",
            "tazel build //app/greeter", "tazel build //app:greeter", "labels"),
    ];

    private static readonly Dictionary<string, ErrorCode> ByCode = All.ToDictionary(code => code.Code, StringComparer.Ordinal);

    /// <summary>Every code, in order.</summary>
    public static IReadOnlyList<ErrorCode> Codes => All;

    /// <summary>Whether <paramref name="text"/> is written as a code: E and four digits.</summary>
    public static bool IsCode(string text) => CodePattern().IsMatch(text);

    /// <summary>What <paramref name="code"/> means, or null when Tazel has no such code.</summary>
    public static ErrorCode? Find(string code) => ByCode.GetValueOrDefault(code);

    [GeneratedRegex("^E[0-9]{4}$")]
    private static partial Regex CodePattern();
}

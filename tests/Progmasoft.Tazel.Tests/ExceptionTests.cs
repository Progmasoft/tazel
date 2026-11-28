// SPDX-FileCopyrightText: 2026 Progmasoft <support@progmasoft.com>
// SPDX-License-Identifier: MIT

extern alias guest;

using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Progmasoft.Tazel.Cli;
using Progmasoft.Tazel.Dsl;
using Progmasoft.Tazel.Dsl.Custom;
using Progmasoft.Tazel.Dsl.Options;
using Progmasoft.Tazel.Label;
using Progmasoft.Tazel.MicroVM;
using Progmasoft.Tazel.PluginApi;
using Progmasoft.Tazel.Project;
using Xunit;
using GuestProtocol = guest::Progmasoft.Tazel.MicroVM.MicroVMProtocol;
using GuestKind = guest::Progmasoft.Tazel.MicroVM.MessageKind;
using GuestRun = guest::Progmasoft.Tazel.MicroVM.RunRequest;

namespace Progmasoft.Tazel.Tests;

/// <summary>The standard constructors of Tazel's exceptions, and the agent's own copy of the protocol.</summary>
public sealed class ExceptionTests
{
    public static TheoryData<string> Kinds => ["Dsl", "CommandLine", "Project", "Plugin", "Label", "TazelOptions", "MicroVM", "CustomCommands"];

    private static Exception Make(string kind, params object[] arguments) => kind switch
    {
        "Dsl" => (Exception)Activator.CreateInstance(typeof(DslException), arguments)!,
        "CommandLine" => (Exception)Activator.CreateInstance(typeof(CommandLineException), arguments)!,
        "Project" => (Exception)Activator.CreateInstance(typeof(ProjectException), arguments)!,
        "Plugin" => (Exception)Activator.CreateInstance(typeof(PluginException), arguments)!,
        "Label" => (Exception)Activator.CreateInstance(typeof(LabelException), arguments)!,
        "TazelOptions" => (Exception)Activator.CreateInstance(typeof(TazelOptionsException), arguments)!,
        "MicroVM" => (Exception)Activator.CreateInstance(typeof(MicroVMException), arguments)!,
        _ => (Exception)Activator.CreateInstance(typeof(CustomCommandsException), arguments)!,
    };

    [Theory]
    [MemberData(nameof(Kinds))]
    public void EachExceptionHasTheStandardConstructors(string kind)
    {
        Assert.NotNull(Make(kind).Message);
        Assert.Equal("why", Make(kind, "why").Message);
        var inner = new InvalidOperationException("under");
        var wrapped = Make(kind, "why", inner);
        Assert.Equal("why", wrapped.Message);
        Assert.Same(inner, wrapped.InnerException);
    }

    [Fact]
    public async Task TheAgentsCopyOfTheProtocolReadsWhatItWrites()
    {
        var request = new GuestRun(["sh", "-c", "true"], new Dictionary<string, string>(StringComparer.Ordinal) { ["A"] = "b" }, "/work");
        using var stream = new MemoryStream();
        await GuestProtocol.WriteAsync(stream, GuestKind.Run, GuestProtocol.Encode(request), TestContext.Current.CancellationToken);
        await GuestProtocol.WriteAsync(stream, GuestKind.Exit, GuestProtocol.EncodeStatus(-3), TestContext.Current.CancellationToken);
        stream.Position = 0;

        var run = await GuestProtocol.ReadAsync(stream, TestContext.Current.CancellationToken);
        Assert.Equal(request.Arguments, GuestProtocol.DecodeRun(run!.Value.Payload).Arguments);
        var exit = await GuestProtocol.ReadAsync(stream, TestContext.Current.CancellationToken);
        Assert.Equal(-3, GuestProtocol.DecodeStatus(exit!.Value.Payload));
        Assert.Null(await GuestProtocol.ReadAsync(stream, TestContext.Current.CancellationToken));
        Assert.Equal("/a b/ü", GuestProtocol.DecodePath(GuestProtocol.EncodePath("/a b/ü")));
    }
}

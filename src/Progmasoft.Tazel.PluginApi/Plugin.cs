// SPDX-FileCopyrightText: 2026 Progmasoft <support@progmasoft.com>
// SPDX-License-Identifier: MIT

using System;
using Progmasoft.Tazel.Dsl.Common;

namespace Progmasoft.Tazel.PluginApi;

/// <summary>
/// A Tazel plugin: the one public class of its entry assembly that implements this interface and has a public
/// constructor without parameters. Tazel creates it and calls <see cref="Apply"/> before the BUILD.csx files of the
/// module run; the public types of the plugin's assemblies are then there for those files to use.
/// </summary>
public interface ITazelPlugin
{
    /// <summary>Prepares the plugin for one build of a module.</summary>
    void Apply(IPluginContext context);
}

/// <summary>What a plugin learns about the build, and what it can ask of Tazel when it is applied.</summary>
public interface IPluginContext
{
    /// <summary>The group of the plugin, as in <c>com.example.plugins</c>.</summary>
    string Group { get; }

    /// <summary>The name of the plugin.</summary>
    string Name { get; }

    /// <summary>The version of the plugin.</summary>
    string Version { get; }

    /// <summary>The platform the build is for.</summary>
    Platform Platform { get; }

    /// <summary>Whether the build is a debug or a release build.</summary>
    BuildMode Mode { get; }

    /// <summary>The root of the module that uses the plugin.</summary>
    string ModuleRoot { get; }

    /// <summary>
    /// Lets every BUILD.csx of the module use the types of <paramref name="namespace"/> without a <c>using</c>.
    /// </summary>
    void Import(string @namespace);

    /// <summary>Says something to the user, as a status line of the build.</summary>
    void Status(string message);
}

/// <summary>The group, name and version of a plugin; Progmasoft.Tazel.PluginSdk writes it into the assembly.</summary>
/// <param name="group">The group, as in <c>com.example.plugins</c>.</param>
/// <param name="name">The name.</param>
/// <param name="version">The SemVer version.</param>
[AttributeUsage(AttributeTargets.Assembly, AllowMultiple = false)]
public sealed class TazelPluginAttribute(string group, string name, string version) : Attribute
{
    /// <summary>The group.</summary>
    public string Group { get; } = group;

    /// <summary>The name.</summary>
    public string Name { get; } = name;

    /// <summary>The version.</summary>
    public string Version { get; } = version;
}

/// <summary>What a plugin or one of its steps reports as a failure, with a message for the user.</summary>
public sealed class PluginException : Exception
{
    /// <summary>A failure and what to say about it.</summary>
    public PluginException(string message)
        : base(message)
    {
    }

    /// <summary>Nothing to say.</summary>
    public PluginException()
    {
    }

    /// <summary>A failure and its cause.</summary>
    public PluginException(string message, Exception inner)
        : base(message, inner)
    {
    }
}

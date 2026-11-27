// SPDX-FileCopyrightText: 2026 Progmasoft <support@progmasoft.com>
// SPDX-License-Identifier: MIT

using System;
using System.IO;

namespace Progmasoft.Tazel.Dsl.Configuration;

/// <summary>A <c>TAZEL_HOME</c> Tazel cannot use.</summary>
public sealed class ConfigurationHomeException : Exception
{
    /// <summary>What is wrong with it.</summary>
    public ConfigurationHomeException(string message)
        : base(message)
    {
    }

    /// <summary>Nothing to say.</summary>
    public ConfigurationHomeException()
    {
    }

    /// <summary>What is wrong, and why.</summary>
    public ConfigurationHomeException(string message, Exception inner)
        : base(message, inner)
    {
    }
}

/// <summary>
/// The directory of Tazel's own files for this user: <c>TAZEL_HOME</c> when it is set, else <c>$HOME/.Tazel</c> on
/// Linux, macOS and the BSDs and <c>%APPDATA%\Tazel</c> on Windows, whatever XDG says. A <c>TAZEL_HOME</c> that is set
/// but empty or relative is an error, never a reason to use another directory. Nothing here creates it.
/// </summary>
public static class TazelHome
{
    /// <summary>The name of the variable that sets it.</summary>
    public const string Variable = "TAZEL_HOME";

    /// <summary>The directory, resolved from the environment as it is now.</summary>
    public static string Directory()
    {
        if (System.Environment.GetEnvironmentVariable(Variable) is { } explicitHome)
        {
            if (explicitHome.Length == 0 || !Path.IsPathFullyQualified(explicitHome))
            {
                throw new ConfigurationHomeException(
                    $"{Variable} is '{explicitHome}', which is not an absolute path; set it to one, or unset it for the default");
            }

            return Path.GetFullPath(explicitHome);
        }

        if (OperatingSystem.IsWindows())
        {
            var appData = System.Environment.GetFolderPath(System.Environment.SpecialFolder.ApplicationData);
            return appData.Length > 0 ? Path.Combine(appData, "Tazel")
                : throw new ConfigurationHomeException("%APPDATA% is not set; set TAZEL_HOME");
        }

        var home = System.Environment.GetEnvironmentVariable("HOME");
        return home is { Length: > 0 } && Path.IsPathFullyQualified(home) ? Path.Combine(home, ".Tazel")
            : throw new ConfigurationHomeException("HOME is not an absolute path; set TAZEL_HOME");
    }
}

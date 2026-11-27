// SPDX-FileCopyrightText: 2026 Progmasoft <support@progmasoft.com>
// SPDX-License-Identifier: MIT

// Replaces the SPDX License List that Tazel checks the License of a module against: licenses.json and exceptions.json
// of a release of https://github.com/spdx/license-list-data, as published, next to this file. The version is a tag of
// that repository without its v, such as 3.29.0; take one that is at least two weeks old.
//
//   dotnet run spdx/update.cs -- 3.29.0
//
// Afterwards update the version that the tests expect (SpdxLicensesTests, PackagingTests), THIRD-PARTY-NOTICES.md and
// docs/packaging.md.

using System;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;

if (args.Length != 1 || !Regex.IsMatch(args[0], @"^\d+\.\d+(\.\d+)?$"))
{
    Console.Error.WriteLine("usage: dotnet run spdx/update.cs -- <version, such as 3.29.0>");
    return 2;
}

var version = args[0];
var directory = Path.GetDirectoryName(Path.GetFullPath(ThisFile()))!;
using var http = new HttpClient();
foreach (var name in new[] { "licenses.json", "exceptions.json" })
{
    var url = $"https://raw.githubusercontent.com/spdx/license-list-data/v{version}/json/{name}";
    string text;
    try
    {
        text = await http.GetStringAsync(url);
    }
    catch (HttpRequestException error)
    {
        Console.Error.WriteLine($"{url}: {error.Message} (is v{version} a release of spdx/license-list-data?)");
        return 1;
    }

    using (var document = JsonDocument.Parse(text))
    {
        var listed = document.RootElement.GetProperty("licenseListVersion").GetString();
        if (listed != version)
        {
            Console.Error.WriteLine($"{url} says it is version {listed}, not {version}");
            return 1;
        }
    }

    await File.WriteAllTextAsync(Path.Combine(directory, name), text);
    Console.WriteLine($"{name}: SPDX License List {version}");
}

return 0;

static string ThisFile([System.Runtime.CompilerServices.CallerFilePath] string path = "") => path;

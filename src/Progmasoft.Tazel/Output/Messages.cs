// SPDX-FileCopyrightText: 2026 Progmasoft <support@progmasoft.com>
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Xml;

namespace Progmasoft.Tazel.Output;

/// <summary>How Tazel prints its messages: <c>--message-format</c>.</summary>
internal enum MessageFormat
{
    /// <summary>The rich terminal output of Spectre.Console: colours, boxes, pictures of problems.</summary>
    RichStdio,

    /// <summary>Plain lines for scripts and logs, each message starting with a fixed word.</summary>
    ScriptFriendlyStdio,

    /// <summary>One JSON object per message per line.</summary>
    Jsonl,

    /// <summary>The messages as elements of one <c>&lt;tazel&gt;</c> document.</summary>
    Xml,
}

/// <summary>A field of a message: a string, a number, a list of strings or a list of rows of strings.</summary>
internal sealed record Field(string Name, object? Value);

/// <summary>A message of a kind (<c>status</c>, <c>error</c>, ...) with its fields in order.</summary>
internal sealed record Message(string Kind, IReadOnlyList<Field> Fields)
{
    public Message(string kind, params Field[] fields)
        : this(kind, (IReadOnlyList<Field>)fields)
    {
    }

    /// <summary>The value of <paramref name="name"/>, or null.</summary>
    public object? this[string name] => Fields.FirstOrDefault(field => field.Name == name)?.Value;

    /// <summary>The text of <paramref name="name"/>, or null.</summary>
    public string? Text(string name) => this[name] as string;
}

/// <summary>Writes messages in one of the formats that are not Spectre.Console's.</summary>
internal abstract class MessageWriter(TextWriter output) : IDisposable
{
    protected TextWriter Output { get; } = output;

    /// <summary>The writer of <paramref name="format"/>; null for <see cref="MessageFormat.RichStdio"/>.</summary>
    public static MessageWriter? For(MessageFormat format, TextWriter output) => format switch
    {
        MessageFormat.ScriptFriendlyStdio => new ScriptFriendlyWriter(output),
        MessageFormat.Jsonl => new JsonlWriter(output),
        MessageFormat.Xml => new XmlMessageWriter(output),
        _ => null,
    };

    public abstract void Write(Message message);

    /// <summary>Ends what is written: closes the document of the XML format.</summary>
    public virtual void Dispose() => Output.Flush();
}

/// <summary>
/// Plain lines: a fixed word first (<c>INFO</c>, <c>DONE</c>, <c>FAILED</c>, <c>error[E0406]:</c>, ...), more lines of
/// a message indented by two spaces, no colours, boxes, emoji or light lines, never wrapped. The output of a command
/// (<c>output</c>) is printed as it is.
/// </summary>
internal sealed class ScriptFriendlyWriter(TextWriter output) : MessageWriter(output)
{
    public override void Write(Message message)
    {
        switch (message.Kind)
        {
            case "output":
                Output.WriteLine(message.Text("text"));
                break;
            case "status":
                Line("INFO", message.Text("text"));
                break;
            case "action":
                Line("DONE", message.Text("description"));
                Indented(message.Text("output"));
                break;
            case "action-failed":
                Line("FAILED", message.Text("description"));
                Line("  command:", Reporter.Quote((IReadOnlyList<string>)message["command"]!));
                Indented(message.Text("output"));
                break;
            case "error":
                Line($"error[{message.Text("code")}]:", message.Text("message"));
                Help(message);
                break;
            case "diagnostic":
                Line($"error[{message.Text("code")}]:",
                    $"{message.Text("file")}:{message["line"]}:{message["column"]}: {message.Text("message")}");
                Line("  here:", message.Text("label"));
                Help(message);
                break;
            case "warning":
                Line("warning:", message.Text("message"));
                break;
            case "success":
                Line("SUCCESS", message.Text("message"));
                break;
            case "test":
                Line(message.Text("status")!.ToUpperInvariant(), $"{message.Text("label")} {message["seconds"]:0.0} s");
                break;
            case "test-summary":
                Line("TESTS", $"{message["passed"]} passed, {message["failed"]} failed, {message["total"]} in all");
                break;
            case "test-log":
                Line("LOG", message.Text("label"));
                Indented(message.Text("log"));
                break;
            case "table":
                Output.WriteLine(string.Join('\t', (IReadOnlyList<string>)message["columns"]!));
                foreach (var row in (IReadOnlyList<IReadOnlyList<string>>)message["rows"]!)
                {
                    Output.WriteLine(string.Join('\t', row));
                }

                break;
            case "explanation":
                Line(message.Text("code"), $"{message.Text("title")} ({message.Text("where")})");
                Indented(message.Text("text"));
                Indented(message.Text("wrong") is { } wrong ? "wrong:\n" + wrong : null);
                Indented(message.Text("right") is { } right ? "right:\n" + right : null);
                break;
            default:
                throw new ArgumentException($"no message kind {message.Kind}", nameof(message));
        }
    }

    private void Help(Message message)
    {
        if (message.Text("help") is { } help)
        {
            Line("  help:", help);
        }
    }

    // The word, then the text, whose further lines are indented.
    private void Line(string? word, string? text)
    {
        var lines = (text ?? "").TrimEnd('\n').Split('\n');
        Output.WriteLine(lines[0].Length == 0 ? word : $"{word} {lines[0]}");
        foreach (var line in lines.Skip(1))
        {
            Output.WriteLine("  " + line);
        }
    }

    private void Indented(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        foreach (var line in text.TrimEnd('\n').Split('\n'))
        {
            Output.WriteLine("  " + line);
        }
    }
}

/// <summary>One JSON object per message per line: <c>{"kind":"error","code":"E0406",...}</c>; absent fields are left out.</summary>
internal sealed class JsonlWriter(TextWriter output) : MessageWriter(output)
{
    public override void Write(Message message)
    {
        using var buffer = new MemoryStream();
        // Text stays as it is (é, not \u00E9): the lines are read as JSON, never placed in HTML.
        using (var json = new Utf8JsonWriter(buffer, new JsonWriterOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
        {
            json.WriteStartObject();
            json.WriteString("kind", message.Kind);
            foreach (var field in message.Fields.Where(field => field.Value is not null))
            {
                json.WritePropertyName(field.Name);
                switch (field.Value)
                {
                    case string text:
                        json.WriteStringValue(text);
                        break;
                    case int number:
                        json.WriteNumberValue(number);
                        break;
                    case double number:
                        json.WriteNumberValue(Math.Round(number, 3));
                        break;
                    case bool truth:
                        json.WriteBooleanValue(truth);
                        break;
                    case IReadOnlyList<IReadOnlyList<string>> rows:
                        json.WriteStartArray();
                        foreach (var row in rows)
                        {
                            Strings(json, row);
                        }

                        json.WriteEndArray();
                        break;
                    case IReadOnlyList<string> strings:
                        Strings(json, strings);
                        break;
                    default:
                        throw new ArgumentException($"a field of type {field.Value!.GetType().Name}", nameof(message));
                }
            }

            json.WriteEndObject();
        }

        Output.WriteLine(Encoding.UTF8.GetString(buffer.ToArray()));
        Output.Flush();
    }

    private static void Strings(Utf8JsonWriter json, IEnumerable<string> strings)
    {
        json.WriteStartArray();
        foreach (var text in strings)
        {
            json.WriteStringValue(text);
        }

        json.WriteEndArray();
    }
}

/// <summary>
/// The messages as elements of one <c>&lt;tazel&gt;</c> document, written as they happen: an element per message named
/// by its kind, a child element per field; a list's items are <c>&lt;item&gt;</c>, a table's rows <c>&lt;row&gt;</c>
/// of <c>&lt;cell&gt;</c>. Closing the writer closes the document.
/// </summary>
internal sealed class XmlMessageWriter : MessageWriter
{
    private readonly XmlWriter _xml;

    public XmlMessageWriter(TextWriter output)
        : base(output)
    {
        _xml = XmlWriter.Create(output, new XmlWriterSettings
        {
            Indent = true,
            CloseOutput = false,
            // Control characters a tool printed are written as references rather than refused.
            CheckCharacters = false,
        });
        _xml.WriteStartDocument();
        _xml.WriteStartElement("tazel");
        _xml.Flush();
    }

    public override void Write(Message message)
    {
        _xml.WriteStartElement(message.Kind);
        foreach (var field in message.Fields.Where(field => field.Value is not null))
        {
            _xml.WriteStartElement(field.Name);
            switch (field.Value)
            {
                case IReadOnlyList<IReadOnlyList<string>> rows:
                    foreach (var row in rows)
                    {
                        _xml.WriteStartElement("row");
                        foreach (var cell in row)
                        {
                            _xml.WriteElementString("cell", Clean(cell));
                        }

                        _xml.WriteEndElement();
                    }

                    break;
                case IReadOnlyList<string> strings:
                    foreach (var text in strings)
                    {
                        _xml.WriteElementString("item", Clean(text));
                    }

                    break;
                case double number:
                    _xml.WriteString(Math.Round(number, 3).ToString(CultureInfo.InvariantCulture));
                    break;
                case IFormattable formattable:
                    _xml.WriteString(formattable.ToString(null, CultureInfo.InvariantCulture));
                    break;
                default:
                    _xml.WriteString(Clean(field.Value!.ToString()!));
                    break;
            }

            _xml.WriteEndElement();
        }

        _xml.WriteEndElement();
        _xml.Flush();
    }

    public override void Dispose()
    {
        _xml.WriteEndElement();
        _xml.WriteEndDocument();
        _xml.Dispose();
        base.Dispose();
    }

    // XML 1.0 cannot hold most control characters even as references: they become U+FFFD.
    private static string Clean(string text) =>
        string.Create(text.Length, text, (span, source) =>
        {
            for (var i = 0; i < source.Length; i++)
            {
                var c = source[i];
                span[i] = c < ' ' && c is not '\t' and not '\n' and not '\r' ? '�' : c;
            }
        });
}

/*
 * SPDX-FileCopyrightText: 2026 Progmasoft <support@progmasoft.com>
 * SPDX-License-Identifier: MPL-2.0 WITH AdditionRef-Progmasoft-Exception-2.0
 */

package com.progmasoft.tazel.cli

/** The file that holds the commands of `tazel custom`, in the directory they run in. */
internal const val CUSTOM_COMMANDS_FILE = "tazel_custom.txt"

/** One step of a custom command. */
internal sealed interface CustomStep {
  /** A program started directly, with these arguments and no shell in between. */
  data class Program(val arguments: List<String>) : CustomStep

  /** A whole command line for the shell of the host: CMD on Windows, `sh` elsewhere. */
  data class Shell(val line: String) : CustomStep
}

/** A named command of `tazel_custom.txt`: steps that run in order until one fails. */
internal data class CustomCommand(
  val name: String,
  val steps: List<CustomStep>,
)

/** What is wrong with a `tazel_custom.txt`, with where it is. */
internal class CustomCommandsException(message: String) : Exception(message)

private enum class TokenKind {
  NAME,
  STRING,
  COLON,
  SEMICOLON,
  OPEN,
  CLOSE,
}

private data class Token(
  val kind: TokenKind,
  val text: String,
  val line: Int,
  val column: Int,
)

/** Splits the text of the file: comments go, and whitespace of any kind only separates. */
private class CustomLexer(private val text: String) {
  private var position = 0
  private var line = 1
  private var column = 1

  private fun fail(
    message: String,
    atLine: Int = line,
    atColumn: Int = column,
  ): Nothing = throw CustomCommandsException("$CUSTOM_COMMANDS_FILE:$atLine:$atColumn: $message")

  private fun advance(): Char {
    val letter = text[position++]
    if (letter == '\n') {
      line++
      column = 1
    } else {
      column++
    }
    return letter
  }

  private fun skipBlockComment() {
    val startLine = line
    val startColumn = column
    advance()
    advance()
    while (position < text.length) {
      if (text.startsWith("#>", position)) {
        advance()
        advance()
        return
      }
      advance()
    }
    fail("this comment has no closing #>", startLine, startColumn)
  }

  // A string has no escapes: it ends at the next quote of its own kind, whatever comes between.
  private fun string(): Token {
    val startLine = line
    val startColumn = column
    val quote = advance()
    val begin = position
    while (position < text.length && text[position] != quote) advance()
    if (position == text.length) fail("this string has no closing $quote", startLine, startColumn)
    val value = text.substring(begin, position)
    advance()
    return Token(TokenKind.STRING, value, startLine, startColumn)
  }

  private fun name(): Token {
    val startLine = line
    val startColumn = column
    val begin = position
    while (position < text.length && isNameLetter(text[position])) advance()
    return Token(TokenKind.NAME, text.substring(begin, position), startLine, startColumn)
  }

  fun tokens(): List<Token> = buildList {
    while (position < text.length) {
      val letter = text[position]
      when {
        letter.isWhitespace() -> advance()
        text.startsWith("<#", position) -> skipBlockComment()
        letter == '#' -> while (position < text.length && text[position] != '\n') advance()
        letter == '"' || letter == '\'' || letter == '`' -> add(string())
        isNameLetter(letter) -> add(name())
        else -> {
          val kind =
            when (letter) {
              ':' -> TokenKind.COLON
              ';' -> TokenKind.SEMICOLON
              '(' -> TokenKind.OPEN
              ')' -> TokenKind.CLOSE
              else -> fail("'$letter' has no meaning here")
            }
          add(Token(kind, letter.toString(), line, column))
          advance()
        }
      }
    }
  }

  private fun isNameLetter(letter: Char) =
    letter.isLetterOrDigit() || letter == '-' || letter == '_' || letter == '.'
}

private class CustomParser(private val tokens: List<Token>) {
  private var position = 0

  private fun fail(
    token: Token?,
    message: String,
  ): Nothing {
    val place = token?.let { "${it.line}:${it.column}: " }.orEmpty()
    throw CustomCommandsException("$CUSTOM_COMMANDS_FILE:$place$message")
  }

  private fun peek() = tokens.getOrNull(position)

  private fun take(
    kind: TokenKind,
    wanted: String,
  ): Token {
    val token = peek()
    if (token?.kind != kind) fail(token ?: tokens.lastOrNull(), "expected $wanted")
    position++
    return token
  }

  private fun strings(): List<Token> = buildList {
    while (peek()?.kind == TokenKind.STRING) add(take(TokenKind.STRING, "a string"))
  }

  private fun step(): CustomStep {
    val token = peek()
    val shell = token?.kind == TokenKind.NAME && token.text == "s"
    if (shell) position++
    val open = take(TokenKind.OPEN, "'(' of a command")
    val values = strings()
    take(TokenKind.CLOSE, "')' after the strings of a command")
    if (shell) {
      if (values.size != 1) {
        fail(open, "s(...) takes one string, the whole command line: s(\"npm dev\")")
      }
      return CustomStep.Shell(values.single().text)
    }
    if (values.isEmpty()) fail(open, "a command names at least a program")
    return CustomStep.Program(values.map(Token::text))
  }

  fun commands(): List<CustomCommand> {
    val commands = linkedMapOf<String, CustomCommand>()
    while (peek() != null) {
      val name = take(TokenKind.NAME, "the name of a command")
      take(TokenKind.COLON, "':' after the name of a command")
      val steps = buildList {
        do add(step()) while (peek()?.kind != TokenKind.SEMICOLON && peek() != null)
      }
      take(TokenKind.SEMICOLON, "';' at the end of a command")
      if (name.text in commands) fail(name, "the command '${name.text}' is defined twice")
      commands[name.text] = CustomCommand(name.text, steps)
    }
    return commands.values.toList()
  }
}

/** The commands of the text of a `tazel_custom.txt`, in the order they are written. */
internal fun parseCustomCommands(text: String): List<CustomCommand> =
  CustomParser(CustomLexer(text).tokens()).commands()

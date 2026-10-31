/*
 * SPDX-FileCopyrightText: 2026 Progmasoft <support@progmasoft.com>
 * SPDX-License-Identifier: MPL-2.0 WITH AdditionRef-Progmasoft-Exception-2.0
 */

package com.progmasoft.tazel.custom.commands.lexer

import com.progmasoft.tazel.custom.commands.CUSTOM_COMMANDS_FILE
import com.progmasoft.tazel.custom.commands.CustomCommandsException

/** The kinds of [Token]. */
enum class TokenKind {
  NAME,
  STRING,
  COLON,
  SEMICOLON,
  OPEN,
  CLOSE,
}

/** One word of the file, with where it starts. */
data class Token(
  val kind: TokenKind,
  val text: String,
  val line: Int,
  val column: Int,
)

/** Splits the text of the file: comments go, and whitespace of any kind only separates. */
class CustomLexer(private val text: String) {
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

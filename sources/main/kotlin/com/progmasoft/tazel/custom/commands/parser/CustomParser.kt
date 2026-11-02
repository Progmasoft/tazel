/*
 * SPDX-FileCopyrightText: 2026 Progmasoft <support@progmasoft.com>
 * SPDX-License-Identifier: MPL-2.0 WITH AdditionRef-Progmasoft-Exception-2.0
 */

package com.progmasoft.tazel.custom.commands.parser

import com.progmasoft.tazel.custom.commands.CUSTOM_COMMANDS_FILE
import com.progmasoft.tazel.custom.commands.CUSTOM_NAME_END
import com.progmasoft.tazel.custom.commands.CustomArgument
import com.progmasoft.tazel.custom.commands.CustomCommand
import com.progmasoft.tazel.custom.commands.CustomCommandsException
import com.progmasoft.tazel.custom.commands.CustomStep
import com.progmasoft.tazel.custom.commands.MAX_CUSTOM_ARGUMENTS
import com.progmasoft.tazel.custom.commands.lexer.CustomLexer
import com.progmasoft.tazel.custom.commands.lexer.Token
import com.progmasoft.tazel.custom.commands.lexer.TokenKind

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

  private fun values(): List<Token> = buildList {
    while (true) {
      val kind = peek()?.kind
      if (kind != TokenKind.STRING && kind != TokenKind.PLACEHOLDER) break
      add(take(kind, "a string"))
    }
  }

  private fun argumentOf(token: Token): CustomArgument =
    when {
      token.kind == TokenKind.STRING -> CustomArgument.Text(token.text)
      token.text == "*" -> CustomArgument.All
      else -> CustomArgument.Position(token.text.toInt())
    }

  private fun step(): CustomStep {
    val token = peek()
    val shell = token?.kind == TokenKind.NAME && token.text == "s"
    if (shell) position++
    val open = take(TokenKind.OPEN, "'(' of a command")
    val values = values()
    take(TokenKind.CLOSE, "')' after the strings of a command")
    if (shell) {
      values
        .firstOrNull { it.kind == TokenKind.PLACEHOLDER }
        ?.let { fail(it, "s(...) takes no arguments; (...) passes them") }
      if (values.size != 1) {
        fail(open, "s(...) takes one string, the whole command line: s(\"npm dev\")")
      }
      return CustomStep.Shell(values.single().text)
    }
    if (values.isEmpty()) fail(open, "a command names at least a program")
    if (values.first().kind != TokenKind.STRING) {
      fail(values.first(), "a command starts with the name of its program")
    }
    return CustomStep.Program(values.map(::argumentOf))
  }

  fun commands(): List<CustomCommand> {
    val commands = linkedMapOf<String, CustomCommand>()
    while (peek() != null) {
      val name = take(TokenKind.NAME, "the name of a command")
      val words = buildList {
        add(name.text)
        while (peek()?.kind == TokenKind.NAME) add(take(TokenKind.NAME, "a word").text)
      }
      if (CUSTOM_NAME_END in words) {
        fail(name, "'$CUSTOM_NAME_END' ends a name on the command line, so a name cannot hold it")
      }
      if (words.size > MAX_CUSTOM_ARGUMENTS) {
        fail(name, "a name holds at most $MAX_CUSTOM_ARGUMENTS words")
      }
      take(TokenKind.COLON, "':' after the name of a command")
      val steps = buildList {
        do add(step()) while (peek()?.kind != TokenKind.SEMICOLON && peek() != null)
      }
      take(TokenKind.SEMICOLON, "';' at the end of a command")
      val joined = words.joinToString(" ")
      if (joined in commands) fail(name, "the command '$joined' is defined twice")
      val command = CustomCommand(joined, steps)
      if (words.size + command.required > MAX_CUSTOM_ARGUMENTS) {
        fail(
          name,
          "'$joined' needs ${command.required} arguments, and its name leaves room for " +
            "${MAX_CUSTOM_ARGUMENTS - words.size}",
        )
      }
      commands[joined] = command
    }
    return commands.values.toList()
  }
}

/** The commands of the text of a `tazel_custom.txt`, in the order they are written. */
fun parseCustomCommands(text: String): List<CustomCommand> =
  CustomParser(CustomLexer(text).tokens()).commands()

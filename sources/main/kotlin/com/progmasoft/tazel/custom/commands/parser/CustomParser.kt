/*
 * SPDX-FileCopyrightText: 2026 Progmasoft <support@progmasoft.com>
 * SPDX-License-Identifier: MPL-2.0 WITH AdditionRef-Progmasoft-Exception-2.0
 */

package com.progmasoft.tazel.custom.commands.parser

import com.progmasoft.tazel.custom.commands.CUSTOM_COMMANDS_FILE
import com.progmasoft.tazel.custom.commands.CustomCommand
import com.progmasoft.tazel.custom.commands.CustomCommandsException
import com.progmasoft.tazel.custom.commands.CustomStep
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
fun parseCustomCommands(text: String): List<CustomCommand> =
  CustomParser(CustomLexer(text).tokens()).commands()

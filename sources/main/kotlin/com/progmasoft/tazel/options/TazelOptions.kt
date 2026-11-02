/*
 * SPDX-FileCopyrightText: 2026 Progmasoft <support@progmasoft.com>
 * SPDX-License-Identifier: MPL-2.0 WITH AdditionRef-Progmasoft-Exception-2.0
 */

package com.progmasoft.tazel.options

import com.progmasoft.tazel.custom.commands.CustomCommandsException
import com.progmasoft.tazel.custom.commands.lexer.CustomLexer
import com.progmasoft.tazel.custom.commands.lexer.Token
import com.progmasoft.tazel.custom.commands.lexer.TokenKind
import java.nio.file.Files
import java.nio.file.Path

/**
 * The file of the options of Tazel on this machine, in a directory a command runs in or above it.
 */
const val OPTIONS_FILE = "tazel_options.txt"

/** How much of something a MicroVM gets. */
sealed interface Allocation {
  /** As much as Tazel chooses for this machine. */
  data object Auto : Allocation

  /** Exactly [amount]. */
  data class Fixed(val amount: Int) : Allocation
}

/** The options of `tazel_options.txt`; each one not written keeps its default. */
data class TazelOptions(
  /**
   * Whether what a project brings to run, plugins and custom commands among it, runs inside a
   * MicroVM of hardware virtualization. Without it the project file and plugins run in Tazel's JVM
   * with the rights of the user, and custom commands and tools run as processes of the host system.
   */
  val sandboxingWithMicrovm: Boolean = false,
  /** Whether the MicroVM reaches the network. */
  val microvmNetwork: Boolean = false,
  /** The memory of the MicroVM, in MiB. */
  val microvmMemory: Allocation = Allocation.Auto,
  /** The processors of the MicroVM. */
  val microvmCpus: Allocation = Allocation.Auto,
)

/** What is wrong with a `tazel_options.txt`, with where it is. */
class TazelOptionsException(message: String) : Exception(message)

private const val MIN_MEMORY = 256
private const val MAX_MEMORY = 1_048_576
private const val MAX_CPUS = 256

private val KEYS =
  listOf("sandboxing-with-microvm", "microvm-network", "microvm-memory", "microvm-cpus")

private class OptionsParser(private val tokens: List<Token>) {
  private var position = 0

  private fun fail(
    token: Token?,
    message: String,
  ): Nothing {
    val place = token?.let { "${it.line}:${it.column}: " }.orEmpty()
    throw TazelOptionsException("$OPTIONS_FILE:$place$message")
  }

  private fun take(
    kind: TokenKind,
    wanted: String,
  ): Token {
    val token = tokens.getOrNull(position)
    if (token?.kind != kind) fail(token ?: tokens.lastOrNull(), "expected $wanted")
    position++
    return token
  }

  private fun boolean(value: Token): Boolean =
    when (value.takeIf { it.kind == TokenKind.NAME }?.text) {
      "true" -> true
      "false" -> false
      else -> fail(value, "expected true or false")
    }

  // "auto" is a string, in any of the three quotes; a number stands bare.
  private fun allocation(
    value: Token,
    range: IntRange,
    unit: String,
  ): Allocation {
    if (value.kind == TokenKind.STRING) {
      if (value.text == "auto") return Allocation.Auto
      fail(value, "the only string here is \"auto\"")
    }
    val amount =
      value.text
        .takeIf { it.isNotEmpty() && it.all(Char::isDigit) && !it.startsWith("0") }
        ?.toIntOrNull() ?: fail(value, "expected a number of $unit or \"auto\"")
    if (amount !in range) fail(value, "$amount is outside ${range.first} to ${range.last} $unit")
    return Allocation.Fixed(amount)
  }

  fun options(): TazelOptions {
    var options = TazelOptions()
    val seen = mutableSetOf<String>()
    while (position < tokens.size) {
      val key = take(TokenKind.NAME, "the name of an option")
      if (key.text !in KEYS)
        fail(key, "'${key.text}' is not an option; the options are ${KEYS.joinToString()}")
      if (!seen.add(key.text)) fail(key, "'${key.text}' is set twice")
      take(TokenKind.COLON, "':' after ${key.text}")
      val value = tokens.getOrNull(position) ?: fail(key, "expected a value for ${key.text}")
      if (value.kind != TokenKind.NAME && value.kind != TokenKind.STRING) {
        fail(value, "expected a value for ${key.text}")
      }
      position++
      options =
        when (key.text) {
          "sandboxing-with-microvm" -> options.copy(sandboxingWithMicrovm = boolean(value))
          "microvm-network" -> options.copy(microvmNetwork = boolean(value))
          "microvm-memory" ->
            options.copy(microvmMemory = allocation(value, MIN_MEMORY..MAX_MEMORY, "MiB"))
          else -> options.copy(microvmCpus = allocation(value, 1..MAX_CPUS, "processors"))
        }
      // The last option may end the file without a ';'.
      if (position < tokens.size) take(TokenKind.SEMICOLON, "';' after the value of ${key.text}")
    }
    return options
  }
}

/** Reads the text of a `tazel_options.txt`, with the comments and strings of `tazel_custom.txt`. */
fun parseTazelOptions(text: String): TazelOptions {
  val tokens =
    try {
      CustomLexer(text, OPTIONS_FILE).tokens()
    } catch (error: CustomCommandsException) {
      throw TazelOptionsException(error.message.orEmpty())
    }
  return OptionsParser(tokens).options()
}

/** The nearest `tazel_options.txt` at or above [directory]. */
fun findTazelOptions(directory: Path): Path? =
  generateSequence(directory.toAbsolutePath(), Path::getParent)
    .map { it.resolve(OPTIONS_FILE) }
    .firstOrNull(Files::isRegularFile)

/**
 * Why a command must not run from [directory], or null when it may. Tazel has no MicroVM to start,
 * so a sandbox that is asked for stops every command that would run what a project brings, rather
 * than run it on the host system.
 */
fun sandboxRefusal(directory: Path): String? {
  val file = findTazelOptions(directory) ?: return null
  if (!parseTazelOptions(Files.readString(file)).sandboxingWithMicrovm) return null
  return "$file asks for sandboxing-with-microvm, but Tazel has no MicroVM to start, so " +
    "nothing ran. Set it to false to run without the sandbox."
}

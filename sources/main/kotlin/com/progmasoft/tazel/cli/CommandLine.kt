/*
 * SPDX-FileCopyrightText: 2026 Progmasoft <support@progmasoft.com>
 * SPDX-License-Identifier: MPL-2.0 WITH AdditionRef-Progmasoft-Exception-2.0
 */

package com.progmasoft.tazel.cli

import com.progmasoft.tazel.custom.commands.MAX_CUSTOM_ARGUMENTS
import com.progmasoft.tazel.dsl.ProjectConfigurationException
import com.progmasoft.tazel.dsl.byteSize

/**
 * The targets a command works on. Empty lists mean every target of that kind; a command line that
 * names any target works on the named ones only.
 */
data class TargetSelection(
  val viPkgs: List<String> = emptyList(),
  val executables: List<String> = emptyList(),
  val libraries: List<String> = emptyList(),
) {
  val isEverything: Boolean
    get() = viPkgs.isEmpty() && executables.isEmpty() && libraries.isEmpty()
}

/** A package of ViGet as it is written on the command line: `Publisher.Name`. */
data class PackageCoordinate(
  val publisher: String,
  val name: String,
) {
  override fun toString() = "$publisher.$name"
}

/** What `tazel lock graph --invert` shows the dependents of, by how it was written. */
sealed interface InvertTarget {
  /**
   * `MyPackage`: every project below the directory Tazel runs in whose `project.name` is [name].
   * Its dependents are the projects that use a ViGet package of that name from any publisher, or a
   * local `MyPackage.vipkg`.
   */
  data class Project(val name: String) : InvertTarget

  /** `Publisher.Name`: that ViGet package, in every version the projects declare. */
  data class Package(val coordinate: PackageCoordinate) : InvertTarget

  /** `Libraries/Local.vipkg`: the local package at that path of a project. */
  data class Local(val path: String) : InvertTarget
}

/** The commands of Tazel by the words that select them. */
enum class CommandName(val words: String, val summary: String) {
  CHECK("check", "check the sources of the project without writing an executable"),
  BUILD("build", "build the targets of the project"),
  RUN("run", "build the executables of the project and run them"),
  TEST("test", "build and run the test suites of the project"),
  FETCH("fetch", "fetch the dependencies of the project ahead of time"),
  CUSTOM("custom", "run a command of tazel_custom.txt, or list them"),
  LOCK_RESOLVE("lock resolve", "resolve the dependencies and renew LOCK.sqlite3"),
  LOCK_CREATE_DUMP(
    "lock create-dump",
    "write the lockfile as text, to LOCK.dump unless --path names a file",
  ),
  LOCK_GRAPH("lock graph", "print the dependency graph; renews LOCK.sqlite3 unless --locked"),
  UPDATE("update", "update the dependencies of the system and of the project"),
  INSTALL("install", "download a package from ViGet into the project"),
  VIPKG_CREATE("vipkg create", "write a local .vipkg for every target"),
  VIGET_PUSH("viget push", "build, test and publish the package to ViGet for the first time"),
  VIGET_UPDATE("viget update", "build, test and publish a new version of the package"),
  VIGET_VIPKG_DESC("viget vipkg-desc", "print the description of a package of ViGet"),
  CACHE_STATUS("cache status", "show what the build caches of the project hold"),
  CACHE_CLEAN("cache clean", "remove the local build cache of the project"),
  CACHE_PUSH("cache push", "send the local results to the remote build cache"),
  CACHE_PULL("cache pull", "fetch the results of the current sources from the remote build cache"),
  CACHE_KEYGEN("cache keygen", "print a new pair of keys that sign the results of a remote cache"),
  CACHE_SERVE("cache serve", "serve a remote build cache from a directory"),
  REMOTE_SERVE("remote serve", "compile for other machines"),
  VERSION("version", "print the version of Tazel"),
  HELP("help", "print this help, or the help of one command"),
  FMT("fmt", "format the sources of the project with talafmt"),
  LINT("lint", "lint the sources of the project with talalint"),
  JVM_START_DAEMON(
    "jvm start-daemon",
    "start the JVM daemon that keeps Tazel warm between commands",
  ),
  JVM_RESTART_DAEMON("jvm restart-daemon", "stop the JVM daemon and start it again"),
  JVM_STOP_DAEMON("jvm stop-daemon", "stop the JVM daemon"),
}

/** What `tazel jvm` does with the daemon. */
enum class DaemonAction(val commandName: CommandName) {
  START(CommandName.JVM_START_DAEMON),
  RESTART(CommandName.JVM_RESTART_DAEMON),
  STOP(CommandName.JVM_STOP_DAEMON),
}

/**
 * What a command line says about how a build spends the machine; it goes before the project file,
 * and what is null here is left to the project file.
 */
data class ExecutionOptions(
  val jobs: Int? = null,
  val cpus: Int? = null,
  val memory: Long? = null,
  val memoryPerJob: Long? = null,
  val keepGoing: Boolean = false,
  val incremental: Boolean? = null,
  val profile: String? = null,
  val remoteExecutor: String? = null,
  val noRemoteExecutor: Boolean = false,
)

/** One invocation of Tazel after its words became values. */
sealed interface Command {
  val name: CommandName

  /** [noCache] does the work even when an earlier run left its result; so do the commands below. */
  data class Check(
    val targets: TargetSelection,
    val noCache: Boolean = false,
    val execution: ExecutionOptions = ExecutionOptions(),
  ) : Command {
    override val name = CommandName.CHECK
  }

  data class Build(
    val targets: TargetSelection,
    val noCache: Boolean = false,
    val execution: ExecutionOptions = ExecutionOptions(),
  ) : Command {
    override val name = CommandName.BUILD
  }

  /** [programArguments] are what follows `--`; they are handed to the program unread. */
  data class Run(
    val targets: TargetSelection,
    val programArguments: List<String>,
    val noCache: Boolean = false,
    val execution: ExecutionOptions = ExecutionOptions(),
  ) : Command {
    override val name = CommandName.RUN
  }

  data class Test(
    val targets: TargetSelection,
    val noCache: Boolean = false,
    val execution: ExecutionOptions = ExecutionOptions(),
  ) : Command {
    override val name = CommandName.TEST
  }

  data object Fetch : Command {
    override val name = CommandName.FETCH
  }

  /**
   * Runs the command of `tazel_custom.txt` that [words] start with, and hands it the rest; lists
   * the commands when there are none.
   */
  data class Custom(val words: List<String>) : Command {
    override val name = CommandName.CUSTOM
  }

  data object LockResolve : Command {
    override val name = CommandName.LOCK_RESOLVE
  }

  /**
   * [path] is the file to write, as written after `--path`; LOCK.dump beside the project file when
   * absent. Its folder has to exist already.
   */
  data class LockCreateDump(val path: String? = null) : Command {
    override val name = CommandName.LOCK_CREATE_DUMP
  }

  /**
   * Prints the dependency graph. [locked] leaves LOCK.sqlite3 as it is and fails when it does not
   * record what the project declares. [allFeatures] turns every feature of the project on.
   * [duplicates] shows only the packages that are there in more than one version, [invert] the
   * projects that depend on one package, and [depth] how many levels below each root are printed.
   */
  data class LockGraph(
    val locked: Boolean = false,
    val allFeatures: Boolean = false,
    val duplicates: Boolean = false,
    val invert: InvertTarget? = null,
    val depth: Int? = null,
  ) : Command {
    override val name = CommandName.LOCK_GRAPH
  }

  data object Update : Command {
    override val name = CommandName.UPDATE
  }

  /** [global] installs for the system and not for the project. */
  data class Install(
    val coordinate: PackageCoordinate,
    val global: Boolean,
  ) : Command {
    override val name = CommandName.INSTALL
  }

  data object ViPkgCreate : Command {
    override val name = CommandName.VIPKG_CREATE
  }

  data object ViGetPush : Command {
    override val name = CommandName.VIGET_PUSH
  }

  data object ViGetUpdate : Command {
    override val name = CommandName.VIGET_UPDATE
  }

  /** Prints the `project.description` of [coordinate] as ViGet holds it. */
  data class ViGetVipkgDesc(val coordinate: PackageCoordinate) : Command {
    override val name = CommandName.VIGET_VIPKG_DESC
  }

  data object CacheStatus : Command {
    override val name = CommandName.CACHE_STATUS
  }

  data object CacheClean : Command {
    override val name = CommandName.CACHE_CLEAN
  }

  data object CachePush : Command {
    override val name = CommandName.CACHE_PUSH
  }

  data class CachePull(val targets: TargetSelection) : Command {
    override val name = CommandName.CACHE_PULL
  }

  data object CacheKeygen : Command {
    override val name = CommandName.CACHE_KEYGEN
  }

  /** Serves results from [directory] on [host] and [port], at most [maxGigabytes] of them. */
  data class CacheServe(
    val directory: String,
    val host: String = "127.0.0.1",
    val port: Int = 7723,
    val maxGigabytes: Long = 10,
  ) : Command {
    override val name = CommandName.CACHE_SERVE
  }

  /** Compiles for other machines on [host] and [port], [jobs] at a time. */
  data class RemoteServe(
    val host: String = "127.0.0.1",
    val port: Int = 7724,
    val jobs: Int? = null,
  ) : Command {
    override val name = CommandName.REMOTE_SERVE
  }

  data object Version : Command {
    override val name = CommandName.VERSION
  }

  /** [topic] is the command whose help was asked for; the general help when absent. */
  data class Help(val topic: CommandName?) : Command {
    override val name = CommandName.HELP
  }

  /** [toolArguments] are what follows `--`; they are handed to talafmt unread. */
  data class Fmt(val toolArguments: List<String>) : Command {
    override val name = CommandName.FMT
  }

  /**
   * Starts, restarts or stops the JVM daemon. These commands always run in the process that read
   * them; they are never handed to a daemon.
   */
  data class Daemon(
    val action: DaemonAction,
    /** The most memory a daemon this command starts may hold, such as `2g`. */
    val maxHeap: String? = null,
  ) : Command {
    override val name = action.commandName
  }

  /**
   * [workspace] lints every member of the workspace. [toolArguments] are what follows `--`; they
   * are handed to talalint unread.
   */
  data class Lint(
    val workspace: Boolean,
    val targets: TargetSelection,
    val toolArguments: List<String>,
  ) : Command {
    override val name = CommandName.LINT
  }
}

/** Whether Tazel uses something the terminal may or may not support. */
enum class WhenMode(val word: String) {
  /** Always, also in a pipe or in CI. */
  ALWAYS("always"),

  /** When the terminal supports it; the default. */
  AUTO("auto"),
  NEVER("never"),
}

/**
 * How Tazel writes, whatever the command: [color] for colors, [progress] for the progress bar, and
 * [quiet] to leave out the status lines. They may be written anywhere before `--`.
 */
data class OutputOptions(
  val color: WhenMode = WhenMode.AUTO,
  val progress: WhenMode = WhenMode.AUTO,
  val quiet: Boolean = false,
) {
  /**
   * Whether the progress bar may be drawn: --quiet hides it too, unless --progress asks for it
   * explicitly.
   */
  val effectiveProgress: WhenMode
    get() = if (quiet && progress == WhenMode.AUTO) WhenMode.NEVER else progress
}

/** The outcome of reading a command line: a command, or the reason there is none. */
sealed interface ParseResult {
  data class Parsed(
    val command: Command,
    val output: OutputOptions = OutputOptions(),
  ) : ParseResult

  /** [hint] suggests what was probably meant, when a word looks like a typo of a real one. */
  data class Rejected(
    val message: String,
    val hint: String? = null,
  ) : ParseResult
}

/**
 * Reads the arguments of one invocation, the program name excluded.
 *
 * The parser is written by hand so that every accepted line is a value of [Command] and nothing
 * else: there is no table of strings a later stage would have to interpret again. An option takes
 * its value from the next argument (`--vipkg Name`, `-p Name`), after `=` or `:` (`--vipkg=Name`,
 * `-p:Name`), or, for a short option, joined to it (`-pName`). After `=` or `:` an option that may
 * be repeated also takes a list: `-e=First,Second`. `--` ends the arguments Tazel reads.
 */
fun parseCommandLine(arguments: List<String>): ParseResult =
  try {
    val (output, rest) = outputOptions(arguments)
    val command = if (rest.isEmpty()) Command.Help(null) else ArgumentReader(rest).command()
    ParseResult.Parsed(command, output)
  } catch (problem: CommandLineProblem) {
    ParseResult.Rejected(problem.message ?: "the command line could not be read", problem.hint)
  }

/**
 * One option as it is written: [flag] is its name, `--vipkg` or `-p`; [attached] is the value
 * written in the same argument, and [separated] tells that `=` or `:` put it there, which lets an
 * option that may be repeated take a list.
 */
private class OptionWord(
  val written: String,
  val flag: String,
  val attached: String?,
  val separated: Boolean,
) {
  fun noValue() {
    if (attached != null) reject("$flag takes no value, and '$written' gives it one")
  }

  /** The option with [value], spelled the way it was written: `--color:x`, `--color=x`, `-ex`. */
  fun spelled(value: String): String =
    when {
      separated -> flag + written[flag.length] + value
      attached != null -> flag + value
      else -> "$flag $value"
    }
}

/** The option [argument] is, or `null` for an argument that is not one, such as `--` or a name. */
private fun optionWord(argument: String): OptionWord? {
  if (argument == "--" || argument.length < 2 || argument[0] != '-') return null
  if (argument.startsWith("--")) {
    val at = argument.indexOfFirst { it == '=' || it == ':' }
    return if (at < 0) OptionWord(argument, argument, null, false)
    else OptionWord(argument, argument.substring(0, at), argument.substring(at + 1), true)
  }
  val flag = argument.substring(0, 2)
  val rest = argument.substring(2)
  return when {
    rest.isEmpty() -> OptionWord(argument, flag, null, false)
    rest[0] == '=' || rest[0] == ':' -> OptionWord(argument, flag, rest.substring(1), true)
    else -> OptionWord(argument, flag, rest, false)
  }
}

private val outputFlags = listOf("--color", "--progress", "--quiet")

/**
 * Takes the options of [OutputOptions] out of [arguments], wherever they stand before `--`, and
 * returns them with the arguments that remain.
 */
private fun outputOptions(arguments: List<String>): Pair<OutputOptions, List<String>> {
  var output = OutputOptions()
  val seen = mutableSetOf<String>()
  val rest = mutableListOf<String>()
  var index = 0
  fun mode(word: OptionWord): WhenMode {
    if (!seen.add(word.flag)) reject("${word.flag} is written twice")
    val text =
      word.attached
        ?: arguments.getOrNull(++index)?.takeUnless { it.startsWith("-") }
        ?: reject("${word.flag} requires a value: always, auto or never")
    return WhenMode.entries.firstOrNull { it.word == text }
      ?: reject(
        "${word.flag} takes always, auto or never, and '$text' is none of them",
        hint(text, WhenMode.entries.map { it.word }, word::spelled),
      )
  }
  while (index < arguments.size) {
    val argument = arguments[index]
    // What follows `custom` belongs to the command it names, whatever it looks like.
    if (argument == "--" || (argument == "custom" && rest.isEmpty())) {
      rest += arguments.subList(index, arguments.size)
      break
    }
    val word = optionWord(argument)
    when (word?.flag) {
      "--color" -> output = output.copy(color = mode(word))
      "--progress" -> output = output.copy(progress = mode(word))
      "--quiet" -> {
        word.noValue()
        if (!seen.add(word.flag)) reject("--quiet is written twice")
        output = output.copy(quiet = true)
      }
      else -> rest += argument
    }
    index++
  }
  return output to rest
}

private class CommandLineProblem(
  message: String,
  val hint: String?,
) : Exception(message)

private fun reject(
  message: String,
  hint: String? = null,
): Nothing = throw CommandLineProblem(message, hint)

/** The hint for [word] among [candidates], written as [spell] spells the match. */
private fun hint(
  word: String,
  candidates: Iterable<String>,
  spell: (String) -> String = { it },
): String? = closestTo(word, candidates)?.let { didYouMean(word, spell(it)) }

/** The first word of every command, in the order of the help. */
private val commandWords = CommandName.entries.map { it.words.substringBefore(' ') }.distinct()

private val graphFlags =
  listOf("--locked", "--all-features", "--duplicates", "-d", "--invert", "-i", "--depth")

private val selectionFlags = listOf("--vipkg", "-p", "--executable", "-e", "--library", "-l")

private const val NO_CACHE = "--no-cache"

private val cacheServeFlags = listOf("--directory", "--host", "--port", "--max-size")

private val remoteServeFlags = listOf("--host", "--port", "--jobs", "-j")

private val executionFlags =
  listOf(
    "--jobs",
    "-j",
    "--cpus",
    "--memory",
    "--memory-per-job",
    "--keep-going",
    "-k",
    "--incremental",
    "--no-incremental",
    "--profile",
    "--remote-executor",
    "--no-remote-executor",
  )

/** The flags of [ExecutionOptions], which every command that builds takes. */
private class ExecutionBuilder {
  private var options = ExecutionOptions()
  private val seen = mutableSetOf<String>()

  private fun once(word: OptionWord) {
    if (!seen.add(word.flag.removePrefix("--no-").removePrefix("--"))) {
      reject("${word.flag} is written twice")
    }
  }

  private fun positive(
    word: OptionWord,
    reader: ArgumentReader,
  ): Int =
    reader.value(word).let { text ->
      text.toIntOrNull()?.takeIf { it >= 1 }
        ?: reject("${word.flag} takes a whole number from 1, and '$text' is none")
    }

  private fun size(
    word: OptionWord,
    reader: ArgumentReader,
  ): Long =
    reader.value(word).let { text ->
      try {
        byteSize(text, word.flag)
      } catch (problem: ProjectConfigurationException) {
        reject(problem.message.orEmpty())
      }
    }

  /** True when [word] was one of the flags and its value was taken from [reader]. */
  fun take(
    word: OptionWord?,
    reader: ArgumentReader,
  ): Boolean {
    when (word?.flag) {
      "--jobs",
      "-j" -> {
        if (!seen.add("jobs")) reject("${word.flag} is written twice")
        options = options.copy(jobs = positive(word, reader))
      }
      "--cpus" -> {
        once(word)
        options = options.copy(cpus = positive(word, reader))
      }
      "--memory" -> {
        once(word)
        options = options.copy(memory = size(word, reader))
      }
      "--memory-per-job" -> {
        once(word)
        options = options.copy(memoryPerJob = size(word, reader))
      }
      "--keep-going",
      "-k" -> {
        word.noValue()
        if (!seen.add("keep-going")) reject("${word.flag} is written twice")
        options = options.copy(keepGoing = true)
      }
      "--incremental",
      "--no-incremental" -> {
        word.noValue()
        once(word)
        options = options.copy(incremental = word.flag == "--incremental")
      }
      "--profile" -> {
        once(word)
        options = options.copy(profile = reader.value(word))
      }
      "--remote-executor",
      "--no-remote-executor" -> {
        once(word)
        options =
          if (word.flag == "--no-remote-executor") {
            word.noValue()
            options.copy(noRemoteExecutor = true)
          } else {
            options.copy(remoteExecutor = reader.value(word))
          }
      }
      else -> return false
    }
    return true
  }

  fun build() = options
}

/** The flags that select targets, shared by every command that takes them. */
private class SelectionBuilder {
  val viPkgs = mutableListOf<String>()
  val executables = mutableListOf<String>()
  val libraries = mutableListOf<String>()

  /** True when [word] was one of the selection flags and its values were taken from [reader]. */
  fun take(
    word: OptionWord?,
    reader: ArgumentReader,
  ): Boolean {
    val flag = word?.flag ?: return false
    val destination =
      when (flag) {
        "--vipkg",
        "-p" -> viPkgs
        "--executable",
        "-e" -> executables
        "--library",
        "-l" -> libraries
        else -> return false
      }
    for (value in reader.values(word)) {
      if (!isTargetName(value)) reject("$flag takes the name of a target, and '$value' is not one")
      if (value in destination) reject("$flag $value is written twice")
      destination += value
    }
    return true
  }

  fun build() = TargetSelection(viPkgs.toList(), executables.toList(), libraries.toList())
}

private class ArgumentReader(private val arguments: List<String>) {
  private var position = 0

  private fun hasNext() = position < arguments.size

  private fun next() = arguments[position++]

  /** The value of an option: the one written with it, or else the next argument. */
  fun value(word: OptionWord): String {
    word.attached?.let {
      if (it.isEmpty()) reject("${word.flag} requires a value after '${word.written}'")
      return it
    }
    if (!hasNext()) reject("${word.flag} requires a value")
    val value = next()
    if (value.startsWith("-")) reject("${word.flag} requires a value, and '$value' is an option")
    return value
  }

  /** The values of an option that may be repeated: after `=` or `:`, a list separated by commas. */
  fun values(word: OptionWord): List<String> =
    if (word.separated) value(word).split(',') else listOf(value(word))

  /** The next argument as an option, or `null` with the argument as it is written. */
  fun nextOption(): Pair<String, OptionWord?> = next().let { it to optionWord(it) }

  /** The arguments that are left, unread. */
  private fun passThrough(): List<String> {
    val rest = arguments.subList(position, arguments.size).toList()
    position = arguments.size
    return rest
  }

  private fun finish(command: CommandName) {
    if (hasNext()) reject("'${next()}' is not an argument of tazel ${command.words}")
  }

  /** The second word of a command of two words. */
  private fun action(
    first: String,
    vararg actions: String,
  ): String {
    val choices = actions.joinToString(" or ") { "tazel $first $it" }
    if (!hasNext()) reject("tazel $first needs an action: $choices")
    val action = next()
    if (action !in actions) {
      reject(
        "'$action' is not an action of tazel $first; write $choices",
        hint(action, actions.asList()) { "tazel $first $it" },
      )
    }
    return action
  }

  // The words belong to the command of the file, so that nothing in them is an option of Tazel.
  private fun custom(): Command.Custom {
    val words = passThrough()
    if (words.size > MAX_CUSTOM_ARGUMENTS) {
      reject("tazel custom takes at most $MAX_CUSTOM_ARGUMENTS arguments, and got ${words.size}")
    }
    return Command.Custom(words)
  }

  fun command(): Command =
    when (val word = next()) {
      "check" ->
        compiling(CommandName.CHECK).let { Command.Check(it.targets, it.noCache, it.execution) }
      "build" ->
        compiling(CommandName.BUILD).let { Command.Build(it.targets, it.noCache, it.execution) }
      "run" -> run()
      "test" ->
        compiling(CommandName.TEST).let { Command.Test(it.targets, it.noCache, it.execution) }
      "fetch" -> Command.Fetch.also { finish(CommandName.FETCH) }
      "custom" -> custom()
      "lock" ->
        when (action("lock", "resolve", "create-dump", "graph")) {
          "resolve" -> Command.LockResolve.also { finish(CommandName.LOCK_RESOLVE) }
          "graph" -> lockGraph()
          else -> lockCreateDump()
        }
      "update" -> Command.Update.also { finish(CommandName.UPDATE) }
      "install" -> install()
      "vipkg" -> {
        action("vipkg", "create")
        Command.ViPkgCreate.also { finish(CommandName.VIPKG_CREATE) }
      }
      "remote" ->
        when (action("remote", "serve")) {
          else -> remoteServe()
        }
      "viget" ->
        when (action("viget", "push", "update", "vipkg-desc")) {
          "push" -> Command.ViGetPush.also { finish(CommandName.VIGET_PUSH) }
          "update" -> Command.ViGetUpdate.also { finish(CommandName.VIGET_UPDATE) }
          else -> vipkgDesc()
        }
      "cache" ->
        when (action("cache", "status", "clean", "push", "pull", "keygen", "serve")) {
          "status" -> Command.CacheStatus.also { finish(CommandName.CACHE_STATUS) }
          "clean" -> Command.CacheClean.also { finish(CommandName.CACHE_CLEAN) }
          "push" -> Command.CachePush.also { finish(CommandName.CACHE_PUSH) }
          "pull" -> Command.CachePull(compiling(CommandName.CACHE_PULL).targets)
          "keygen" -> Command.CacheKeygen.also { finish(CommandName.CACHE_KEYGEN) }
          else -> cacheServe()
        }
      "version" -> Command.Version.also { finish(CommandName.VERSION) }
      "help" -> help()
      "fmt" -> fmt()
      "lint" -> lint()
      "jvm" -> {
        val action =
          when (action("jvm", "start-daemon", "restart-daemon", "stop-daemon")) {
            "start-daemon" -> DaemonAction.START
            "restart-daemon" -> DaemonAction.RESTART
            else -> DaemonAction.STOP
          }
        daemon(action)
      }
      else ->
        reject(
          "'$word' is not a command of tazel; write tazel help for the commands",
          if (word == "--help" || word == "-h") didYouMean(word, "tazel help")
          else hint(word, commandWords) { "tazel $it" },
        )
    }

  private fun lockCreateDump(): Command {
    var path: String? = null
    while (hasNext()) {
      val (argument, word) = nextOption()
      when (word?.flag) {
        "--path" -> {
          if (path != null) reject("--path is written twice")
          path = value(word)
          if (path.isBlank()) reject("--path takes the name of a file, and it is empty")
        }
        else ->
          reject(
            "'$argument' is not an argument of tazel lock create-dump",
            hint(word?.flag ?: argument, listOf("--path") + outputFlags),
          )
      }
    }
    return Command.LockCreateDump(path)
  }

  private fun cacheServe(): Command {
    var directory: String? = null
    var host: String? = null
    var port: Int? = null
    var size: Long? = null
    fun number(
      word: OptionWord,
      range: LongRange,
      what: String,
    ): Long {
      val text = value(word)
      return text.toLongOrNull()?.takeIf { it in range }
        ?: reject("${word.flag} takes $what, and '$text' is not one")
    }
    while (hasNext()) {
      val (argument, word) = nextOption()
      when (word?.flag) {
        "--directory" -> {
          if (directory != null) reject("--directory is written twice")
          directory = value(word)
        }
        "--host" -> {
          if (host != null) reject("--host is written twice")
          host = value(word)
        }
        "--port" -> {
          if (port != null) reject("--port is written twice")
          port = number(word, 0L..65535L, "a port").toInt()
        }
        "--max-size" -> {
          if (size != null) reject("--max-size is written twice")
          size = number(word, 1L..1_000_000L, "a number of gigabytes")
        }
        else ->
          reject(
            "'$argument' is not an argument of tazel cache serve",
            hint(word?.flag ?: argument, cacheServeFlags + outputFlags),
          )
      }
    }
    val defaults = Command.CacheServe("")
    return Command.CacheServe(
      directory ?: reject("tazel cache serve needs --directory: where the results are kept"),
      host ?: defaults.host,
      port ?: defaults.port,
      size ?: defaults.maxGigabytes,
    )
  }

  private fun vipkgDesc(): Command {
    if (!hasNext()) {
      reject("tazel viget vipkg-desc needs a package: tazel viget vipkg-desc Publisher.Name")
    }
    val argument = next()
    if (argument.startsWith("-")) {
      reject("'$argument' is not an option of tazel viget vipkg-desc; it takes Publisher.Name")
    }
    val command = Command.ViGetVipkgDesc(packageCoordinate(argument))
    finish(CommandName.VIGET_VIPKG_DESC)
    return command
  }

  private fun lockGraph(): Command {
    var graph = Command.LockGraph()
    val seen = mutableSetOf<String>()
    fun once(flag: String) {
      if (!seen.add(flag)) reject("$flag is written twice")
    }
    while (hasNext()) {
      val (argument, word) = nextOption()
      when (word?.flag) {
        "--locked" -> {
          word.noValue()
          graph = graph.copy(locked = true).also { once("--locked") }
        }
        "--all-features" -> {
          word.noValue()
          graph = graph.copy(allFeatures = true).also { once("--all-features") }
        }
        "--duplicates",
        "-d" -> {
          word.noValue()
          graph = graph.copy(duplicates = true).also { once("--duplicates") }
        }
        "--invert",
        "-i" -> {
          once("--invert")
          graph = graph.copy(invert = invertTarget(value(word)))
        }
        "--depth" -> {
          once("--depth")
          val text = value(word)
          val depth =
            text.toIntOrNull()?.takeIf { it >= 0 }
              ?: reject("--depth takes a number of levels, 0 or more, and '$text' is not one")
          graph = graph.copy(depth = depth)
        }
        else ->
          reject(
            "'$argument' is not an argument of tazel lock graph",
            hint(word?.flag ?: argument, graphFlags + outputFlags),
          )
      }
    }
    if (graph.duplicates && graph.invert != null) {
      reject("--duplicates and --invert each choose what to show; write one of them")
    }
    return graph
  }

  /** Takes --no-cache when [word] is that option; [seen] tells that it was written before. */
  private fun noCache(
    word: OptionWord?,
    seen: Boolean,
  ): Boolean {
    if (word?.flag != NO_CACHE) return false
    word.noValue()
    if (seen) reject("$NO_CACHE is written twice")
    return true
  }

  private fun daemon(action: DaemonAction): Command {
    var maxHeap: String? = null
    while (hasNext()) {
      val (argument, word) = nextOption()
      when (word?.flag) {
        "--max-heap" -> {
          if (action == DaemonAction.STOP) reject("--max-heap is for a daemon that starts")
          if (maxHeap != null) reject("--max-heap is written twice")
          val text = value(word)
          if (text.none(Char::isDigit)) {
            reject("--max-heap takes a size such as 2g, and '$text' is none")
          }
          maxHeap = text
        }
        else -> reject("'$argument' is not an argument of tazel ${action.commandName.words}")
      }
    }
    return Command.Daemon(action, maxHeap)
  }

  private fun remoteServe(): Command {
    var host: String? = null
    var port: Int? = null
    var jobs: Int? = null
    while (hasNext()) {
      val (argument, word) = nextOption()
      when (word?.flag) {
        "--host" -> host = value(word)
        "--port" ->
          port =
            value(word).let { text ->
              text.toIntOrNull()?.takeIf { it in 0..65535 }
                ?: reject("--port takes a number up to 65535, and '$text' is none")
            }
        "--jobs",
        "-j" ->
          jobs =
            value(word).let { text ->
              text.toIntOrNull()?.takeIf { it >= 1 }
                ?: reject("${word.flag} takes a whole number from 1, and '$text' is none")
            }
        else ->
          reject(
            "'$argument' is not an argument of tazel remote serve",
            hint(word?.flag ?: argument, remoteServeFlags + outputFlags),
          )
      }
    }
    val defaults = Command.RemoteServe()
    return Command.RemoteServe(host ?: defaults.host, port ?: defaults.port, jobs)
  }

  private class Compiling(
    val targets: TargetSelection,
    val noCache: Boolean,
    val execution: ExecutionOptions,
  )

  /** A command that compiles: its arguments select targets and say how to spend the machine. */
  private fun compiling(command: CommandName): Compiling {
    val selection = SelectionBuilder()
    val execution = ExecutionBuilder()
    var noCache = false
    while (hasNext()) {
      val (argument, word) = nextOption()
      if (noCache(word, noCache)) {
        noCache = true
      } else if (!selection.take(word, this) && !execution.take(word, this)) {
        reject(
          "'$argument' is not an argument of tazel ${command.words}",
          hint(word?.flag ?: argument, selectionFlags + NO_CACHE + executionFlags + outputFlags),
        )
      }
    }
    return Compiling(selection.build(), noCache, execution.build())
  }

  private fun run(): Command {
    val selection = SelectionBuilder()
    val execution = ExecutionBuilder()
    var noCache = false
    while (hasNext()) {
      val (argument, word) = nextOption()
      if (argument == "--") {
        return Command.Run(selection.build(), passThrough(), noCache, execution.build())
      }
      if (noCache(word, noCache)) {
        noCache = true
      } else if (!selection.take(word, this) && !execution.take(word, this)) {
        reject(
          "'$argument' is not an argument of tazel run; program arguments follow '--'",
          hint(word?.flag ?: argument, selectionFlags + NO_CACHE + executionFlags + outputFlags),
        )
      }
    }
    return Command.Run(selection.build(), emptyList(), noCache, execution.build())
  }

  private fun install(): Command {
    var coordinate: PackageCoordinate? = null
    var global = false
    while (hasNext()) {
      val (argument, word) = nextOption()
      when (word?.flag) {
        "--global",
        "-g" -> {
          word.noValue()
          if (global) reject("--global is written twice")
          global = true
        }
        else -> {
          if (argument.startsWith("-")) {
            reject(
              "'$argument' is not an option of tazel install",
              hint(word?.flag ?: argument, listOf("--global", "-g") + outputFlags),
            )
          }
          if (coordinate != null)
            reject("tazel install takes one package, and '$argument' is a second")
          coordinate = packageCoordinate(argument)
        }
      }
    }
    return Command.Install(
      coordinate ?: reject("tazel install needs a package: tazel install Publisher.Name"),
      global,
    )
  }

  private fun help(): Command {
    if (!hasNext()) return Command.Help(null)
    val words = passThrough().joinToString(" ")
    val topic =
      CommandName.entries.firstOrNull { it.words == words }
        ?: reject(
          "'$words' is not a command of tazel; write tazel help for the commands",
          hint(words, CommandName.entries.map { it.words }) { "tazel help $it" },
        )
    return Command.Help(topic)
  }

  private fun fmt(): Command {
    if (!hasNext()) return Command.Fmt(emptyList())
    val argument = next()
    if (argument != "--") {
      reject("'$argument' is not an argument of tazel fmt; options of talafmt follow '--'")
    }
    return Command.Fmt(passThrough())
  }

  private fun lint(): Command {
    val selection = SelectionBuilder()
    var workspace = false
    var toolArguments = emptyList<String>()
    while (hasNext()) {
      val (argument, word) = nextOption()
      if (argument == "--") {
        toolArguments = passThrough()
        break
      }
      if (word?.flag == "--workspace") {
        word.noValue()
        if (workspace) reject("--workspace is written twice")
        workspace = true
      } else if (!selection.take(word, this)) {
        reject(
          "'$argument' is not an argument of tazel lint; options of talalint follow '--'",
          hint(word?.flag ?: argument, selectionFlags + "--workspace" + outputFlags),
        )
      }
    }
    val targets = selection.build()
    if (workspace && !targets.isEverything) {
      reject("--workspace lints every member; it cannot be combined with a selection of targets")
    }
    return Command.Lint(workspace, targets, toolArguments)
  }
}

private val identifier = Regex("[A-Za-z_][A-Za-z0-9_]*")

/** A target is named like the declaration it comes from: one case-sensitive identifier. */
internal fun isTargetName(text: String) = identifier.matches(text)

/**
 * The target of `--invert`. A path that ends in `.vipkg` is a local package, so `Name.vipkg` is
 * never read as the ViGet package `Name.vipkg`; a word with one dot is a ViGet package, and one
 * identifier the name of a project.
 */
private fun invertTarget(text: String): InvertTarget =
  when {
    text.endsWith(".vipkg", ignoreCase = true) ->
      InvertTarget.Local(text.replace('\\', '/').removePrefix("./"))
    '.' in text -> InvertTarget.Package(packageCoordinate(text))
    identifier.matches(text) -> InvertTarget.Project(text)
    else ->
      reject(
        "--invert takes a project name, Publisher.Name or the path of a .vipkg, and '$text' is " +
          "none of them"
      )
  }

private fun packageCoordinate(text: String): PackageCoordinate {
  val parts = text.split('.')
  if (parts.size != 2 || parts.any { !identifier.matches(it) }) {
    reject("a package is written Publisher.Name, and '$text' is not")
  }
  return PackageCoordinate(parts[0], parts[1])
}

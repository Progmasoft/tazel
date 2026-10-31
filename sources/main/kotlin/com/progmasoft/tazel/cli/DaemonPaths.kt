/*
 * SPDX-FileCopyrightText: 2026 Progmasoft <support@progmasoft.com>
 * SPDX-License-Identifier: MPL-2.0 WITH AdditionRef-Progmasoft-Exception-2.0
 */

package com.progmasoft.tazel.cli

import java.io.File
import java.io.IOException
import java.nio.file.FileAlreadyExistsException
import java.nio.file.Files
import java.nio.file.Path
import java.nio.file.attribute.PosixFilePermissions
import java.security.MessageDigest
import java.security.SecureRandom

/**
 * Where the daemon of one Tazel installation keeps its files, for the user that runs it.
 *
 * One daemon serves one user and one installation: [key] is derived from the version of Tazel, the
 * Java runtime and the classpath, so a second installation or a newer version gets a daemon of its
 * own rather than talking to one that runs different code. The directory is private to the user: on
 * POSIX systems it is created with mode 700, on Windows it lives in the user's local application
 * data.
 */
internal class DaemonPaths(
  val directory: Path,
  val key: String,
) {
  /** The socket the daemon listens on. */
  val socket: Path = directory.resolve("$key.sock")

  /** The secret a client sends first; only the user can read it. */
  val token: Path = directory.resolve("$key.token")

  /** Held by the running daemon, so that a second one cannot start. */
  val lock: Path = directory.resolve("$key.lock")

  /** The process id of the running daemon. */
  val pid: Path = directory.resolve("$key.pid")

  /** What the daemon writes outside a command: startup, shutdown, failures. */
  val log: Path = directory.resolve("$key.log")

  /** Creates the private directory. */
  fun prepare() {
    if (Files.isDirectory(directory)) return
    if (isPosix()) {
      Files.createDirectories(directory.parent)
      try {
        Files.createDirectory(
          directory,
          PosixFilePermissions.asFileAttribute(PosixFilePermissions.fromString("rwx------")),
        )
      } catch (_: FileAlreadyExistsException) {
        // Another process created it first.
      }
    } else {
      Files.createDirectories(directory)
    }
  }

  /** Writes a new random token that only the user can read, and returns it. */
  fun newToken(): String {
    val bytes = ByteArray(32).also(SecureRandom()::nextBytes)
    val secret = bytes.joinToString("") { "%02x".format(it) }
    val temporary = Files.createTempFile(directory, "$key-", ".token")
    try {
      if (isPosix()) {
        Files.setPosixFilePermissions(temporary, PosixFilePermissions.fromString("rw-------"))
      }
      Files.writeString(temporary, secret)
      Files.move(
        temporary,
        token,
        java.nio.file.StandardCopyOption.REPLACE_EXISTING,
        java.nio.file.StandardCopyOption.ATOMIC_MOVE,
      )
    } finally {
      Files.deleteIfExists(temporary)
    }
    return secret
  }

  /** The token of the running daemon, or null when there is none. */
  fun readToken(): String? =
    try {
      Files.readString(token).trim().takeIf { it.length == 64 }
    } catch (_: IOException) {
      null
    }

  /** Removes the files a daemon that is gone left behind. */
  fun clean() {
    listOf(socket, token, pid).forEach { runCatching { Files.deleteIfExists(it) } }
  }

  companion object {
    /** The daemon files of this installation for the user running Tazel. */
    fun forInstallation(
      tazelVersion: String,
      environment: (String) -> String? = System::getenv,
    ): DaemonPaths =
      DaemonPaths(
        stateDirectory(environment).resolve("daemon"),
        installationKey(tazelVersion, System.getProperty("java.home"), classpath()),
      )

    internal fun installationKey(
      tazelVersion: String,
      javaHome: String,
      classpath: List<Path>,
    ): String {
      val digest = MessageDigest.getInstance("SHA-256")
      fun line(text: String) {
        digest.update(text.toByteArray(Charsets.UTF_8))
        digest.update(0)
      }
      line("tazel-daemon $DAEMON_PROTOCOL")
      line(tazelVersion)
      line(javaHome)
      classpath.forEach { entry ->
        val absolute = entry.toAbsolutePath().normalize()
        val modified = runCatching { Files.getLastModifiedTime(absolute).toMillis() }.getOrNull()
        line("$absolute $modified")
      }
      // A socket path is limited to about a hundred bytes, so the key stays short.
      return digest.digest().take(8).joinToString("") { "%02x".format(it) }
    }

    /**
     * `TAZEL_DAEMON_DIR` when it is set, otherwise the per-user state directory of the operating
     * system.
     */
    internal fun stateDirectory(
      environment: (String) -> String?,
      osName: String = System.getProperty("os.name"),
      home: String = System.getProperty("user.home"),
    ): Path {
      environment("TAZEL_DAEMON_DIR")?.takeIf(String::isNotBlank)?.let {
        return Path.of(it)
      }
      val os = osName.lowercase()
      return when {
        os.startsWith("windows") ->
          Path.of(
            environment("LOCALAPPDATA")?.takeIf(String::isNotBlank)
              ?: File(home, "AppData/Local").path,
            "Progmasoft",
            "Tazel",
          )
        os.startsWith("mac") ->
          Path.of(home, "Library", "Application Support", "Progmasoft", "Tazel")
        else ->
          Path.of(
            environment("XDG_STATE_HOME")?.takeIf(String::isNotBlank)
              ?: File(home, ".local/state").path,
            "progmasoft",
            "tazel",
          )
      }
    }

    private fun classpath(): List<Path> =
      System.getProperty("java.class.path")
        .split(File.pathSeparator)
        .filter(String::isNotBlank)
        .map(Path::of)
  }
}

private fun isPosix() =
  java.nio.file.FileSystems.getDefault().supportedFileAttributeViews().contains("posix")

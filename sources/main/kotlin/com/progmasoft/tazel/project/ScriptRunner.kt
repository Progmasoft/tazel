/*
 * SPDX-FileCopyrightText: 2026 Progmasoft <support@progmasoft.com>
 * SPDX-License-Identifier: MPL-2.0 WITH AdditionRef-Progmasoft-Exception-2.0
 */

package com.progmasoft.tazel.project

import com.progmasoft.tazel.dsl.ProjectException
import com.progmasoft.tazel.label.LabelException
import java.nio.file.Path
import kotlin.reflect.KClass
import kotlin.script.experimental.api.ResultValue
import kotlin.script.experimental.api.ResultWithDiagnostics
import kotlin.script.experimental.api.ScriptCompilationConfiguration
import kotlin.script.experimental.api.ScriptDiagnostic
import kotlin.script.experimental.api.ScriptEvaluationConfiguration
import kotlin.script.experimental.api.compilerOptions
import kotlin.script.experimental.api.defaultImports
import kotlin.script.experimental.api.implicitReceivers
import kotlin.script.experimental.host.toScriptSource
import kotlin.script.experimental.jvm.dependenciesFromClassContext
import kotlin.script.experimental.jvm.jvm
import kotlin.script.experimental.jvmhost.BasicJvmScriptingHost

/** What every project file sees without importing it. */
private val IMPORTS =
  listOf(
    "com.progmasoft.tazel.dsl.*",
    "kotlin.time.Duration.Companion.seconds",
    "kotlin.time.Duration.Companion.minutes",
    "kotlin.time.Duration.Companion.hours",
  )

/**
 * Compiles and runs project files inside Tazel, so a machine needs no Kotlin beside it. A project
 * file is a Kotlin script whose top level is the members of one receiver object.
 */
class ScriptRunner {
  private val host by lazy { BasicJvmScriptingHost() }

  /**
   * Runs [source], the text of [file], with [receiver] as its implicit receiver. [shownName] is how
   * messages name the file, such as `core/BUILD.kts`.
   */
  fun <T : Any> run(file: Path, shownName: String, source: String, receiver: T, type: KClass<T>) {
    val compilation = ScriptCompilationConfiguration {
      defaultImports(IMPORTS)
      implicitReceivers(type)
      compilerOptions.append("-jvm-target", "25")
      jvm { dependenciesFromClassContext(ScriptRunner::class, wholeClasspath = true) }
    }
    val evaluation = ScriptEvaluationConfiguration { implicitReceivers(receiver) }
    val result =
      synchronized(compilerLock) {
        host.eval(source.toScriptSource(file.fileName.toString()), compilation, evaluation)
      }
    val errors =
      result.reports.filter {
        it.severity == ScriptDiagnostic.Severity.ERROR ||
          it.severity == ScriptDiagnostic.Severity.FATAL
      }
    if (result is ResultWithDiagnostics.Failure || errors.isNotEmpty()) {
      throw ProjectException(
        errors.ifEmpty { result.reports }.joinToString("\n") { describe(shownName, it) }
      )
    }
    val value = (result as ResultWithDiagnostics.Success).value.returnValue
    if (value is ResultValue.Error) {
      val error = value.error
      val reason =
        when (error) {
          is ProjectException,
          is LabelException -> error.message
          else -> "${error.javaClass.simpleName}: ${error.message}"
        }
      throw ProjectException("$shownName: $reason")
    }
  }

  private fun describe(shownName: String, report: ScriptDiagnostic): String {
    val start = report.location?.start
    val position = if (start == null) "" else ":${start.line}:${start.col}"
    return "$shownName$position: ${report.message}"
  }
}

/** The Kotlin compiler is used by one project file at a time. */
private val compilerLock = Any()

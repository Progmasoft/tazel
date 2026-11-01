/*
 * SPDX-FileCopyrightText: 2026 Progmasoft <support@progmasoft.com>
 * SPDX-License-Identifier: MPL-2.0 WITH AdditionRef-Progmasoft-Exception-2.0
 */

package com.progmasoft.tazel.lock

import com.progmasoft.tazel.dsl.DependencyDeclaration
import com.progmasoft.tazel.dsl.DependencyManifest
import com.progmasoft.tazel.dsl.PackageDependency
import com.progmasoft.tazel.dsl.ProjectConfigurationException
import com.progmasoft.tazel.dsl.Stability
import java.nio.file.Files
import java.nio.file.Path
import java.sql.DriverManager
import kotlin.test.Test
import kotlin.test.assertContains
import kotlin.test.assertEquals
import kotlin.test.assertFailsWith
import kotlin.test.assertTrue

class ProjectLockFileTest {
  private val project =
    LockedProject(
      DependencyManifest(
        listOf(DependencyDeclaration(PackageDependency("Publisher", "Core", "0.3.1", Stability.RC)))
      ),
      emptyList(),
    )

  private fun inLockfile(work: (Path) -> Unit) {
    val root = Files.createTempDirectory("tazel-lock-")
    try {
      ProjectLockFile.write(root, project)
      work(root.resolve(ProjectLockFile.FILE_NAME))
    } finally {
      root.toFile().deleteRecursively()
    }
  }

  private fun setFormat(
    lockfile: Path,
    format: Int,
  ) {
    DriverManager.getConnection("jdbc:sqlite:${lockfile.toAbsolutePath()}").use { connection ->
      connection.createStatement().use {
        it.executeUpdate("UPDATE metadata SET value = '$format' WHERE key = 'format_version'")
      }
    }
  }

  @Test
  fun aReleaseCandidateIsRecorded() {
    inLockfile { lockfile -> assertEquals(project, ProjectLockFile.read(lockfile)) }
  }

  @Test
  fun aLockfileOfFormatFourStaysCurrent() {
    inLockfile { lockfile ->
      setFormat(lockfile, 4)
      assertEquals(project, ProjectLockFile.read(lockfile))
      assertTrue(ProjectLockFile.isCurrent(lockfile, project))
    }
  }

  @Test
  fun otherFormatsAreRefusedBeforeTheirContentsAreRead() {
    for (format in listOf(3, 6)) {
      inLockfile { lockfile ->
        setFormat(lockfile, format)
        val problem =
          assertFailsWith<ProjectConfigurationException> { ProjectLockFile.read(lockfile) }
        assertContains(problem.message.orEmpty(), "tazel lock resolve")
      }
    }
  }
}

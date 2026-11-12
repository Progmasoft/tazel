/*
 * SPDX-FileCopyrightText: 2026 Progmasoft <support@progmasoft.com>
 * SPDX-License-Identifier: MPL-2.0 WITH AdditionRef-Progmasoft-Exception-2.0
 */

import org.gradle.api.JavaVersion
import org.jetbrains.kotlin.gradle.dsl.JvmTarget

plugins {
  kotlin("jvm") version "2.4.0"
  id("com.diffplug.spotless") version "8.9.0"
  application
  jacoco
}

group = "com.progmasoft.tazel"
version = "0.1.0"

repositories {
  mavenCentral()
}

dependencies {
  implementation("com.github.ajalt.mordant:mordant:3.0.1")
  implementation("org.jetbrains.kotlin:kotlin-scripting-common:2.4.0")
  implementation("org.jetbrains.kotlin:kotlin-scripting-jvm:2.4.0")
  implementation("org.jetbrains.kotlin:kotlin-scripting-jvm-host:2.4.0")
  testImplementation(kotlin("test"))
}

kotlin {
  jvmToolchain(25)
  compilerOptions {
    jvmTarget = JvmTarget.JVM_25
    allWarningsAsErrors = true
  }
}

java {
  sourceCompatibility = JavaVersion.VERSION_25
  targetCompatibility = JavaVersion.VERSION_25
}

application {
  mainClass = "com.progmasoft.tazel.cli.MainKt"
  applicationName = "tazel"
  // The embedded Kotlin compiler reaches for sun.misc.Unsafe; without the second flag the JVM
  // prints a warning into the output of every command.
  applicationDefaultJvmArgs =
    listOf("--enable-native-access=ALL-UNNAMED", "--sun-misc-unsafe-memory-access=allow")
}

sourceSets {
  main {
    kotlin.srcDir("sources/main/kotlin")
    resources.srcDir("sources/main/resources")
  }
  test {
    kotlin.srcDir("sources/test/kotlin")
    resources.srcDir("sources/test/resources")
  }
}

spotless {
  kotlin {
    target("sources/**/*.kt", "api/sources/**/*.kt", "gradle-plugin/sources/**/*.kt")
    ktfmt().googleStyle()
    trimTrailingWhitespace()
    endWithNewline()
  }
  kotlinGradle {
    target("*.gradle.kts", "api/*.gradle.kts", "gradle-plugin/*.gradle.kts")
    ktlint()
    trimTrailingWhitespace()
    endWithNewline()
  }
}

// The distribution archives keep no file times and a fixed order, so the same commit packs into
// the same bytes.
tasks.withType<AbstractArchiveTask>().configureEach {
  isPreserveFileTimestamps = false
  isReproducibleFileOrder = true
}

tasks.jar {
  manifest {
    attributes("Implementation-Title" to "Tazel", "Implementation-Version" to project.version)
  }
}

tasks.test {
  useJUnitPlatform()
  maxHeapSize = "512m"
  jvmArgs("--enable-native-access=ALL-UNNAMED", "--sun-misc-unsafe-memory-access=allow")
}

jacoco {
  toolVersion = "0.8.15"
}

tasks.jacocoTestReport {
  dependsOn(tasks.test)
  reports {
    xml.required = true
    html.required = false
  }
}

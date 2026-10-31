/*
 * SPDX-FileCopyrightText: 2026 Progmasoft <support@progmasoft.com>
 * SPDX-License-Identifier: MPL-2.0 WITH AdditionRef-Progmasoft-Exception-2.0
 */

import org.jetbrains.kotlin.gradle.dsl.JvmTarget
import org.jetbrains.kotlin.gradle.dsl.KotlinVersion

// The Gradle plugin com.progmasoft.tazel.plugin, which builds plugins of the Tazel project file.
// It runs inside Gradle, with the Kotlin standard library Gradle ships: it does not bundle one,
// compiles for the Kotlin of Gradle 9.0 and runs on its JVM.
plugins {
  kotlin("jvm")
  `java-gradle-plugin`
  id("com.gradle.plugin-publish") version "2.0.0"
}

group = "com.progmasoft.tazel"
version = rootProject.version

repositories {
  mavenCentral()
}

dependencyLocking { lockAllConfigurations() }

dependencies {
  compileOnly(kotlin("stdlib"))
  testImplementation(kotlin("test"))
}

kotlin {
  jvmToolchain(25)
  compilerOptions {
    jvmTarget = JvmTarget.JVM_17
    apiVersion = KotlinVersion.KOTLIN_2_2
    languageVersion = KotlinVersion.KOTLIN_2_2
  }
}

java {
  sourceCompatibility = JavaVersion.VERSION_17
  targetCompatibility = JavaVersion.VERSION_17
}

sourceSets {
  main { kotlin.srcDir("sources/main/kotlin") }
  test { kotlin.srcDir("sources/test/kotlin") }
}

// The version of the plugin is the version of Tazel; tazelApi() asks for the API of that version.
val versionResource =
  tasks.register("versionResource") {
    val directory = layout.buildDirectory.dir("generated/version")
    val pluginVersion = project.version.toString()
    inputs.property("version", pluginVersion)
    outputs.dir(directory)
    doLast {
      directory.get().asFile.resolve("tazel-gradle-plugin.properties").apply {
        parentFile.mkdirs()
        writeText("version=$pluginVersion\n")
      }
    }
  }
sourceSets.main { resources.srcDir(versionResource) }

tasks.withType<AbstractArchiveTask>().configureEach {
  isPreserveFileTimestamps = false
  isReproducibleFileOrder = true
}

tasks.test {
  useJUnitPlatform()
}

gradlePlugin {
  website = "https://tazel.tala.run/"
  vcsUrl = "https://github.com/Progmasoft/tazel"
  plugins {
    create("tazelPlugin") {
      id = "com.progmasoft.tazel.plugin"
      implementationClass = "com.progmasoft.tazel.gradle.TazelPluginPlugin"
      displayName = "Tazel plugin"
      description =
        "Builds a plugin of the Tazel project file: adds the Tazel API from " +
        "https://maven.progmasoft.com/tazel as tazelApi(), and writes the descriptor and the " +
        "service entry of the plugin from the tazelPlugin { } block."
      // The Portal refuses the tags "gradle" and "plugin".
      tags = listOf("tazel", "tala", "build", "kotlin-dsl")
    }
  }
}

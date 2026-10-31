/*
 * SPDX-FileCopyrightText: 2026 Progmasoft <support@progmasoft.com>
 * SPDX-License-Identifier: MPL-2.0 WITH AdditionRef-Progmasoft-Exception-2.0
 */

import org.jetbrains.kotlin.gradle.dsl.JvmTarget

// The API that plugins of the Tazel project file compile against: com.progmasoft.api.tazel and its
// packages, published as com.progmasoft.tazel:tazel-api to https://maven.progmasoft.com/tazel.
// A plugin does not bundle it; Tazel provides it at run time.
plugins {
  kotlin("jvm")
  `java-library`
  `maven-publish`
}

group = "com.progmasoft.tazel"
version = rootProject.version

repositories {
  mavenCentral()
}

dependencyLocking { lockAllConfigurations() }

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
  withSourcesJar()
}

sourceSets {
  main { kotlin.srcDir("sources/main/kotlin") }
}

tasks.withType<AbstractArchiveTask>().configureEach {
  isPreserveFileTimestamps = false
  isReproducibleFileOrder = true
}

tasks.jar {
  manifest {
    attributes("Implementation-Title" to "Tazel API", "Implementation-Version" to project.version)
  }
}

publishing {
  publications {
    create<MavenPublication>("api") {
      artifactId = "tazel-api"
      from(components["java"])
      pom {
        name = "Tazel API"
        description = "The API that plugins of the Tazel project file compile against."
        url = "https://tazel.tala.run/"
        licenses {
          license {
            name = "MPL-2.0 WITH AdditionRef-Progmasoft-Exception-2.0"
            url = "https://github.com/Progmasoft/tazel/blob/main/LICENSE.txt"
          }
        }
        developers {
          developer {
            name = "Progmasoft"
            email = "support@progmasoft.com"
          }
        }
        scm {
          url = "https://github.com/Progmasoft/tazel"
          connection = "scm:git:https://github.com/Progmasoft/tazel.git"
        }
      }
    }
  }
  repositories {
    // A local repository, which is copied to https://maven.progmasoft.com/tazel.
    maven {
      name = "staging"
      url =
        rootProject.layout.buildDirectory
          .dir("maven")
          .get()
          .asFile
          .toURI()
    }
  }
}

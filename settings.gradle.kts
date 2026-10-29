/*
 * SPDX-FileCopyrightText: 2026 Progmasoft <support@progmasoft.com>
 * SPDX-License-Identifier: MPL-2.0 WITH AdditionRef-Progmasoft-Exception-1.1
 */

rootProject.name = "tazel"

// The API of plugins, which Tazel implements and plugins compile against.
include("tazel-api")
project(":tazel-api").projectDir = file("api")

// The Gradle plugin com.progmasoft.tazel.plugin, for building plugins.
include("tazel-gradle-plugin")
project(":tazel-gradle-plugin").projectDir = file("gradle-plugin")

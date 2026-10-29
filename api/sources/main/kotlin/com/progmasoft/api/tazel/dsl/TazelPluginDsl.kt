/*
 * SPDX-FileCopyrightText: 2026 Progmasoft <support@progmasoft.com>
 * SPDX-License-Identifier: MPL-2.0 WITH AdditionRef-Progmasoft-Exception-1.1
 */

package com.progmasoft.api.tazel.dsl

/**
 * Marks the scopes of a plugin DSL, so that a nested block of the project file cannot reach the
 * members of the block around it by mistake: inside `cc { c { } }`, `c` sees only its own members.
 */
@DslMarker
@Target(AnnotationTarget.CLASS, AnnotationTarget.TYPEALIAS, AnnotationTarget.TYPE)
annotation class TazelPluginDsl

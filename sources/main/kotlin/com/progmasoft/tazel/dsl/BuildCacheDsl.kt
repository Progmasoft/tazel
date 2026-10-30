/*
 * SPDX-FileCopyrightText: 2026 Progmasoft <support@progmasoft.com>
 * SPDX-License-Identifier: MPL-2.0 WITH AdditionRef-Progmasoft-Exception-1.1
 */

package com.progmasoft.tazel.dsl

import java.net.URI
import java.net.URISyntaxException
import java.security.GeneralSecurityException
import java.security.KeyFactory
import java.security.KeyPairGenerator
import java.security.PrivateKey
import java.security.PublicKey
import java.security.spec.PKCS8EncodedKeySpec
import java.security.spec.X509EncodedKeySpec
import java.util.Base64

/** The cache in `.tazel/cache`, which is asked before any remote one. */
data class LocalCacheSettings(
  val enabled: Boolean = true,
  val maxEntries: Int = DEFAULT_MAX_ENTRIES,
) {
  companion object {
    const val DEFAULT_MAX_ENTRIES = 8
  }
}

/**
 * A cache shared over HTTP. [token] and [signingKey] are left out of [toString], so they never
 * reach a log. With a [publicKey], only results signed by the matching [signingKey] are used.
 */
data class RemoteCacheSettings(
  val url: String,
  val read: Boolean = true,
  val write: Boolean = false,
  val token: String? = null,
  val publicKey: String? = null,
  val signingKey: String? = null,
) {
  override fun toString() =
    "RemoteCacheSettings(url=$url, read=$read, write=$write, token=${if (token == null) "none" else "set"})"
}

data class BuildCacheSettings(
  val local: LocalCacheSettings = LocalCacheSettings(),
  val remote: RemoteCacheSettings? = null,
)

@DslMarker annotation class BuildCacheDsl

@BuildCacheDsl
class LocalCacheScope internal constructor() {
  var enabled: Boolean = true
  var maxEntries: Int = LocalCacheSettings.DEFAULT_MAX_ENTRIES

  internal fun build(): LocalCacheSettings {
    if (maxEntries < 1) {
      throw ProjectConfigurationException(
        "buildCache.local.maxEntries is $maxEntries and has to be at least 1; " +
          "enabled = false turns the local cache off"
      )
    }
    return LocalCacheSettings(enabled, maxEntries)
  }
}

@BuildCacheDsl
class RemoteCacheScope internal constructor() {
  var url: String? = null
  var read: Boolean = true
  var write: Boolean = false

  /** Sent as a bearer token. An empty one, such as a variable that is not set, counts as none. */
  var token: String? = null

  /** Results are used only with a signature this key confirms; `tazel cache keygen` makes one. */
  var publicKey: String? = null

  /** Signs what this machine writes. A secret, like [token]. */
  var signingKey: String? = null

  internal fun build(): RemoteCacheSettings {
    val address =
      requireCacheUrl(
        url ?: throw ProjectConfigurationException("buildCache.remote.url is required")
      )
    val secret = token?.takeIf(String::isNotBlank)
    // The value is not part of the message: it is a secret.
    if (secret != null && secret.any(Char::isISOControl)) {
      throw ProjectConfigurationException("buildCache.remote.token holds a control character")
    }
    val confirming = publicKey?.takeIf(String::isNotBlank)
    val signing = signingKey?.takeIf(String::isNotBlank)
    confirming?.let(ResultKeys::requirePublic)
    signing?.let(ResultKeys::requireSigning)
    if (write && confirming != null && signing == null) {
      throw ProjectConfigurationException(
        "buildCache.remote.write needs signingKey when publicKey is set: readers would refuse " +
          "what this machine writes"
      )
    }
    return RemoteCacheSettings(address, read, write, secret, confirming, signing)
  }
}

@BuildCacheDsl
class BuildCacheScope internal constructor() {
  private var local: LocalCacheSettings? = null
  private var remote: RemoteCacheSettings? = null

  fun local(block: LocalCacheScope.() -> Unit) {
    if (local != null) {
      throw ProjectConfigurationException("buildCache.local may be configured only once")
    }
    local = LocalCacheScope().apply(block).build()
  }

  fun remote(block: RemoteCacheScope.() -> Unit) {
    if (remote != null) {
      throw ProjectConfigurationException("buildCache.remote may be configured only once")
    }
    remote = RemoteCacheScope().apply(block).build()
  }

  internal fun build() = BuildCacheSettings(local ?: LocalCacheSettings(), remote)
}

/**
 * The address of a remote cache without a final `/`. It takes https, because the cache hands out
 * programs and receives a token; plain http only reaches the machine Tazel runs on.
 */
internal fun requireCacheUrl(value: String): String {
  val text = value.trim().trimEnd('/')
  val address =
    try {
      URI(text)
    } catch (_: URISyntaxException) {
      throw ProjectConfigurationException("buildCache.remote.url is not an address: $value")
    }
  val host = address.host?.lowercase()
  if (host == null || address.rawUserInfo != null) {
    throw ProjectConfigurationException(
      "buildCache.remote.url needs a host and no user name: $value"
    )
  }
  if (address.rawQuery != null || address.rawFragment != null) {
    throw ProjectConfigurationException("buildCache.remote.url takes no query or fragment: $value")
  }
  val local = host == "localhost" || host == "127.0.0.1" || host == "[::1]"
  when (address.scheme?.lowercase()) {
    "https" -> Unit
    "http" ->
      if (!local) {
        throw ProjectConfigurationException(
          "buildCache.remote.url has to use https; http is for this machine only: $value"
        )
      }
    else -> throw ProjectConfigurationException("buildCache.remote.url has to use https: $value")
  }
  return text
}

/** The Ed25519 keys of signed results, written in Base64. */
object ResultKeys {
  private const val ALGORITHM = "Ed25519"

  private fun decoded(
    text: String,
    field: String,
  ): ByteArray =
    try {
      Base64.getDecoder().decode(text.trim())
    } catch (_: IllegalArgumentException) {
      // The value is not part of the message: it may be a secret.
      throw ProjectConfigurationException("buildCache.remote.$field is not Base64")
    }

  fun publicOf(text: String): PublicKey =
    KeyFactory.getInstance(ALGORITHM).generatePublic(X509EncodedKeySpec(decoded(text, "publicKey")))

  fun signingOf(text: String): PrivateKey =
    KeyFactory.getInstance(ALGORITHM)
      .generatePrivate(PKCS8EncodedKeySpec(decoded(text, "signingKey")))

  internal fun requirePublic(text: String) {
    try {
      publicOf(text)
    } catch (_: GeneralSecurityException) {
      throw ProjectConfigurationException("buildCache.remote.publicKey is not an Ed25519 key")
    }
  }

  internal fun requireSigning(text: String) {
    try {
      signingOf(text)
    } catch (_: GeneralSecurityException) {
      throw ProjectConfigurationException("buildCache.remote.signingKey is not an Ed25519 key")
    }
  }

  /** A new pair: the public key, then the signing key. */
  fun generate(): Pair<String, String> {
    val pair = KeyPairGenerator.getInstance(ALGORITHM).generateKeyPair()
    val encoder = Base64.getEncoder()
    return encoder.encodeToString(pair.public.encoded) to
      encoder.encodeToString(pair.private.encoded)
  }
}

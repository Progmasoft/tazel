// SPDX-FileCopyrightText: 2026 Progmasoft <support@progmasoft.com>
// SPDX-License-Identifier: MIT

using System;
using System.Security.Cryptography;
using Org.BouncyCastle.Crypto.Generators;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Crypto.Signers;
using Org.BouncyCastle.Security;

namespace Progmasoft.Tazel.Cache;

/// <summary>The algorithms that sign the results of a remote cache.</summary>
internal enum SigningAlgorithm
{
    /// <summary>Ed25519 (RFC 8032).</summary>
    Ed25519,

    /// <summary>RSA with a 4096-bit modulus, RSA-PSS with SHA-256.</summary>
    Rsa4096,

    /// <summary>ML-DSA-65 (FIPS 204).</summary>
    MlDsa65,
}

/// <summary>
/// A key of a remote cache, written as <c>algorithm:base64</c>: <c>ed25519:</c>, <c>rsa-4096:</c> or <c>ml-dsa-65:</c>.
/// </summary>
/// <remarks>
/// A public key is the raw 32 bytes for Ed25519, the SubjectPublicKeyInfo for RSA and the encoded key for ML-DSA-65; a
/// signing key is the 32-byte seed for Ed25519 and ML-DSA-65 and the PKCS #8 key for RSA. Ed25519 and ML-DSA come
/// from BouncyCastle, the same on every host; RSA from the base class library.
/// </remarks>
internal sealed class CacheKey
{
    private CacheKey(SigningAlgorithm algorithm, byte[] bytes, bool isSigning)
    {
        Algorithm = algorithm;
        Bytes = bytes;
        IsSigning = isSigning;
    }

    public SigningAlgorithm Algorithm { get; }

    public bool IsSigning { get; }

    private byte[] Bytes { get; }

    /// <summary>The name of <paramref name="algorithm"/> as keys and signatures write it.</summary>
    public static string NameOf(SigningAlgorithm algorithm) => algorithm switch
    {
        SigningAlgorithm.Ed25519 => "ed25519",
        SigningAlgorithm.Rsa4096 => "rsa-4096",
        _ => "ml-dsa-65",
    };

    /// <summary>The algorithm named <paramref name="name"/>, or null.</summary>
    public static SigningAlgorithm? AlgorithmNamed(string name) => name switch
    {
        "ed25519" => SigningAlgorithm.Ed25519,
        "rsa-4096" => SigningAlgorithm.Rsa4096,
        "ml-dsa-65" => SigningAlgorithm.MlDsa65,
        _ => null,
    };

    /// <summary>Reads a public key; <paramref name="what"/> names it in an error.</summary>
    public static CacheKey ParsePublic(string text, string what) => Parse(text, what, isSigning: false);

    /// <summary>Reads a signing key; <paramref name="what"/> names it in an error.</summary>
    public static CacheKey ParseSigning(string text, string what) => Parse(text, what, isSigning: true);

    /// <summary>A new key pair of <paramref name="algorithm"/>, as text.</summary>
    public static (string PublicKey, string SigningKey) Generate(SigningAlgorithm algorithm)
    {
        var name = NameOf(algorithm) + ":";
        switch (algorithm)
        {
            case SigningAlgorithm.Ed25519:
            {
                var signing = new Ed25519PrivateKeyParameters(new SecureRandom());
                return (name + Convert.ToBase64String(signing.GeneratePublicKey().GetEncoded()),
                    name + Convert.ToBase64String(signing.GetEncoded()));
            }
            case SigningAlgorithm.Rsa4096:
            {
                using var rsa = RSA.Create(4096);
                return (name + Convert.ToBase64String(rsa.ExportSubjectPublicKeyInfo()),
                    name + Convert.ToBase64String(rsa.ExportPkcs8PrivateKey()));
            }
            default:
            {
                var generator = new MLDsaKeyPairGenerator();
                generator.Init(new MLDsaKeyGenerationParameters(new SecureRandom(), MLDsaParameters.ml_dsa_65));
                var pair = generator.GenerateKeyPair();
                return (name + Convert.ToBase64String(((MLDsaPublicKeyParameters)pair.Public).GetEncoded()),
                    name + Convert.ToBase64String(((MLDsaPrivateKeyParameters)pair.Private).GetSeed()));
            }
        }
    }

    /// <summary>The signature of <paramref name="message"/>; this must be a signing key.</summary>
    public byte[] Sign(byte[] message)
    {
        ArgumentNullException.ThrowIfNull(message);
        if (!IsSigning)
        {
            throw new InvalidOperationException("a public key does not sign");
        }

        switch (Algorithm)
        {
            case SigningAlgorithm.Ed25519:
            {
                var signer = new Ed25519Signer();
                signer.Init(true, new Ed25519PrivateKeyParameters(Bytes));
                signer.BlockUpdate(message, 0, message.Length);
                return signer.GenerateSignature();
            }
            case SigningAlgorithm.Rsa4096:
            {
                using var rsa = RSA.Create();
                rsa.ImportPkcs8PrivateKey(Bytes, out _);
                return rsa.SignData(message, HashAlgorithmName.SHA256, RSASignaturePadding.Pss);
            }
            default:
            {
                var signer = new MLDsaSigner(MLDsaParameters.ml_dsa_65, deterministic: false);
                signer.Init(true, MLDsaPrivateKeyParameters.FromSeed(MLDsaParameters.ml_dsa_65, Bytes));
                signer.BlockUpdate(message, 0, message.Length);
                return signer.GenerateSignature();
            }
        }
    }

    /// <summary>Whether <paramref name="signature"/> is this public key's signature of <paramref name="message"/>.</summary>
    public bool Verify(byte[] message, byte[] signature)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentNullException.ThrowIfNull(signature);
        try
        {
            return Algorithm switch
            {
                SigningAlgorithm.Ed25519 => VerifyEd25519(message, signature),
                SigningAlgorithm.Rsa4096 => VerifyRsa(message, signature),
                _ => VerifyMlDsa(message, signature),
            };
        }
        catch (CryptographicException)
        {
            return false;
        }
    }

    private bool VerifyEd25519(byte[] message, byte[] signature)
    {
        var verifier = new Ed25519Signer();
        verifier.Init(false, new Ed25519PublicKeyParameters(Bytes));
        verifier.BlockUpdate(message, 0, message.Length);
        return verifier.VerifySignature(signature);
    }

    private bool VerifyRsa(byte[] message, byte[] signature)
    {
        using var rsa = RSA.Create();
        rsa.ImportSubjectPublicKeyInfo(Bytes, out _);
        return rsa.VerifyData(message, signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pss);
    }

    private bool VerifyMlDsa(byte[] message, byte[] signature)
    {
        var verifier = new MLDsaSigner(MLDsaParameters.ml_dsa_65, deterministic: false);
        verifier.Init(false, MLDsaPublicKeyParameters.FromEncoding(MLDsaParameters.ml_dsa_65, Bytes));
        verifier.BlockUpdate(message, 0, message.Length);
        return verifier.VerifySignature(signature);
    }

    private static CacheKey Parse(string text, string what, bool isSigning)
    {
        ArgumentNullException.ThrowIfNull(text);
        var colon = text.IndexOf(':', StringComparison.Ordinal);
        if (colon < 0 || AlgorithmNamed(text[..colon]) is not { } algorithm)
        {
            throw new FormatException($"{what} does not start with ed25519:, rsa-4096: or ml-dsa-65:");
        }

        byte[] bytes;
        try
        {
            bytes = Convert.FromBase64String(text[(colon + 1)..].Trim());
        }
        catch (FormatException)
        {
            throw new FormatException($"{what} is not base64 after {text[..colon]}:");
        }

        var key = new CacheKey(algorithm, bytes, isSigning);
        if (!key.HasItsLength())
        {
            throw new FormatException($"{what} is not a {(isSigning ? "signing" : "public")} key of {text[..colon]}");
        }

        return key;
    }

    /// <summary>Whether the bytes can be a key of this kind: the sizes of Ed25519 and ML-DSA-65, and a 4096-bit RSA key.</summary>
    private bool HasItsLength()
    {
        try
        {
            switch (Algorithm)
            {
                case SigningAlgorithm.Ed25519:
                    return Bytes.Length == 32;
                case SigningAlgorithm.MlDsa65:
                    return IsSigning ? Bytes.Length == 32 : Bytes.Length == 1952;
                default:
                {
                    using var rsa = RSA.Create();
                    if (IsSigning)
                    {
                        rsa.ImportPkcs8PrivateKey(Bytes, out _);
                    }
                    else
                    {
                        rsa.ImportSubjectPublicKeyInfo(Bytes, out _);
                    }

                    return rsa.KeySize == 4096;
                }
            }
        }
        catch (CryptographicException)
        {
            return false;
        }
    }
}

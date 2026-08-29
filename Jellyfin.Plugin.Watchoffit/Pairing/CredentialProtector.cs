using System.Text;

#if WINDOWS
using System.Security.Cryptography;
#endif

namespace Jellyfin.Plugin.Watchoffit.Pairing;

/// <summary>
/// Pluggable credential protection. The interface exists so Phase 4 or
/// later can swap the Phase 3 plain-text implementation for a real
/// keyring/DPAPI implementation without touching <see cref="WatchoffitConnectionStore"/>.
/// </summary>
public interface ICredentialProtector
{
    /// <summary>Wire literal for the <c>credential.scheme</c> field. Must match one of the documented values.</summary>
    string Scheme { get; }

    /// <summary>Wrap the credential value for at-rest storage.</summary>
    /// <param name="value">Raw credential value to protect.</param>
    /// <returns>Protected (encoded) value safe to write to disk.</returns>
    string Protect(string value);

    /// <summary>Reverse a previously <see cref="Protect"/>ed value. Throws on a corrupted or wrong-scheme envelope.</summary>
    /// <param name="value">Protected value as read from disk.</param>
    /// <returns>Raw credential value.</returns>
    string Unprotect(string value);
}

/// <summary>
/// Phase 3 Linux default protector. Stores the credential as-is; the
/// store applies restrictive file permissions (0o600) so a co-tenant
/// on the same host cannot read the file.
/// </summary>
/// <remarks>
/// See <c>docs/pairing-design.md</c> §11.2 for
/// the design trade-off (keyring dependency inside containers was the
/// reason plain text was chosen for Phase 3).
/// </remarks>
public sealed class PlainCredentialProtector : ICredentialProtector
{
    /// <inheritdoc />
    public string Scheme => "plain";

    /// <inheritdoc />
    public string Protect(string value) => value;

    /// <inheritdoc />
    public string Unprotect(string value) => value;
}

/// <summary>
/// Windows DPAPI protector. Encrypts the credential with
/// <c>ProtectedData.Protect(bytes, entropy, DataProtectionScope.CurrentUser)</c>
/// so the file is unreadable to other Windows users on the same host.
/// </summary>
/// <remarks>
/// The protected bytes are base64-encoded for inclusion in the
/// <c>connection.json</c> JSON document. The on-disk scheme is
/// <c>"dpapi"</c>; the store refuses to load files whose scheme does
/// not match the active protector. <see cref="DPAPICredentialProtector"/>
/// is only constructed on Windows; <see cref="CredentialProtectorFactory.CreateForCurrentPlatform"/>
/// returns <see cref="PlainCredentialProtector"/> elsewhere.
/// </remarks>
public sealed class DPAPICredentialProtector : ICredentialProtector
{
#if WINDOWS
    /// <summary>Fixed entropy appended to every DPAPI call. Not a secret — its purpose is to bind the protected bytes to this plugin so a stray ProtectedData user can't decrypt by accident.</summary>
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("Jellyfin.Plugin.Watchoffit.ConnectionStore.v1");
#endif

    /// <inheritdoc />
    public string Scheme => "dpapi";

    /// <inheritdoc />
    public string Protect(string value)
    {
#if WINDOWS
        var bytes = ProtectedData.Protect(Encoding.UTF8.GetBytes(value), Entropy, DataProtectionScope.CurrentUser);
        return Convert.ToBase64String(bytes);
#else
        throw new PlatformNotSupportedException(
            "DPAPICredentialProtector is only usable on Windows. " +
            "Use CredentialProtectorFactory.CreateForCurrentPlatform() to pick the right protector per platform.");
#endif
    }

    /// <inheritdoc />
    public string Unprotect(string value)
    {
#if WINDOWS
        var bytes = Convert.FromBase64String(value);
        var plain = ProtectedData.Unprotect(bytes, Entropy, DataProtectionScope.CurrentUser);
        return Encoding.UTF8.GetString(plain);
#else
        throw new PlatformNotSupportedException(
            "DPAPICredentialProtector is only usable on Windows.");
#endif
    }
}

/// <summary>Factory that returns the right <see cref="ICredentialProtector"/> for the current platform.</summary>
public static class CredentialProtectorFactory
{
    /// <summary>Returns the platform-correct protector: DPAPI on Windows, plain text elsewhere.</summary>
    /// <returns>The active <see cref="ICredentialProtector"/> for this host.</returns>
    public static ICredentialProtector CreateForCurrentPlatform()
    {
        if (OperatingSystem.IsWindows())
        {
            return new DPAPICredentialProtector();
        }

        return new PlainCredentialProtector();
    }
}

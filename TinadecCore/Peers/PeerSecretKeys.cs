using TinadecCore.Persistence;

namespace TinadecCore.Peers;

/// <summary>
/// Canonical <see cref="ISecretStore"/> references for peer device secrets.
/// Keys strictly adhere to <see cref="SecretReferences.Validate"/>: ASCII letters, digits,
/// '-', '_', and '.' only (no colons ':' and no '..').
/// </summary>
public static class PeerSecretKeys
{
    public const string LocalCertPem = "peer.local_identity.cert_pem";
    public const string LocalPrivateKeyPem = "peer.local_identity.private_key_pem";
    public const string ActivePairingCode = "peer.pairing_code.active";

    public static void ValidateAll()
    {
        SecretReferences.Validate(LocalCertPem);
        SecretReferences.Validate(LocalPrivateKeyPem);
        SecretReferences.Validate(ActivePairingCode);
    }
}

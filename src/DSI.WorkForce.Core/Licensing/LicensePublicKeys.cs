namespace DSI.WorkForce.Core.Licensing;

/// <summary>
/// Public keys whose signatures the product accepts. Only PUBLIC keys live here: they can verify a license but cannot
/// create one. The matching private key stays with Helao2 (license generator).
/// </summary>
public static class LicensePublicKeys
{
    /// <summary>
    /// Helao2 production key. Set once with:  DSI.WorkForce.LicenseGenerator keygen --update-source
    /// (creates the private key file - keep it secret and backed up - and writes the public key here).
    /// </summary>
    public const string Production = "";

#if DEBUG
    /// <summary>
    /// Development key for testing (private half: tools/DSI.WorkForce.LicenseGenerator/dev-keys/dev-private-key.pem).
    /// Trusted by DEBUG builds only, so licenses made with the dev key never work in a Release build.
    /// </summary>
    public const string Development = "MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAEeGLN+BbbM7LSp0rLJevolxEtwAW1x7sBdQYfwqa9VLKgxT3gMvC3YyWg3KEHK2naBuGYqv3CfDSxU0Giqzb59Q==";
#endif

    public static IReadOnlyList<string> Trusted
    {
        get
        {
            var keys = new List<string>();
            if (Production.Length > 0) keys.Add(Production);
#if DEBUG
            keys.Add(Development);
#endif
            return keys;
        }
    }
}

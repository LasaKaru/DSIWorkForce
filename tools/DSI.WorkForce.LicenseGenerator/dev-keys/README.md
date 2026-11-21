# Development signing key (NOT for customers)

`dev-private-key.pem` signs **test** licenses. The product trusts it **only in DEBUG builds**
(`LicensePublicKeys.Development` is compiled with `#if DEBUG`), so keys made with it never activate a Release build.

For real customers create your own production key once:

```powershell
dotnet run --project tools/DSI.WorkForce.LicenseGenerator -- keygen --update-source
```

This writes `helao2-license-private.pem` (keep it secret, back it up, never commit it) and puts the public key
into `src/DSI.WorkForce.Core/Licensing/LicensePublicKeys.cs`. Rebuild and ship the product after that.

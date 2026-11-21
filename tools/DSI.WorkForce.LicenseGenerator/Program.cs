using System.Globalization;
using System.Text;
using DSI.WorkForce.Core.Licensing;

// Helao2 license generator for DSI WorkForce.
//
//   Helao2.LicenseGenerator                                  interactive (asks the questions)
//   Helao2.LicenseGenerator generate --customer "ABC Tyres" --type Trial --days 30 [--email x] [--installation ID]
//                                    [--max-users 10] [--notes "..."] [--key helao2-license-private.pem]
//   Helao2.LicenseGenerator verify <key>                     show what is inside a key and whether it is valid
//   Helao2.LicenseGenerator keygen [--update-source]         create the PRODUCTION signing key (once)

var cmd = args.Length == 0 ? "interactive" : args[0].ToLowerInvariant();
try
{
    return cmd switch
    {
        "interactive" => Interactive(),
        "generate" => Generate(Opt("--customer") ?? throw new ArgumentException("--customer is required"),
            Enum.Parse<LicenseType>(Opt("--type") ?? "Trial", ignoreCase: true), int.TryParse(Opt("--days"), out var d) ? d : 30,
            Opt("--email"), Opt("--installation"), int.TryParse(Opt("--max-users"), out var mu) ? mu : null, Opt("--notes"), Opt("--key")),
        "verify" => Verify(args.Length > 1 ? string.Join("", args.Skip(1)) : Console.In.ReadToEnd()),
        "keygen" => KeyGen(Opt("--out") ?? Directory.GetCurrentDirectory(), args.Contains("--update-source")),
        _ => Usage(),
    };
}
catch (Exception ex) when (ex is ArgumentException or FormatException or FileNotFoundException or InvalidOperationException)
{
    Console.ForegroundColor = ConsoleColor.Red;
    Console.Error.WriteLine(ex.Message);
    Console.ResetColor();
    return 1;
}

string? Opt(string name) { var i = Array.IndexOf(args, name); return i >= 0 && i + 1 < args.Length ? args[i + 1] : null; }

int Usage()
{
    Console.WriteLine("Usage: Helao2.LicenseGenerator [generate|verify|keygen] ...  (no arguments = interactive)");
    return 2;
}

int Interactive()
{
    Header();
    var customer = Ask("Licensed to (company name)", required: true)!;
    var email = Ask("Customer e-mail (optional)");
    Console.WriteLine("License type:  1 = Trial   2 = Subscription (paid for a period)   3 = Perpetual (one-time, never expires)");
    var type = Ask("Choose 1, 2 or 3", "1") switch { "2" => LicenseType.Subscription, "3" => LicenseType.Perpetual, _ => LicenseType.Trial };
    var days = type == LicenseType.Perpetual ? 0 : int.Parse(Ask("Valid for how many days", type == LicenseType.Trial ? "30" : "365")!, CultureInfo.InvariantCulture);
    var installation = Ask("Installation ID from the customer's activation page (optional - empty = any installation)");
    var maxUsersText = Ask("Maximum active users (optional - empty = unlimited)");
    var notes = Ask("Notes (optional, e.g. invoice number)");
    return Generate(customer, type, days, email, installation, string.IsNullOrWhiteSpace(maxUsersText) ? null : int.Parse(maxUsersText), notes, null);
}

int Generate(string customer, LicenseType type, int days, string? email, string? installation, int? maxUsers, string? notes, string? keyPath)
{
    var (pem, keyFile, isDev) = LoadPrivateKey(keyPath);
    var now = DateTime.UtcNow;
    var license = new LicenseInfo
    {
        LicensedTo = customer.Trim(),
        Email = Blank(email),
        Type = type,
        IssuedUtc = now,
        // Expires at the end of the last day (23:59:59 UTC).
        ExpiresUtc = type == LicenseType.Perpetual ? null : now.Date.AddDays(Math.Max(1, days) + 1).AddSeconds(-1),
        InstallationId = Blank(installation)?.ToUpperInvariant(),
        MaxUsers = maxUsers,
        Notes = Blank(notes),
    };
    var key = LicenseCodec.Sign(license, pem);

    // Self-check with the matching public key.
    var check = LicenseCodec.Verify(key, [LicenseCodec.PublicKeyFromPrivate(pem)], license.InstallationId, now);
    if (!check.IsValid) throw new InvalidOperationException("Self-check failed: " + check.Message);

    Console.WriteLine();
    Console.ForegroundColor = ConsoleColor.Green;
    Console.WriteLine("License key (send the whole text to the customer):");
    Console.ResetColor();
    Console.WriteLine();
    Console.WriteLine(key);
    Console.WriteLine();
    Console.WriteLine(check.Message);
    if (isDev)
    {
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine("NOTE: signed with the DEVELOPMENT key - works only with DEBUG builds of DSI WorkForce (testing).");
        Console.WriteLine("      For customers run 'keygen' once and use the production key.");
        Console.ResetColor();
    }

    // Keep a copy + register of issued licenses next to the private key.
    var dir = Path.Combine(Path.GetDirectoryName(keyFile)!, "issued-licenses");
    Directory.CreateDirectory(dir);
    var safe = string.Concat(customer.Where(char.IsLetterOrDigit));
    var file = Path.Combine(dir, $"{now:yyyyMMdd-HHmm}_{safe}_{type}.txt");
    File.WriteAllText(file, $"""
        DSI WorkForce license - {LicenseService_Vendor}
        Licensed to : {license.LicensedTo}
        Type        : {license.Type}
        Issued      : {license.IssuedUtc:yyyy-MM-dd} (UTC)
        Expires     : {(license.ExpiresUtc is { } e ? e.ToString("yyyy-MM-dd") : "never")}
        Installation: {license.InstallationId ?? "any"}
        Max users   : {license.MaxUsers?.ToString() ?? "unlimited"}
        License ID  : {license.LicenseId}

        How to activate: sign in as administrator > License > paste the key below > Validate & activate.
        Support: support@helao2.com

        {key}
        """);
    var register = Path.Combine(dir, "register.csv");
    if (!File.Exists(register)) File.WriteAllText(register, "IssuedUtc,LicenseId,LicensedTo,Type,ExpiresUtc,InstallationId,MaxUsers,DevKey,Notes\n");
    File.AppendAllText(register, string.Join(",", new[]
    {
        now.ToString("O"), license.LicenseId.ToString(), Csv(license.LicensedTo), license.Type.ToString(),
        license.ExpiresUtc?.ToString("O") ?? "", license.InstallationId ?? "", license.MaxUsers?.ToString() ?? "", isDev.ToString(), Csv(license.Notes ?? ""),
    }) + "\n");
    Console.WriteLine($"Saved: {file}");
    return 0;
}

int Verify(string key)
{
    var keys = new List<string>(LicensePublicKeys.Trusted);
    var devPem = Path.Combine(AppContext.BaseDirectory, "dev-keys", "dev-private-key.pem");
    if (File.Exists(devPem)) keys.Add(LicenseCodec.PublicKeyFromPrivate(File.ReadAllText(devPem)));
    var check = LicenseCodec.Verify(key, keys, installationId: null, DateTime.UtcNow);
    if (check.Status == LicenseStatus.WrongInstallation) check = check with { Message = "Valid signature; bound to installation " + check.License!.InstallationId };
    Console.WriteLine($"{check.Status}: {check.Message}");
    if (check.License is { } l)
        Console.WriteLine($"  Licensed to {l.LicensedTo} | {l.Type} | issued {l.IssuedUtc:yyyy-MM-dd} | expires {(l.ExpiresUtc?.ToString("yyyy-MM-dd") ?? "never")} | id {l.LicenseId}");
    return check.Status is LicenseStatus.Valid or LicenseStatus.WrongInstallation ? 0 : 1;
}

int KeyGen(string outDir, bool updateSource)
{
    var file = Path.Combine(outDir, "helao2-license-private.pem");
    if (File.Exists(file)) throw new InvalidOperationException($"{file} already exists. Keep using it - a new key would invalidate every license already issued.");
    var (privatePem, publicKey) = LicenseCodec.CreateKeyPair();
    File.WriteAllText(file, privatePem);
    Console.ForegroundColor = ConsoleColor.Yellow;
    Console.WriteLine($"PRIVATE key written to {file}");
    Console.WriteLine("Keep it secret, back it up (e.g. password manager / offline USB). Never commit it, never send it to customers.");
    Console.ResetColor();
    Console.WriteLine();
    Console.WriteLine("Public key (goes into the product):");
    Console.WriteLine(publicKey);

    if (updateSource)
    {
        var src = FindUp("src/DSI.WorkForce.Core/Licensing/LicensePublicKeys.cs");
        var text = File.ReadAllText(src);
        const string marker = "public const string Production = \"";
        var start = text.IndexOf(marker, StringComparison.Ordinal) + marker.Length;
        var end = text.IndexOf('"', start);
        File.WriteAllText(src, text[..start] + publicKey + text[end..]);
        Console.WriteLine($"Updated {src}. Rebuild and redeploy DSI WorkForce.");
    }
    else Console.WriteLine("Paste it into LicensePublicKeys.Production (src/DSI.WorkForce.Core/Licensing/LicensePublicKeys.cs) and rebuild.");
    return 0;
}

(string Pem, string File, bool IsDev) LoadPrivateKey(string? path)
{
    var candidates = new[]
    {
        path, Environment.GetEnvironmentVariable("HELAO2_LICENSE_KEY"),
        Path.Combine(Directory.GetCurrentDirectory(), "helao2-license-private.pem"),
        Path.Combine(AppContext.BaseDirectory, "helao2-license-private.pem"),
    };
    foreach (var c in candidates.Where(c => !string.IsNullOrWhiteSpace(c)))
        if (File.Exists(c)) return (File.ReadAllText(c!), Path.GetFullPath(c!), false);
    if (path is not null) throw new FileNotFoundException($"Private key '{path}' not found.");

    var dev = Path.Combine(AppContext.BaseDirectory, "dev-keys", "dev-private-key.pem");
    if (!File.Exists(dev)) throw new FileNotFoundException("No private key found. Run 'keygen' first or pass --key.");
    return (File.ReadAllText(dev), Path.GetFullPath(dev), true);
}

string FindUp(string relative)
{
    for (var dir = new DirectoryInfo(Directory.GetCurrentDirectory()); dir is not null; dir = dir.Parent)
        if (File.Exists(Path.Combine(dir.FullName, relative))) return Path.Combine(dir.FullName, relative);
    throw new FileNotFoundException($"Run from inside the DSIWorkForce repository ({relative} not found).");
}

string? Ask(string question, string? defaultValue = null, bool required = false)
{
    while (true)
    {
        Console.Write(defaultValue is null ? $"{question}: " : $"{question} [{defaultValue}]: ");
        var answer = Console.ReadLine()?.Trim();
        if (string.IsNullOrEmpty(answer)) answer = defaultValue;
        if (!required || !string.IsNullOrWhiteSpace(answer)) return answer;
    }
}

void Header()
{
    Console.ForegroundColor = ConsoleColor.Cyan;
    Console.WriteLine("Helao2 (Pvt) Ltd - DSI WorkForce license generator");
    Console.ResetColor();
    Console.WriteLine(new string('-', 52));
}

static string? Blank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
static string Csv(string s) => s.Contains(',') || s.Contains('"') ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;

partial class Program
{
    private const string LicenseService_Vendor = "Helao2 (Pvt) Ltd";
}

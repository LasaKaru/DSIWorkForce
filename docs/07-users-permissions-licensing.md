# 7. Users, permissions & licensing

## Signing in

DSI WorkForce has its own user accounts (table `dsi.AppUser`). Passwords are stored only as salted PBKDF2-SHA256 hashes. A password needs at least 6 characters and may not be the user name.

* **First start:** the database setup creates the administrator
  **user `admin`, password `Admin@123`**. You must choose a new password at the first sign-in.
* 5 wrong passwords in a row lock the account for 15 minutes.
* Sessions last one shift (10 hours, sliding) and end **within one minute** when an administrator disables the user,
  changes their permissions or resets their password.
* The WPF desktop app uses the same accounts (sign-in window at start-up).

## Permissions (Administration › Users & permissions)

Tick any combination per user:

| Permission | Allows |
|---|---|
| **Administrator** | Everything, including permissions added in future versions. |
| Scan / upload | Web *Upload scans* page, WPF *Scan station*. |
| Review & verify | Review queue, correct values, verify / reject / re-read sheets. |
| Reports | Report pages and Excel export (web + WPF). |
| Master data | Employees, items, presses / machines, shifts. |
| Report columns | Change report layouts and rebuild report views. |
| User management | Create users, reset passwords, set permissions. |
| License | Activate / renew the product license. |

Typical set-ups: a *scan operator* has only **Scan**, a *clerk* has **Scan + Review**, a *supervisor* has
**Review + Reports**, a *manager* has **Reports**, *IT* has **Administrator**.
Every user can see the dashboard. Menus show only what the user may use, and the server checks every page and API call
(hiding a menu is not the protection).

The system refuses to remove the last active administrator.

## Master data (Administration › Master data)

Employees (EPF No, name, section), items (code, description as in the ERP, standard rate per hour), presses / machines
and shifts (code exactly as written on the sheet, start and end time). Records are never deleted: set them **inactive**
and they stay in history but are no longer matched by the OCR. The Worker reloads master data every 5 minutes.

## Settings (Administration › Settings, Administrator only)

| Option | Effect |
|---|---|
| **Show Helao2 branding** | On (default): Helao2 logos in the sidebar ("Powered by"), page footer and activation page, plus the "© 2026 Helao2 (Pvt) Ltd" line, on the web and in the WPF app. Off: neutral "© 2026 DSI WorkForce". The support e-mail stays on the activation page so a license can still be renewed. |
| **Show the OCR engine panel** | Adds a panel to the License page: engine logo, **engine name**, an *Activated* / *Not activated* badge (follows the license), the tagline, the recognition models in use and every Worker that has loaded them. |
| **Engine name / Tagline** | Free text shown on that panel (defaults: "Helao2 Vision OCR" / "AI handwriting recognition for the automation industry"). |

Settings are stored in `dsi.AppSetting` (`Ui.Branding`, JSON) and reach all users within 30 seconds. Each Worker writes
`Ocr.Engine.<machine>` once its OCR models are loaded; the Settings page lists them under *Recognition workers*.

The engine panel describes the engines this product really uses (PaddleOCR PP-OCRv5 + CNN digit model, both local).
Don't name third-party products the system doesn't use (for example a commercial OCR SDK) on this panel.

## Licensing

The product works only with a valid license key from **Helao2 (Pvt) Ltd**. Without one, or after it expires,
**every page and API call is locked** and users are sent to the activation page (logo, status, installation ID,
contact **support@helao2.com**, key box and *Validate & activate*). The Worker also pauses: scans wait in the hot
folder and are processed once a valid key is entered.

### How the protection works

* A key is the license details (customer, type, expiry, optional installation ID / user limit) **signed with Helao2's
  private ECDSA P-256 key**. The product contains only the **public** key. It can check a key but can never create one.
  Changing even one character (e.g. the expiry date) makes the key invalid.
* The key is checked every minute while the apps run, so expiry takes effect without a restart.
* Turning the PC clock back is detected (the product remembers the latest time it has seen).
* Optional **installation binding**: the activation page shows an *Installation ID*. If Helao2 puts it into the key,
  the key works only on that installation.
* Optional **user limit**: maximum number of active user accounts.
* An expiry warning appears 14 days before the end, for everyone.

### License types

| Type | Meaning |
|---|---|
| Trial | Evaluation for N days (e.g. 14 or 30). |
| Subscription | Paid period (e.g. 365 days), renewed with a new key. |
| Perpetual | One-time purchase, never expires. |

### Activating (customer)

1. Sign in as a user with the **License** permission (e.g. `admin`).
2. *Administration › License* (or the page you are redirected to).
3. Paste the key you received (line breaks don't matter) and click **Validate & activate**.

## License generator (Helao2 only, never give it to customers)

`tools/DSI.WorkForce.LicenseGenerator` builds `Helao2.LicenseGenerator.exe`.

### One-time set-up: the production signing key

```powershell
cd D:\visualstudio2022\repos\DSIWorkForce
dotnet run --project tools/DSI.WorkForce.LicenseGenerator -- keygen --update-source
```

* Creates **`helao2-license-private.pem`**. This file *is* your ability to sell licenses: keep it secret, back it up
  (password manager / offline USB), never commit it (it is in `.gitignore`), never e-mail it.
* Writes the matching public key into `src/DSI.WorkForce.Core/Licensing/LicensePublicKeys.cs`.
  Commit that change, then build the product in **Release** and deploy it.
* Never run `keygen` again for the same product: a new key pair invalidates every license already issued.

### Creating a key

Interactive (asks the questions):

```powershell
dotnet run --project tools/DSI.WorkForce.LicenseGenerator
```

Or with options (for scripts):

```powershell
dotnet run --project tools/DSI.WorkForce.LicenseGenerator -- generate --customer "Samson Rubber Industries" --type Trial --days 30
dotnet run --project tools/DSI.WorkForce.LicenseGenerator -- generate --customer "Samson Rubber Industries" --type Subscription --days 365 --max-users 25 --installation 7F3A...
dotnet run --project tools/DSI.WorkForce.LicenseGenerator -- generate --customer "Samson Rubber Industries" --type Perpetual --notes "INV-2026-041"
dotnet run --project tools/DSI.WorkForce.LicenseGenerator -- verify HLO2-....
```

The generator looks for the private key in `--key`, the `HELAO2_LICENSE_KEY` environment variable, or
`helao2-license-private.pem` in the current folder. Each key is also saved as a text file (ready to e-mail) under
`issued-licenses\`, together with `register.csv`, a list of everything you issued.

### Testing in Visual Studio (development key)

Until you run `keygen`, the generator signs with the **development key** in
`tools/DSI.WorkForce.LicenseGenerator/dev-keys/`. **Debug** builds of the product (F5 in Visual Studio) trust that key,
so you can test activation immediately. **Release** builds do **not** trust it, so test keys can never activate a
customer installation.

```powershell
dotnet run --project tools/DSI.WorkForce.LicenseGenerator -- generate --customer "Test" --type Trial --days 14
```

Copy the `HLO2-…` line, open `https://localhost:7082/license`, sign in as `admin`, paste and activate.

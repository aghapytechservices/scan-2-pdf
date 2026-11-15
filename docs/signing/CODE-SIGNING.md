# Code-signing Scan2PdfBridge.exe

The release workflow (`.github/workflows/build-bridge.yml`) signs the exe automatically once the certificate's
secrets are added to the repo. Until then, it builds unsigned and says so in the run summary.

Since June 2023, publicly trusted code-signing keys must live on secure hardware (a USB token or a cloud HSM), so a
certificate you can use from GitHub Actions must come with **cloud signing**. Pick one of the two setups below.

## Option A: SSL.com with eSigner (recommended for GitHub Actions)

1. Buy an **OV** (or EV) code-signing certificate from SSL.com with **eSigner** cloud signing, issued to
   **Aghapy Tech Services**. Complete their business validation.
2. In your SSL.com account, enroll the certificate in eSigner and set up **automated signing** to get the
   **TOTP secret**: https://www.ssl.com/how-to/automate-esigner-ev-code-signing
3. In GitHub: **aghapytechservices/scan-2-pdf → Settings → Secrets and variables → Actions → New repository secret**:

   | Secret | Value |
   |---|---|
   | `ES_USERNAME` | SSL.com account username |
   | `ES_PASSWORD` | SSL.com account password |
   | `ES_TOTP_SECRET` | eSigner TOTP secret from step 2 |
   | `ES_CREDENTIAL_ID` | eSigner credential ID (optional if you have only one certificate) |

4. Run **Actions → Build Scan Bridge (.exe) → Run workflow** with a release tag (e.g. `v1.1.1`).
   The run summary shows **"Signed by: CN=Aghapy Tech Services…"**.

## Option B: a certificate exported as a .pfx file

Only if your provider gives you an exportable `.pfx` (some private/internal CAs do; public CAs generally no longer do).

| Secret | Value |
|---|---|
| `CODE_SIGNING_PFX` | The .pfx file, base64-encoded: `[Convert]::ToBase64String([IO.File]::ReadAllBytes("cert.pfx")) \| Set-Clipboard` |
| `CODE_SIGNING_PFX_PASSWORD` | The .pfx password |

The workflow signs with `signtool` (SHA-256, RFC 3161 timestamp from DigiCert).

## Using another provider (DigiCert KeyLocker, GlobalSign, Azure Key Vault…)

Tell Claude which provider you bought from, and the workflow gets a matching signing step.

## What signing does (and doesn't) do

- Windows shows **"Aghapy Tech Services"** as the verified publisher instead of "Unknown publisher".
- SmartScreen's "Windows protected your PC" warning may still appear for a while on a **new** certificate until the
  file builds download reputation. EV certificates used to skip this; today reputation builds for both over time.
- If the workflow is configured to sign but signing fails, the release build fails rather than shipping unsigned.

# Publishing Scan 2 PDF Bridge in the Microsoft Store

The Store edition is an **MSIX package** that Microsoft signs when it publishes it. Users get it from the Store
with **no "Windows protected your PC" warning**, the publisher shows as **Aghapy Tech Services**, and updates are
automatic. No code-signing certificate is needed.

The plain `Scan2PdfBridge.exe` (GitHub Releases) keeps working for anyone not using the Store.

---

## 1. Join the Windows Store program (one-time)

You already have a Partner Center account for the Outlook add-in. Add the Store program to it:

- Go to https://storedeveloper.microsoft.com and sign up with the **same** work account, as a **Company**
  (Aghapy Tech Services), or
- in Partner Center: **Settings (gear) → Account settings → Programs → Apps and games**.

Use **Aghapy Tech Services** as the publisher display name.

## 2. Reserve the app name

Partner Center → **Apps and games** → **+ New product** → **MSIX or PWA app** → name: **Scan 2 PDF Bridge**
(if taken, try "Scan 2 PDF Bridge for Outlook").

## 3. Send Claude the package identity

Open the app → **Product management → Product identity**. Copy these three values:

| Partner Center field | Example |
|---|---|
| `Package/Identity/Name` | `12345AghapyTechServices.Scan2PDFBridge` |
| `Package/Identity/Publisher` | `CN=1A2B3C4D-5E6F-...` |
| `Package/Properties/PublisherDisplayName` | `Aghapy Tech Services` |

They go into `bridge/Package/store-identity.json`. Then run **Actions → Build Scan Bridge (.exe) → Run workflow**
with a release tag. The release gets:

- `Scan2PdfBridge-<version>-Store.msix`, which you **upload to Partner Center** (unsigned; Microsoft signs it)
- `Scan2PdfBridge-StoreTest-x64.zip`, a **test** copy you can install yourself first (step 4)

## 4. Try the Store edition on your PC (optional, recommended)

1. Unzip `Scan2PdfBridge-StoreTest-x64.zip`.
2. Open **PowerShell as Administrator** in that folder and run `.\Install-TestPackage.ps1`.
   It trusts the throwaway test certificate, stops the standalone bridge, and installs the package.
3. Start **Scan 2 PDF Bridge** from the Start menu. Check the tray icon, the status page (Edition: Microsoft Store),
   a scan from Outlook, and **Settings → Apps → Startup** (Scan 2 PDF Bridge should be **On**). Then restart and confirm
   it comes back on its own.
4. Before installing from the real Store later, remove the test version in **Settings → Apps**.

## 5. Create the submission

**Pricing and availability:** Free · all markets · Public.

**Properties**
- Category: **Productivity**
- Privacy policy URL: `https://scan2pdf.aghapyts.com/privacy.html`
- Website: `https://scan2pdf.aghapyts.com`
- Support contact: `https://scan2pdf.aghapyts.com/support.html`
- Product declarations: none of the special cases apply (no in-app purchases, not a game).

**Age ratings:** complete the questionnaire. It's a utility with no user-generated content, chat or purchases,
so it rates for everyone.

**Packages:** upload `Scan2PdfBridge-<version>-Store.msix`. Device family: **Windows 10/11 Desktop**.

**Restricted capability justification** (`runFullTrust`, Partner Center asks for this):
```
Scan 2 PDF Bridge is a Windows desktop app (full trust) that connects the user's scanner to the Scan 2 PDF
Outlook add-in. It drives the scanner through the Windows Image Acquisition (WIA) COM API and serves the scanned
pages to the add-in over the local loopback address (127.0.0.1) only. It shows a system-tray icon and starts at
sign-in through the package's StartupTask. These require running as a full-trust desktop process.
```

**Store listing (English)**

*Product name:* `Scan 2 PDF Bridge`

*Short description:*
```
Connects your scanner to Outlook so you can scan documents straight into an email with the Scan 2 PDF add-in.
```

*Description:*
```
Scan 2 PDF Bridge is the free companion app for the Scan 2 PDF add-in for Outlook. It lets the add-in use the
scanner connected to your PC, so you can scan paper documents and attach them to the email you're writing, as
one PDF or as JPEG images.

• Works with any scanner that has a Windows (WIA) driver: if the Windows Scan app sees it, Scan 2 PDF does too
• Flatbed and document feeder, including double-sided scanning
• Runs quietly in the system tray and starts when you sign in
• Status page at http://127.0.0.1:52801 shows your scanners and settings
• Private by design: scans go only from your scanner to your email, on your own PC. Nothing is uploaded to
  Aghapy Tech Services.

To use it, add the Scan 2 PDF add-in in Outlook (https://scan2pdf.aghapyts.com), start a new email, and click
"Scan to PDF".

No account, sign-in or subscription required. Open source (MIT License) by Aghapy Tech Services.
```

*Search terms:* `scanner`, `scan to PDF`, `Outlook scan`, `scan to email`, `WIA`

*Screenshots* (at least one, 1366 × 768 or larger, from your PC):
1. Outlook with the Scan 2 PDF panel open and scanned pages
2. The tray icon menu (right-click the icon)
3. The bridge status page at http://127.0.0.1:52801 listing your scanner
4. Windows Settings → Apps → Startup showing Scan 2 PDF Bridge turned on

*Store logos:* use `branding/scan-2-pdf-icon-300.png` (300 × 300) for the 1:1 box art if requested.

**Notes for certification**
```
Scan 2 PDF Bridge is a tray app. After launch, look for its icon in the notification area (click ^ next to the
clock) and open http://127.0.0.1:52801/ to see its status page.

No scanner? The app then offers "Demo scanner (sample pages)", so the full flow can be tested without hardware:
1. Install and start Scan 2 PDF Bridge.
2. In Outlook (new Outlook for Windows or Outlook on the web), add the "Scan 2 PDF" add-in from the Microsoft
   Marketplace, or sideload https://scan2pdf.aghapyts.com/manifest.xml.
3. Start a new email → Scan to PDF → choose the demo scanner → Scan → Attach.

The app listens only on 127.0.0.1 and only accepts browser requests from the add-in's own web origin.
No account or sign-in is required.
```

## 6. Submit

Microsoft's certification usually takes a few business days. If it fails, send Claude the report.

## After it's live

- Copy the Store link (Partner Center → Product identity → **URL**, like `https://apps.microsoft.com/detail/9XXXXXXXXXXX`).
- Send it to Claude so the add-in's help box, the website and the support page point to the Store first.
- Updating: bump `<Version>` in `bridge/ScanBridge/ScanBridge.csproj`, run the workflow with a new tag, and submit
  the new `-Store.msix` in a new submission. Store users update automatically.

## Campaign IDs (where installs come from)

Every link to the Store listing carries a campaign ID (`cid`). Partner Center → Scan 2 PDF Bridge →
**Analytics → Acquisitions** shows page views and installs per campaign. Data appears with a delay of a few hours,
and installs are counted most reliably when people open the Store app (the badge opens it directly).

| Campaign ID | Where |
|---|---|
| `site-hero` | Home page, badge at the top |
| `site-get` | Home page, badge in "Get Scan 2 PDF" |
| `site-home` | Home page, links in the setup steps and FAQ |
| `site-help` | Help page links |
| `addin-panel` | Outlook panel, "not reachable" help |
| `github-readme` | GitHub README |
| `llms` | llms.txt (AI search tools) |

For new places (a LinkedIn post, an email, a partner site), use the Store link with a new ID, for example
`https://apps.microsoft.com/detail/9NNN9ZTWZ9TZ?cid=linkedin-launch`.

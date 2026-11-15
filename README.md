# Scan 2 PDF — Outlook add-in

*By [Aghapy Tech Services](https://aghapyts.com)* · **https://scan2pdf.aghapyts.com**

**Download the bridge:** grab `Scan2PdfBridge-win-x64.zip` from the latest
[Actions build](../../actions/workflows/build-bridge.yml) or from
[Releases](../../releases), unzip it, edit `appsettings.json`, and run `install.ps1`.

Adds a **Scan to PDF** button to the ribbon of any new email, reply, or forward. The user scans
one or more pages (flatbed or document feeder, including duplex), reviews them (reorder, rotate,
remove), names the file, and clicks **Attach PDF to email**. The pages become one PDF attached to that email.

```
┌──────────── Outlook (new message) ────────────┐        ┌──── same Windows PC ────┐
│  Task pane (TypeScript / Vite / Tailwind)      │  HTTP  │  Scan 2 PDF Bridge (.NET 8)│
│  • scan settings, page thumbnails              │ ─────► │  • 127.0.0.1:52801       │
│  • builds PDF in-browser (pdf-lib)             │ ◄───── │  • WIA → scanner         │
│  • addFileAttachmentFromBase64Async            │  JPEG  │  • returns JPEG pages    │
└────────────────────────────────────────────────┘        └──────────────────────────┘
```

**Why two parts?** Outlook add-ins are web apps running in a sandboxed webview, so they can't talk to
TWAIN/WIA scanners directly. The small local bridge does the scanner work and exposes it on loopback only.

## Repository layout

```
addin/                 Outlook web add-in
  manifest.xml         Add-in manifest (compose-mode ribbon button, Mailbox 1.8)
  taskpane.html        Task pane UI
  src/main.ts          UI + Office.js attach logic
  src/bridge.ts        Client for the local bridge
  src/pdf.ts           Builds the PDF (pdf-lib), sized from scan DPI
bridge/
  ScanBridge/          ASP.NET Core minimal API + WIA (Windows only)
  install.ps1          Per-user install + start at logon (Task Scheduler)
  uninstall.ps1
```

## Requirements

- **Windows 10/11** with a scanner that has a **WIA driver** (most office scanners/MFPs do; check that
  “Windows Fax and Scan” or the Windows Scan app can see it).
- Outlook that supports **Mailbox requirement set 1.8**: new Outlook for Windows, classic Outlook (Microsoft 365),
  or Outlook on the web in Edge/Chrome **on the PC where the bridge is installed**.
- Dev machine: Node 20+, .NET 8 SDK.

Outlook on Mac, iOS, and Android can't use the scanner path (the bridge is Windows-only), but
*Add images from file* still works there.

## Run it locally

### 1. Bridge

```powershell
cd bridge\ScanBridge
dotnet run
# check: http://127.0.0.1:52801/health  → {"ok":true,...}
#        http://127.0.0.1:52801/scanners → your scanner(s)
```

### 2. Add-in

```powershell
cd addin
npm install
npm run certs        # one-time: trusted HTTPS cert for https://localhost:3000
npm run dev          # serves the task pane
npm run validate     # optional: Microsoft manifest validation
```

### 3. Sideload into Outlook

- **Quickest:** `npm run sideload` (classic Outlook desktop).
- **New Outlook / Outlook on the web:** open https://aka.ms/olksideload → *My add-ins* →
  *Add a custom add-in* → *Add from file…* → pick `addin/manifest.xml`.

Then create a new email → **Scan to PDF** (in the ribbon, or under the **Apps** button in new Outlook).

## Deploy to the office

1. **Host the add-in.** On Vercel: import the repo with **Root Directory = `addin`** (`addin/vercel.json` sets the
   rest). Any other static host works too: `ADDIN_URL=https://your.host npm run build` and upload `addin/dist/`.
   The build writes **`dist/manifest.xml` with every URL pointing at your host** (on Vercel it uses the project's
   production domain automatically), served at `https://your.host/manifest.xml`.
2. **Allow the origin in the bridge.** In `bridge/ScanBridge/appsettings.json`, set
   `ScanBridge:AllowedOrigins` to your hosting origin (e.g. `https://scan.example.com`). Requests from any other
   web origin are rejected.
3. **Install the bridge on each scanning PC.**
   ```powershell
   cd bridge
   dotnet publish .\ScanBridge -c Release -o .\publish     # self-contained single exe, no .NET needed on targets
   .\install.ps1                                           # or: .\install.ps1 -SourceDir \\server\share\Scan2PdfBridge
   ```
   It installs to `%LOCALAPPDATA%\Scan2PdfBridge` and starts at logon as the signed-in user. That's deliberate:
   many WIA drivers don't work from a Windows service running as LocalSystem.
4. **Deploy the manifest** to users from Microsoft 365 admin center → *Integrated apps* → *Upload custom apps*.

## ⚠️ Browser “Local Network Access” — read before rollout

Chromium now treats a web page calling `localhost` as a permission-gated action. This applies to Chrome, Edge,
and progressively to WebView2, which hosts add-ins in new and classic Outlook.

- The add-in already sends its requests with `targetAddressSpace: "loopback"`, and the bridge answers
  Private-Network preflights (`Access-Control-Allow-Private-Network: true`).
- **Outlook on the web (Edge/Chrome):** users may see a prompt about the add-in connecting to *apps on this
  device*. To skip the prompt, set the Edge/Chrome policy **`LocalNetworkAccessAllowedForUrls`** through Intune/GPO.
  Include your add-in origin and `https://outlook.office.com` (plus `https://outlook.cloud.microsoft` if your
  tenant uses it).
- **Desktop Outlook (WebView2):** Microsoft is rolling the same checks out to WebView2. Test on a pilot
  machine with your current Outlook build.
- If the bridge can't be reached, the task pane says so, lists what to check, and leaves
  **Add images from file** available as a fallback.

## Is the bridge running?

The bridge has no main window. It lives in the **system tray** (click the ^ arrow next to the clock if you don't
see it). When started by hand it shows a "Scan 2 PDF Bridge is running" notification.

- **Status page:** http://127.0.0.1:52801/ shows the version, detected scanners and allowed add-in origins.
  Double-click the tray icon, or just run the exe again, to open it.
- **Exit:** right-click the tray icon → *Exit*.
- **Problems** (port already in use, bad settings) appear in a message box. Everything is logged to
  `%LOCALAPPDATA%\Scan2PdfBridge\logs\bridge.log` (tray menu → *Open log folder*).

## Bridge API

| Method | Path        | Notes |
|--------|-------------|-------|
| GET    | `/`         | Human-readable status page |
| GET    | `/health`   | `{ ok, version }` |
| GET    | `/scanners` | `[{ id, name, manufacturer }]` |
| POST   | `/scan`     | Body: `{ deviceId, source: flatbed\|feeder\|duplex, dpi: 75–600, color: color\|gray\|blackWhite, pageSize: letter\|legal\|a4, jpegQuality }` → `{ pages: [base64 JPEG…] }` |

Only one scan runs at a time (a second request returns 409). Scanner errors (paper jam, feeder empty, cover open,
busy, offline) come back as readable messages, and the task pane shows them.

## Tuning file size

A Letter page at 200 dpi in color with JPEG quality 75 is typically 150–400 KB. If PDFs come out too big:
use grayscale, lower the resolution to 150 dpi, or lower JPEG quality under **Advanced**. The task pane refuses to
attach anything over 20 MB and explains how to make it smaller.

## Publishing, signing and policies

- **Outlook add-ins store (Microsoft Marketplace):** step-by-step guide, listing text and reviewer notes in
  [`docs/marketplace/SUBMISSION.md`](docs/marketplace/SUBMISSION.md).
- **Code signing the bridge:** [`docs/signing/CODE-SIGNING.md`](docs/signing/CODE-SIGNING.md). The release workflow
  signs automatically once the certificate secrets are added.
- **Policies:** [Privacy](https://scan2pdf.aghapyts.com/privacy.html) ·
  [Terms](https://scan2pdf.aghapyts.com/terms.html) · [Support](https://scan2pdf.aghapyts.com/support.html)
- **Demo scanner:** when Windows reports no scanner, the bridge offers "Demo scanner (sample pages)" so the add-in
  can be tried without hardware.

## Known limitations / next steps

- WIA only. Scanners with TWAIN-only drivers need a TWAIN backend (e.g. NTwain) behind the same API.
- No OCR. To make PDFs searchable, add a server-side OCR pass (e.g. Tesseract / Azure Document Intelligence).
- The bridge listens on plain HTTP on loopback. Chromium treats loopback as a secure context, so this works from
  the HTTPS task pane. If your policy requires it, add an HTTPS endpoint in `appsettings.json`.

## Credits

Scan 2 PDF is developed and maintained by **Aghapy Tech Services**.
© 2026 Aghapy Tech Services. Released under the [MIT License](LICENSE).

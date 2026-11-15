# Publishing Scan 2 PDF to the Outlook add-ins store (Microsoft Marketplace)

Everything Microsoft asks for is ready except the parts only Aghapy Tech Services can do: opening the Partner
Center account, taking screenshots in real Outlook, and pressing **Submit**.

| Item | Where | Status |
|---|---|---|
| Add-in manifest (XML, v1.1.0.0) | https://scan2pdf.aghapyts.com/manifest.xml | ✅ |
| Support URL (in manifest) | https://scan2pdf.aghapyts.com/support.html | ✅ |
| Privacy policy | https://scan2pdf.aghapyts.com/privacy.html | ✅ |
| Terms of use (EULA) | https://scan2pdf.aghapyts.com/terms.html | ✅ |
| Website | https://scan2pdf.aghapyts.com | ✅ |
| Icons in manifest (64 × 64, 128 × 128) | `/assets/icon-64.png`, `/assets/icon-128.png` | ✅ |
| Store listing icon (300 × 300 PNG, ≤ 512 KB) | `branding/scan-2-pdf-icon-300.png` (9.4 KB) | ✅ |
| Listing text | Below | ✅ |
| Testing notes for reviewers | Below | ✅ |
| Demo scanner for reviewers without hardware | Bridge v1.1.0+ | ✅ |
| Screenshots (real Outlook) | You take these (see step 4) | ⬜ |
| Partner Center account | You create this (step 1) | ⬜ |

> **Publisher name must match.** The manifest's `<ProviderName>` is **Aghapy Tech Services**. Use exactly that as the
> publisher display name in Partner Center, or validation fails.

---

## 1. Open a Partner Center account (one-time)

1. You need a **work or school (Microsoft Entra ID) account**, not a personal Outlook.com account. If Aghapy has
   Microsoft 365 Business on `aghapyts.com`, use an admin account from it.
2. Go to https://partner.microsoft.com → **Join** the **Microsoft AI Cloud Partner Program** (free) with that account.
   Enter Aghapy Tech Services' legal business details exactly as registered.
3. Complete Microsoft's verification (email ownership, employment, and business). Watch the email inbox for the
   verification messages. This can take a few days.
4. In Partner Center: **Settings (gear) → Account settings → Programs → Microsoft 365 and Copilot** → enroll, and
   create a **publisher** named **Aghapy Tech Services**.

Official guide: https://learn.microsoft.com/office/dev/store/open-a-developer-account

## 2. Create the offer

Partner Center → **Marketplace offers** → **Microsoft 365 and Copilot** → **+ New offer** → **Office add-in**.

- **Name:** `Scan 2 PDF`
- **Product setup:** upload the manifest. Download it from https://scan2pdf.aghapyts.com/manifest.xml
  (or `addin/dist/manifest.xml` after `npm run build` with `VERCEL_ENV=production`).
- **Pricing:** Free.

## 3. Marketplace listing (copy/paste)

**Product name**
```
Scan 2 PDF
```

**Short description** (max 100 characters)
```
Scan documents from your scanner straight into your Outlook email as a PDF or JPEG attachment.
```

**Long description**
```
Scan 2 PDF lets you scan paper documents and attach them to the email you're writing, without leaving Outlook.

• Scan from a flatbed or document feeder, including double-sided scanning
• Scan as many pages as you need, then reorder, rotate, preview, or remove them
• Attach everything as one PDF, or as one JPEG per page
• Choose resolution, color, grayscale or black & white, and paper size (Letter, Legal, A4)
• Keeps attachments small with adjustable image quality
• Already have photos or scans? Add JPG/PNG files and turn them into a single PDF

REQUIREMENTS
Scanning requires the free Scan 2 PDF Bridge app for Windows 10 or 11, available at
https://scan2pdf.aghapyts.com. It connects Outlook to any scanner with a Windows (WIA) driver. If your scanner
works with the Windows Scan app, it works with Scan 2 PDF. The bridge runs only on your PC; no account, sign-in,
or subscription is needed, and there are no charges.

On Mac, mobile, or PCs without the bridge, you can still use "Add images from file" to attach images as a PDF.

PRIVACY
Your documents are processed on your own computer and go only into the email you are writing. Aghapy Tech
Services never receives your scans or emails.

Scan 2 PDF is open source (MIT License) and published by Aghapy Tech Services.
```

**Search keywords** (up to 3): `scan`, `scanner`, `PDF`

**Categories:** Productivity · Content Management

**Links**
- Privacy policy: `https://scan2pdf.aghapyts.com/privacy.html`
- Support / Help: `https://scan2pdf.aghapyts.com/support.html`
- End-user license agreement: `https://scan2pdf.aghapyts.com/terms.html`

**Supported products / platforms:** Outlook on Windows (new and classic), Outlook on the web, Outlook on Mac.
Mobile is not declared (no `MobileFormFactor` in the manifest).

## 4. Screenshots

Microsoft wants screenshots of the add-in **running in Outlook**: at least 1, up to 5, at **1366 × 768** px (PNG).
Take them in new Outlook or Outlook on the web, with the panel open on a new email:

1. Panel open with scanner list and settings ("Scanner ready")
2. Several scanned pages in the panel
3. Full-size page preview
4. Email showing the attached PDF
5. The "Attach as JPEG" option

Reference images of the panel are in `docs/marketplace/screenshots/`. Use them as a guide, not as the submission.

## 5. Notes for certification (paste into "Notes for certification")

```
Scan 2 PDF is a compose-mode Outlook add-in (Mailbox 1.8). Open a NEW email and click "Scan to PDF" on the
ribbon (in new Outlook: Apps → Scan 2 PDF).

Scanning uses a small free companion app for Windows, "Scan 2 PDF Bridge", which exposes the PC's scanner to the
add-in on http://127.0.0.1:52801. This is disclosed in the long description.

HOW TO TEST WITHOUT A SCANNER (recommended):
1. On a Windows 10/11 test PC, download and unzip the latest bridge:
   https://github.com/aghapytechservices/scan-2-pdf/releases/latest (Scan2PdfBridge-win-x64.zip)
2. Run Scan2PdfBridge.exe and choose Yes (install) or No (run once). A tray icon appears.
3. When no physical scanner is attached, the bridge offers "Demo scanner (sample pages)", which produces
   realistic sample pages, so every feature can be exercised.
4. In Outlook, open a new email → Scan to PDF → Scan. Try Feeder to get several pages; click a page to
   preview; rotate/reorder/remove; choose PDF or JPEG; click Attach. The files appear as attachments.
5. If Outlook shows a prompt to allow access to apps on this device, choose Allow (Chromium Local Network Access).

HOW TO TEST WITHOUT THE BRIDGE (Mac / Outlook on the web on any OS):
The panel shows "Scanner service not reachable" with guidance. Use "Add images from file" to select JPG/PNG
images; they are combined into one PDF (or attached as JPEGs) and added to the email.

No sign-in, account, license key, or payment is required. The add-in doesn't read mailbox content; it only adds
attachments to the message being composed.
```

## 6. Submit

Review → **Publish**. Microsoft's validation typically takes a few business days. If they report issues, the most
common ones for Office add-ins are listed at https://learn.microsoft.com/office/dev/store/checklist.

## Updating later

Bump `<Version>` in `addin/manifest.xml` for every manifest change, rebuild, and upload the new manifest in
Partner Center. Changes to the web files (taskpane UI) go live immediately via Vercel and don't need resubmission.

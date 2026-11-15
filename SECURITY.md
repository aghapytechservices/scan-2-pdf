# Security Policy

Scan 2 PDF (the Outlook add-in, the Scan 2 PDF Bridge app and https://scan2pdf.aghapyts.com) is maintained by
**Aghapy Tech Services**.

## Supported versions

Only the latest release of the bridge and the currently deployed add-in receive security fixes.

## Reporting a vulnerability

Please **don't open a public issue** for security problems.

- Use GitHub's private reporting: **Security → Report a vulnerability** on
  https://github.com/aghapytechservices/scan-2-pdf/security
- Or contact Aghapy Tech Services through https://aghapyts.com

Include what's affected, steps to reproduce, and the impact. We acknowledge reports within **3 business days**.

## How vulnerabilities are found and ranked

- **Dependabot** watches all dependencies (npm, NuGet, GitHub Actions) and opens alerts and update pull requests.
- **CodeQL** scans the TypeScript, C# and workflow code on every push and weekly (security-extended queries).
- Every finding is ranked by severity using its CVSS score / GitHub advisory severity:
  **Critical**, **High**, **Medium**, **Low**.

## Patch timelines (SLA)

| Severity | Fix released within |
|---|---|
| Critical | 7 days |
| High | 14 days |
| Medium | 30 days |
| Low | Next scheduled release (at most 90 days) |

Timelines start when the issue is confirmed. Dependency update pull requests are reviewed weekly.

## Incident response

1. **Triage:** confirm the report or alert, assess severity and affected versions. Alerts from Dependabot and
   CodeQL are emailed to the maintainers.
2. **Contain:** if the hosted add-in is affected, roll back to the last good deployment on Vercel immediately; if a
   bridge release is affected, mark it as a pre-release / withdraw it on GitHub.
3. **Fix:** develop and test the fix, then deploy the add-in and publish a new bridge release within the SLA above.
4. **Notify:** publish a GitHub security advisory describing the issue, affected versions and the fix, and credit
   the reporter if they wish.
5. **Review:** record what happened and what will prevent a repeat.

## Scope notes

- The add-in has no backend, no sign-in and stores no customer data; scanned documents stay on the user's PC
  (see the [privacy policy](https://scan2pdf.aghapyts.com/privacy.html)).
- The bridge listens only on `127.0.0.1` and accepts browser requests only from the origins listed in
  `appsettings.json`.

# Security Policy

## Supported Versions

Only the latest commit on `main` and the latest tagged release are supported with security updates.

## Reporting a Vulnerability

Please **do not** open a public GitHub issue for security vulnerabilities.

Instead, report vulnerabilities privately via GitHub Security Advisories (**Security** → **Advisories** → **Report a vulnerability**) on this repository. Include:

- A description of the issue and affected component(s)
- Steps to reproduce or a proof-of-concept
- Any suggested remediation

We will acknowledge receipt within 3 business days and coordinate a fix and disclosure timeline.

## Scope & Data Handling

- TaxOpus processes public-domain/official government sales tax files and does not require or store credentials.
- External downloads are saved atomically to `data/raw/` and parsed locally without executing downloaded content.

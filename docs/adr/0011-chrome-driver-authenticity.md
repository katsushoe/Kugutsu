# ADR 0011: ChromeDriver distribution authenticity

- Status: Accepted
- Date: 2026-10-14

## Context

Kugutsu downloads browser drivers during setup. EdgeDriver and geckodriver are distributed with Authenticode signatures, but Chrome for Testing ChromeDriver binaries are unsigned. The Chrome for Testing maintainers state that these binaries are not signed and do not currently plan to sign them ([official issue](https://github.com/GoogleChromeLabs/chrome-for-testing/issues/175)). The project also does not publish official ChromeDriver checksums ([official issue](https://github.com/GoogleChromeLabs/chrome-for-testing/issues/70)). Its official metadata supplies build-specific download URLs and supported Windows platforms ([official repository](https://github.com/GoogleChromeLabs/chrome-for-testing)).

Requiring Authenticode for all three drivers therefore prevents supported Chrome automation from being installed. Accepting any unsigned executable would weaken setup integrity.

## Decision

- Keep HTTPS-only downloading and the existing final-response host allowlist.
- Require EdgeDriver and geckodriver to have a valid Authenticode signature from Microsoft Corporation and Mozilla Corporation respectively.
- For ChromeDriver only, accept `NotSigned` when the official Chrome for Testing metadata selects the download for the installed Chrome major/minor/build and the extracted executable full four-part FileVersion exactly equals the metadata driver version.
- Reject other Chrome signature states and every mismatch. Do not extend the exception to other browser drivers.
- Record the selected official URL, browser and driver versions, architecture, file size, locally computed SHA-256, `signatureValidation`, and signer thumbprint (null for the unsigned ChromeDriver) in `drivers-manifest.json`.

## Consequences

This allows Chrome setup to proceed with the official unsigned binary while preserving publisher validation for Edge and Firefox and a strict version match for Chrome. The local SHA-256 supports later auditing and change detection but does not independently authenticate Google's origin because Google does not publish a corresponding checksum. HTTPS, host allowlisting, official metadata, and exact executable version matching are the available provenance controls. A future official Google signature or checksum should be evaluated as a stronger control.

## Alternatives considered

- Reject ChromeDriver until Google signs or publishes checksums: secure but blocks supported Chrome automation.
- Accept any ChromeDriver downloaded over HTTPS: insufficiently constrained and rejected.
- Reuse ChromeDriver for Edge or skip publisher checks for other drivers: incompatible with their independent versioning and signature guarantees; rejected.
# ADR-0002: WPF desktop client

- Status: Accepted, 2026-11-09

## Context
The sponsor confirmed on 2026-11-09 that all team laptops run Windows; there is no macOS or Linux requirement. Avalonia was preferred only for cross-platform reach.

## Decision
The desktop client is WPF on `net10.0-windows`. All logic that does not need the UI toolkit (local store, outbox, sync agent, view models) lives in `AuditPlatform.Client.Core`, a plain `net10.0` library.

## Consequences
- Mature Windows controls and commercial grid/spreadsheet components are available for working-paper forms.
- Client logic is unit-tested on Linux CI; only the thin WPF shell is Windows-specific. The shell still compiles on Linux (`EnableWindowsTargeting`).
- If a non-Windows platform is ever required, the shell is replaced (for example with Avalonia) while Client.Core is kept.

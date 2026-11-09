# ADR-0005: Identity and authentication

- Status: Open

## Context
3–10 internal users. The master must know who each caller is to enforce engagement membership, attribute every change, and sign off at the right review level. Laptops are often offline.

## Current state in the skeleton
The master registers a fail-closed `Pending` authentication scheme that authenticates nobody, so the sync hub answers 401 to every caller. No endpoint can be reached without a decision here.

## Options to evaluate
- Microsoft Entra ID (if the firm uses Microsoft 365): OIDC sign-in on the client, JWT bearer validation on the master.
- Master-issued accounts with passkeys/WebAuthn or password + TOTP, tokens issued by the master.

Inputs needed from the sponsor: whether the firm uses Microsoft 365 / Entra ID, and the MFA policy.

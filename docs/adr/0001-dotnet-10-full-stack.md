# ADR-0001: .NET 10 full stack, no libp2p

- Status: Accepted, 2026-11-09 (feasibility study decision D3)
- Supersedes: the Rust + libp2p direction of the original study

## Context
The master node is always online on an Oracle Cloud Arm VM (decision D2), so peer-to-peer transport adds little. Most product value sits in forms, Excel import/export, PDF/A and the review workflow, where the .NET ecosystem is stronger. The hiring pool in Vietnam is broader for .NET than for Rust.

## Decision
.NET 10 (LTS) for every component. Master: ASP.NET Core on linux-arm64. Clients sync to the master over SignalR (WebSocket on 443). libp2p is dropped; LAN peer sync is deferred and revisited only if field testing shows a need.

## Consequences
- One language and toolchain across master, client and tests.
- No native CRDT engine in .NET; if ADR-0004 picks a CRDT it comes through bindings (Yrs/Automerge).
- Support horizon follows .NET 10 LTS; plan the move to the next LTS before support ends.

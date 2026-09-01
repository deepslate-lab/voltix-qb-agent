# Voltix QB Agent

Windows agent that syncs **QuickBooks Enterprise (Desktop)** with [Voltix POS](https://voltix.echesconsultancy.com).
It runs on the VM where QuickBooks is installed and **dials out** to Voltix over HTTPS —
no inbound firewall rules needed on the QuickBooks network.

## How it works

- Talks to QuickBooks through **QBFC** (QuickBooks SDK, versions 13–17 probed at
  runtime, qbXML 13.0), late-bound — no SDK assemblies compiled in.
- Attaches to the company file QuickBooks currently has open
  (`BeginSession("", omDontCare)`), or opens a configured `.qbw` unattended when
  QuickBooks' Integrated Applications preferences grant automatic login.
- **Company-identity gate**: before syncing anything it verifies the open company's
  name matches what the Voltix tenant expects — a mispaired agent can never touch
  the wrong books.
- **Stateless**: watermarks, schedules, tuning and the work queue all live in
  Voltix. Reinstalling or updating the agent loses nothing.
- Short QuickBooks sessions per batch, busy-backoff, and no forced single-user
  mode — users keep working in QuickBooks while syncs run.

## Setup

1. In Voltix: **Settings → Integrations → QuickBooks Desktop** → set the expected
   company name → **Generate key** (shown once — copy it).
2. On the QuickBooks VM: install/run the agent, paste the Voltix URL and the key,
   click **Save & Test pairing**. The dialog shows which tenant you paired with —
   verify it before continuing.
3. First QuickBooks access pops QB's authorization dialog — grant access
   (and automatic login, if you want the agent to work while QuickBooks is closed).

The agent lives in the system tray; closing the window minimizes it.
Logs: `%LOCALAPPDATA%\VoltixQbAgent\logs\`.

## Build

```
dotnet build src/VoltixQbAgent -c Release
```

Requires the .NET 8 SDK. The output must run as **x86** (QBFC is 32-bit COM);
the project is pinned accordingly.

## Status

Phase: **skeleton** — pairing, heartbeat/health, and the QuickBooks company gate.
Entity sync (customers, items, accounts…) and document posting land next.

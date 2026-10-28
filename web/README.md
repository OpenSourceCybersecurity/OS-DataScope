# Data Scope web prototype

Clickable prototype of the Data Scope UI running on a synthetic Windows lab
(domain NORTHWIND, servers NW-FS01/NW-FS02, eight shares). No real data.

Stack: React 19 + TypeScript + Vite + Tailwind 4. The detection engine
(`src/domain/detectors.ts`), effective-access engine (`src/domain/access.ts`),
risk rules (`src/domain/risk.ts`) and CSV export (`src/domain/csv.ts`) are real
and unit tested; they will be ported to .NET in M2/M3 using the same test cases.
`src/api/store.ts` stands in for the API so the UI is unchanged when the .NET
backend arrives.

## Run it
    npm install
    npm run dev          # http://localhost:5173
    npm test             # 30 unit tests
    npm run build        # production bundle in dist/
    npm run build:single # one self-contained HTML file in dist-single/

## Try these
- Overview > "Fix these first": HR\Benefits is readable by Domain Users.
- Who has access > Ian Frost: a helpdesk tech who can modify Finance via nesting.
- Access reviews > Finance Q4: choose Remove on Greg Palmer to see the blast radius.
- Remediation: assign, mark implemented, run verification rescan, close.
- Servers & scans > Add file server: the onboarding wizard.
- Persona switcher (top right): Marcus Lee sees only his shares and reviews.

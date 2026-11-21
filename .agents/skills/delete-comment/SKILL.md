---
name: delete-comment
description: "Audit, categorize, and delete code comments across Exo Launcher, file GitHub issues for excuses, move decision records to ADRs, and tighten the comment-ban ratchet. Use when invoking /delete-comment or cleaning remaining files in apps/launcher/tools/comments-ratchet.json."
---

# Delete-comment workflow

Execute each step in order across the target files (or next batch from the ratchet). A file is done only when its non-allowlisted comment count in `apps/launcher/tools/comments-ratchet.json` reaches `0`.

## 1. Select the slice

Run from `apps/launcher`:

```powershell
node tools/check-comments.mjs --top 20
```

Pick one cohesive folder or feature group unless the user named specific files. Inspect every comment block in full surrounding code context before deleting anything.

## 2. Categorize and act on every comment

Ignore only machine-interpreted directives allowlisted by `tools/check-comments.mjs` (`// Generated from `, `design-disable-next-line`, `eslint-*`, `@ts-*`, `/// <reference`, `v8`/`c8`/`istanbul ignore`, `SPDX-License-Identifier`, `Copyright`, `Licensed under`, shebangs, `#Requires`).

Classify every other comment into one of four categories and act immediately:

- **Tautology** (restates what identifiers, types, or control flow already show): delete it; rename variables or extract a helper first if clarity drops without it.
- **Stale** (references retired files, versions, or removed behavior): delete it.
- **Excuse** (justifies a workaround, race, fixed sleep, polling loop, or partial implementation): delete the comment and file a GitHub issue (`gh issue create -R UhhErix/Exo-Launcher`) naming the exact file, symbol, root cause, and proper fix.
- **Decision Record** (documents a non-obvious invariant, constraint, safety boundary, or platform quirk): move the rationale into an existing ADR or create the next `apps/launcher/docs/adr/NNNN-<topic>.md`, then delete the inline comment.

## 3. Strip comments and tighten the ratchet

Apply targeted edits (or strip all non-allowlisted comments cleanly with the AST/lexer tool):

```powershell
node tools/check-comments.mjs --strip <relative-paths...>
node tools/check-comments.mjs --update
```

`--update` only locks in lower baselines and removes cleaned files; never edit `comments-ratchet.json` to add or raise counts. Zero prose comments may remain in any cleaned or newly created file.

## 4. Verify before claiming done

Run fresh checks after the final edit:

```powershell
node tools/check-comments.mjs
npm.cmd run check --prefix ui
```

Also run any affected package or host tests (`npm.cmd test --prefix cloud/<worker>`, `npm.cmd run check --prefix mouse`, or `dotnet test --project ExoLauncher.Tests\ExoLauncher.Tests.csproj -c Release -p:Platform=x64 -nologo`).

## Completion report

Report exact observed numbers: files cleaned, comment counts by category (`Tautology`, `Stale`, `Excuse`, `Decision Record`), GitHub issue URLs created, ADR paths created or updated, net comment lines removed, and remaining total in `comments-ratchet.json`.

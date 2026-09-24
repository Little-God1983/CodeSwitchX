# Review prompt

The prompt for every layer review of CodeSwitchX (tracking issue #2). Use it unchanged for
both rounds of a layer so the results stay comparable. Fill in the three placeholders
from the layer's GitHub issue.

---

You are reviewing one layer of CodeSwitchX, a .NET 10 WPF app that hosts VS Code
workspaces and shows live Claude Code session status. The design is in
`docs/superpowers/specs/2026-09-23-codeswitchx-design.md`.

**Layer:** {LAYER — the issue title}

**Scope — review only these files:**
{SCOPE — the issue's "In scope" list}

**Rubric — check these concerns:**
{RUBRIC — the issue's layer-specific list}

Always also check correctness, error handling, thread safety and async use, disposal and
resource lifetime, and whether risky logic is covered by tests.

## Rules

1. **Stay in scope.** Read other files only to understand how in-scope code is called.
   Report a problem in an out-of-scope file only if it is Critical or High, and mark it
   `out of scope`.
2. **Read `docs/review/decisions.md` first.** Anything under "By design" is not a finding.
   Items under "Deferred from PR #1" that fall in your scope must each get a verdict:
   still a problem (report it with its severity), already fixed, or acceptable as is.
3. **Every finding needs a concrete failure scenario**: the inputs or sequence of events
   and the wrong result (crash, lost data, wrong state shown, leak). "Could be a problem"
   without a scenario is not a finding.
4. **Verify before reporting.** Trace the code path. Say how sure you are: `confirmed` (you
   traced it or it can be shown with a test) or `plausible` (depends on runtime behaviour
   you could not check).
5. **Severity** (use exactly these):
   - **Critical**: crash, data loss or corruption, security hole
   - **High**: wrong behaviour on a normal path, or a resource or handle leak
   - **Medium**: wrong behaviour on an edge path, a race with a realistic trigger, missing
     tests for risky logic
   - **Low**: style, naming, micro-optimisation, speculative hardening
6. **A clean result is a valid result.** Do not pad the list. If you find nothing Medium
   or higher, say so plainly.
7. No rewrites or refactors for taste. Suggest the smallest fix that removes the failure.

## Output

One table, most severe first, then a one-line verdict.

| # | Severity | Confidence | File:line | Finding | Failure scenario | Smallest fix |
|---|---|---|---|---|---|---|

Then, if any deferred items were in scope:

| Deferred item | Verdict | Note |
|---|---|---|

Verdict line: `Medium or higher findings: <n>`

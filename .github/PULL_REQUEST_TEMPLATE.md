<!-- A suspected sandbox escape is reported privately first; see SECURITY.md. -->

## What and why

<!-- What this changes, and the problem it solves. Link the issue if there is one. -->

## Checklist

Tick what applies, and strike through or delete what does not. [CONTRIBUTING.md](../CONTRIBUTING.md) explains each item.

- [ ] **Threat model.** Rows touched: <!-- e.g. T4, W6, or "none" -->. If this changes what is
      defended, what is out of scope or the residual risk, `docs/threat-model.md` is updated.
- [ ] **Escape corpus.** A containment fix or new resolution behaviour has a case in
      `tests/Cap.Escape.Tests` (or a test marked `[Defends(...)]`), and no existing case is
      weakened.
- [ ] **A test that would have caught it** comes with every bug fix.
- [ ] **Benchmark baseline.** Moved in this change if a hot path is slower on purpose, with the
      reason given here.
- [ ] **Analyzer rules.** New or changed rules are in `AnalyzerReleases.Unshipped.md`.
- [ ] **CHANGELOG.md** has an entry under `Unreleased` for anything a user would notice;
      a containment change is under **Security**.
- [ ] **Docs** that describe the changed behaviour are updated.

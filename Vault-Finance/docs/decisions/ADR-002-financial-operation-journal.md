# ADR-002: Immutable Financial Operation Journal and Derived Balances

- Status: Accepted
- Date: 2026-11-23
- Scope: Accepted MVP architecture; only pure Domain increment 1 implemented now
- Contract: [Financial Engine Design](../financial-engine-design.md)

## Decision

Use an immutable canonical operation journal with derived per-account balances, not full double-entry accounting. Transfer is one positive magnitude with two distinct owned same-currency accounts; reversal references its immutable original and derives its exact inverse.

Preserve Money and Account. Monetary operation magnitudes are 0.01 through 99999999999999999999999999.99, compatible with decimal(28,2). Generic Money retains its wider signed/zero contract. No overdrafts, credit accounts or negative openings.

Record UTC instants and derive current effective dates in America/Santo_Domingo. No user-selected historical dates or scheduling.

A posting batch is indivisible. Opening correction can append a full compensation and replacement together, validating the final balance rather than an uncommitted component prefix.

Confirmed operations cannot be edited/deleted. There is no mutable reversed flag or Draft/Pending workflow. Confirmed is the immutable domain representation, not proof of SQL durability, funds or uniqueness.

## Resolved contradictions

- Replace the proposed one-Opening-forever index with immutable opening revisions: one root and a predecessor-linked history.
- A zero opening/replacement is an audited revision with no zero-valued financial operation.
- Positive opening correction uses one atomic batch/result/audit. Opening 100 corrected to 80 after spending 80 has final balance zero despite its compensation subtotal.
- Ordinary activity requires Active accounts. Archived accounts permit exact compensations if final balances remain nonnegative; positive replacement requires Active status.
- Idempotency is (owner, server-selected command type, key), not the old owner-wide scope. Identical retries replay; changed payload within a scope conflicts.
- Retain keys/fingerprints/results and audit for the MVP lifetime.
- All financial writes use one permission-hardened transactional writer; runtime direct financial DML is not an alternative.

These are closed architecture decisions. Provider mappings, constraints, procedure/role behavior and native concurrency still require subsequent evidence. None are implemented now.

## Future SQL guarantees

The writer obtains trusted owner identity, locks account anchors deterministically, reads fresh balances afterward, enforces initialization/current opening revision and at-most-one full reversal, and checks every affected final balance within [0, Bmax].

Components, revision, outcome and audit commit together. Reads/reconciliation use complete batches, never intermediate component cutoffs.

Composite FKs, CHECKs, uniqueness and permissions complement Domain/Application validation. They do not independently establish complete batch/audit membership or authenticate an HTTP user. A compromised service identity/sysadmin remains outside these guarantees.

Idempotency provides at most one committed result per owner/type/key, not exactly-once external delivery. Resolve ambiguous commit using the same scope and a fresh connection.

## Domain increment authorized now

Implement FinancialAmount, immutable FinancialPostingContext, kind/status enums and sealed FinancialOperation factories for Opening, Income, Expense, Transfer and Reversal.

Validate positive/range/precision/currency, IDs, account snapshots, UTC/calendar metadata and local reversal causality. Creating a reversal never mutates its original or proves global uniqueness.

No balance calculator, posting/correction/transfer coordinator, data access, migrations, financial endpoints or new packages.

## Alternatives and consequences

Full double entry is deferred until liabilities, splits, counterpart accounts or accounting statements justify it. Independent transfer rows, mutable balances as sole truth, editing/deleting confirmed history, event sourcing, distributed locks and generic repositories are not selected.

Batch/revision concepts are necessary for approved atomic opening replacement and explicit zero initialization. They are future persistence contracts, not extra Domain entities implemented speculatively here.

Known costs are serialized hot-account writes, aggregate reads, constraints/roles, and no partial reversals or user backdating. Revisit with a new ADR when requirements change; representation migration must preserve provenance, receipts, balances and reversal history.

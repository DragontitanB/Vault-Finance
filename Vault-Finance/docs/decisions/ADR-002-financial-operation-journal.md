# ADR-002: Immutable Financial Operation Journal and Derived Balances

- Status: Accepted
- Date: 2026-11-23
- Scope: Accepted MVP architecture; Domain, journal storage and opening/Income/Expense writers implemented; Transfer/Reversal/correction deferred
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

These are closed architecture decisions. Provider mappings and schema constraints are now implemented and tested in a dedicated SQL database. The initial-opening procedure, restricted-role execution, atomic opening rollback, canonical replay and independent-session concurrency are now implemented/tested. Other command writers, production principal deployment and physical network/native-deadlock fault injection still require subsequent work.

## Future SQL guarantees

The writer obtains trusted owner identity, locks account anchors deterministically, reads fresh balances afterward, enforces initialization/current opening revision and at-most-one full reversal, and checks every affected final balance within [0, Bmax].

Components, revision, outcome and audit commit together. Reads/reconciliation use complete batches, never intermediate component cutoffs.

Composite FKs, CHECKs, uniqueness and permissions complement Domain/Application validation. They do not independently establish complete batch/audit membership or authenticate an HTTP user. A compromised service identity/sysadmin remains outside these guarantees.

Idempotency provides at most one committed result per owner/type/key, not exactly-once external delivery. Resolve ambiguous commit using the same scope and a fresh connection.

## Implemented Domain boundary

Domain implements FinancialAmount, immutable FinancialPostingContext, kind/status enums and sealed FinancialOperation factories for Opening, Income, Expense, Transfer and Reversal.

Validate positive/range/precision/currency, IDs, account snapshots, UTC/calendar metadata and local reversal causality. Creating a reversal never mutates its original or proves global uniqueness.

Domain remains independent of data access and frameworks. Initial opening and a locked derived-balance read now execute through Application/Infrastructure without changing Domain. Correction/transfer coordinators and financial HTTP endpoints remain absent.

## Persistence increment

Infrastructure maps dedicated storage records without changing Domain. The additive FinancialJournalPersistence migration creates batches, operations, opening versions, outcomes and audit, plus a read-only effects view and append-only triggers. It adds only the necessary Accounts owner/currency key and leaves Identity/InitialPersistence unchanged.

The migration is validated only in the marked VaultFinanceJournalTests database. SQL uniqueness protects original-reversal and owner/type/key scopes; it does not perform funds checks, payload replay or complete-batch confirmation. Cross-row lifecycle/calendar/correction checks and least-privilege INSERT permissions remain writer obligations. See the [persistence contract and evidence](../financial-journal-persistence.md). ADR status remains Accepted.

## Initial-opening writer increment

The [opening protocol](../financial-opening-writer.md) implements zero/positive initialization, owner-scoped derived reads, SQL-canonical SHA-256 fingerprints, scoped request locks, deterministic account locks and complete outcome/audit commit. Stable business rejections store only outcome/audit; pre-commit technical failures roll back all rows. Response loss after commit is resolved by the same key, with an explicit unknown outcome when bounded recovery cannot establish a result.

InitialFinancialWriter adds four procedures and one restricted role, without changing tables or previously applied migrations. It is applied only to the existing marked test database. The runtime role is tested under a non-owner principal; development administrators retain privileges and are outside that guarantee. The [Income/Expense increment](../financial-income-expense.md) now reuses one internal SQL transaction owner for opening and entries, preserving opening v1 hashes/receipts. Two restricted public entry points select fixed Income/Expense commands. Locked exact funds/headroom validation and real archive serialization are verified. Transfer/Reversal and opening correction remain non-executable; no HTTP financial endpoint is enabled.

## Alternatives and consequences

Full double entry is deferred until liabilities, splits, counterpart accounts or accounting statements justify it. Independent transfer rows, mutable balances as sole truth, editing/deleting confirmed history, event sourcing, distributed locks and generic repositories are not selected.

Batch/revision storage supports approved atomic opening replacement and explicit zero initialization. No extra Domain entities or financial Application workflows are introduced by that storage.

Known costs are serialized hot-account writes, aggregate reads, constraints/roles, and no partial reversals or user backdating. Revisit with a new ADR when requirements change; representation migration must preserve provenance, receipts, balances and reversal history.

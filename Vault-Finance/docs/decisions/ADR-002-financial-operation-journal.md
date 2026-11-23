# ADR-002: Use an Immutable Operation Journal for the Financial MVP

- Status: Proposed
- Date: 2026-11-23
- Scope: Future financial engine only; no implementation or database change
- Detailed design: [Financial Engine Design](../financial-engine-design.md)

## Context

Vault Finance is a simulated personal-finance modular monolith with Clean Architecture, .NET 10, SQL Server 2019 Express, Identity cookies, CSRF, Money, and owner-scoped Account management. It has no financial operations or balances yet.

The first engine needs auditable opening balances, income, expenses, same-owner/same-currency transfers, reversals, idempotency, and protection against concurrent overspending. It does not need banking settlement, custody, FX, liabilities, split accounting, or full financial statements.

Current future documents allow transaction correction/deletion and do not decide overdraft policy. Money supports a wider range than common fixed-scale SQL mappings. These points must be reconciled before programming; this ADR does not amend them silently.

## Options

### A. Full double-entry ledger

An immutable journal entry contains two or more debit/credit postings against user-visible and counterpart ledger accounts, balanced per currency.

Advantages are a complete accounting equation and a natural path to liabilities, equity, splits, and accounting reports. Costs are counterpart-account semantics, additional records/mappings, cross-row balancing enforcement, and more concepts than the current product requires.

Double entry still requires resource authorization, atomic writes, idempotency, and a funds/concurrency policy; debit/credit equality alone provides none of those guarantees.

### B. Canonical operation journal with derived balances

A confirmed operation row identifies its owner/currency and one source, one destination, or both, with a shared magnitude. A read-only movement projection derives signed effects. A reversal references a non-reversal original and derives its exact inverse.

Advantages are fewer authoritative records, structurally atomic transfers, explicit external income/expense semantics, rebuildable balances without a cache, and compatibility with the existing Account model.

The limitation is deliberate: it is an operation journal, not a general ledger. Full accounting capabilities require a later explicit design/migration.

## Proposed decision

Choose B for the MVP.

1. Persist only confirmed immutable operations: opening, income, expense, transfer, and full reversal.
2. Store a transfer as one canonical row; never post independent source/destination transactions.
3. Derive movements and balances; do not maintain a mutable balance field or independent movements table initially.
4. Use owned/currency composite foreign keys, row-shape checks, one-opening uniqueness, and one-full-reversal uniqueness.
5. Represent reversals by immutable original references rather than client-editable copies of amount/accounts.
6. Commit operation, final idempotency outcome, and audit evidence within one explicit SQL transaction.
7. Serialize the owner/key with a transaction-owned SQL application lock; serialize participating account anchors in deterministic order before reading balance and status.
8. Preserve existing authentication, CSRF, owner-scoped 404 behavior, Clean Architecture dependencies, and /health independence.
9. Keep business rules in Domain/Application and SQL transaction/locking details in Infrastructure; no generic repository or extra distributed component.

The complete schema, protocol, examples, guarantees, and test matrix are in the design document. No entities, migrations, or endpoints are authorized by this ADR alone.

## Guarantees and limitations

The one-row transfer and fixed projection cannot contain a separately committed transfer half. Referencing an immutable original allows an exact inverse without duplicating monetary fields.

Nonnegative/bounded balances depend on the shared account-lock protocol and fresh balance read, not merely on a SQL transaction, RCSI, AsNoTracking, or an Account rowversion. All financial writers must comply. SQL constraints separately enforce ownership/currency and structural uniqueness.

Idempotency is an at-most-one committed financial effect per authenticated owner/key with durable replay evidence. It is not exactly-once external delivery. Unknown commit outcomes must be resolved using the same key on a fresh connection.

Append-only runtime permissions limit ordinary mutation but do not protect against a sysadmin or an INSERT writer bypassing the posting protocol. If that threat is in scope, permission-hardened stored-procedure-only writes are required before exposure.

There is no stored balance to drift, but projections and reporting can still be wrong. Independent snapshot reconciliation and real SQL concurrency/failure tests are mandatory.

## Pending policy and numeric decisions

The recommendation remains Proposed until Q1-Q8 in the detailed design are settled:

- Explicit decimal(28,2) financial-command/balance bounds versus preserving the full existing Money range. decimal(18,2) must not be adopted silently; decimal(31,2) alone is not proof of safe CLR conversion.
- No-overdraft funded-account policy versus negative openings/credit accounts.
- Accounting timezone, current-date-only behavior, and historical/backdated reporting requirements.
- Exact reversals on archived accounts and visibility of nonzero archived balances.
- Opening correction beyond its full cancellation, and whether correction bundles must be atomic.
- Durable key/audit retention, user erasure, and strength of the trusted-writer boundary.

Money and Account remain unchanged. These are future operation policies, not claims about current domain invariants.

## Alternatives deferred or rejected

- Full double entry: deferred until an actual accounting requirement justifies counterpart semantics.
- Two unrelated transfer rows: rejected because grouping, conservation, and retry safety become extra failure surfaces.
- Mutable Account.Balance as the sole truth: rejected because it loses independently reconstructable history.
- Synchronously maintained balance cache/guard: deferred until aggregate cost is measured and rebuild/reconciliation is designed.
- Editing/deleting confirmed movements: rejected in favor of compensations.
- Event sourcing, microservices, Redis locks, queues, and mediator/repository frameworks: unnecessary for one SQL-backed modular monolith.

## Consequences and revisit conditions

The MVP has a compact, auditable source of truth and per-account serialized writes. It also has explicit limits: hot-account contention, aggregate-read cost, full reversals only, bounded numeric/date policies pending approval, and no full general-ledger reporting.

Adopt a new ADR if liabilities, split entries, external accounting integration, independent financial writers, or measured scale needs change those tradeoffs. A later ledger migration must retain provenance, idempotency identities, balances, and reversal history rather than rewriting existing facts.

# Security Strategy

## Current implementation

The API implements cookie authentication under /api/v1/auth and authenticated account management under /api/v1/accounts. Account access is owner-scoped in every query/update. Domain and the applied InitialPersistence migration remain unchanged. Balances, monetary workflows, their authorization/idempotency, and auditing are not implemented.

The sections below distinguish current preparation from controls still required before implementing authenticated financial endpoints.

## Identity and secure sessions

Application defines credential DTO classes, a minimal AuthenticatedUser response (Id and Email only), typed outcomes, and IAuthenticationService. Infrastructure implements its use cases through UserManager and SignInManager. API endpoints translate outcomes into HTTP responses. Credential DTOs deliberately are not records, so their generated ToString cannot disclose passwords. Domain has no Identity, EF, or HTTP dependencies.

Register accepts a trimmed email of at most 254 characters and a 15-to-128-character password. Identity validates the email and password, normalizes email/username, salts and hashes the password, and persists the user. Password policy requires 15 characters and four distinct characters, without mandatory punctuation/case/digit rules so long passphrases are usable. Identity's own PasswordHasher uses 210,000 iterations; no custom hashing algorithm is implemented.

RequireUniqueEmail is enabled. The registration flow always sets UserName to Email, allowing the existing unique NormalizedUserName index to reject concurrent/case-normalized duplicates without changing the applied migration. Identity duplicate results and SQL unique-key collisions both become 409 duplicate_email. NormalizedEmail itself still has the original non-unique index: future internal writers must preserve the username/email invariant or introduce a reviewed uniqueness migration before using another username scheme.

Registration returns 201 with only id/email and does not sign in. Duplicate registration intentionally reveals a conflict as required by the API contract; clients may present a less specific message. Email ownership is not verified yet. Confirmation, recovery, MFA, and production abuse controls require separate flows before a public financial launch.

Login uses Identity.PasswordSignInAsync with lockoutOnFailure=true and isPersistent=false. A successful response returns 200 with id/email and issues a session cookie, never a JWT. The cookie is __Host-VaultFinance with Path=/, no Domain, HttpOnly, SecurePolicy.Always, SameSite=Lax, and a 30-minute sliding ticket lifetime. No persistent browser expiry or absolute session-duration cap is configured. /me requires authentication, reloads the user through Identity, and exposes only id/email. Cookie challenges return ProblemDetails 401/403 instead of HTML redirects.

Unknown users, wrong passwords, locked accounts, and disallowed logins receive the same 401 invalid_credentials response. For unknown/locked users a process-local dummy hash is verified through Identity's own hasher, reducing the obvious missing-hash timing difference. This is not a promise of constant-time HTTP responses.

Logout requires authentication and CSRF, calls SignInManager.SignOutAsync, deletes the browser session cookie, and returns 204. It does not invalidate independently copied cookies on other clients or revoke every session. Identity's normal security-stamp validation remains in effect; global revocation/server-side ticket storage is future work. No tokens belong in browser local storage.

## Rate limits and temporary lockout

Built-in sliding-window limits apply before authentication, CSRF validation, and request handlers: five login requests and three registration requests per minute per direct transport IP, with four segments and no queue. Rejections return ProblemDetails 429, no-store, and Retry-After when supplied by the limiter. Invalid CSRF/body attempts consume the same budget.

Identity temporarily locks an account for one minute after ten failed password attempts. The threshold is higher than one IP's one-minute budget, and the short lockout limits the impact of account-targeted denial. Failed CSRF checks never increment Identity's failure count. Tests verify lockout and recovery after expiry. Rate limits and lockout cannot eliminate distributed credential attacks or targeted account denial.

The limiter is process-local. Reverse proxies must explicitly configure trusted forwarded headers before relying on client-IP partitioning; arbitrary forwarded/test headers are not trusted by production code. Multiple deployments require coordinated edge protection. Do not add permissive proxy trust or disable limits to make deployment work.

## CSRF and browser boundaries

Antiforgery uses ASP.NET Core's cookie/request-token pair, with X-CSRF-TOKEN as the required request header. The __Host-VaultFinance.Antiforgery cookie is HttpOnly, Secure, Path=/, and SameSite=Strict. JavaScript obtains the request token from the JSON body of GET /api/v1/auth/csrf, not by reading the cookie. The endpoint is anonymous so registration and login can be protected, and its response is never cached.

Browser sequence:

1. GET /csrf using the same browser cookie jar; retain the returned token in memory.
2. POST /register or /login with JSON, the browser cookies, and X-CSRF-TOKEN.
3. After successful login, fetch /csrf again: tokens are identity-bound, so the anonymous token must not be reused.
4. POST /logout with the authenticated token, then fetch /csrf again before another anonymous mutation.

Login and logout delete the browser's antiforgery cookie so the next bootstrap rotates the pair. Tests also restore the original anonymous cookie/token alongside a valid authentication cookie and confirm that Identity-bound validation rejects it.

CsrfProtectionMiddleware explicitly validates every matched /api endpoint using a method other than GET, HEAD, OPTIONS, or TRACE. This includes anonymous register/login and authenticated logout, regardless of content type. Exactly one non-empty header is required; missing/invalid/mismatched tokens return ProblemDetails 400 invalid_csrf_token before handler execution. This enforces JSON protection independently of the framework's form-endpoint metadata.

Protected business endpoints outside /api/v1/auth challenge anonymous callers with 401 before CSRF validation. This never executes a handler without identity. For authenticated account callers, create, patch, and archive all require CSRF. The existing registration/login/logout CSRF response ordering is preserved.

For every future API mutation: keep the route under /api, retain the middleware, require authorization unless explicitly reviewed as public, and add tests for missing/invalid/valid tokens. DisableAntiforgery metadata does not bypass this manual guard. External webhook/service authentication would require a separately reviewed design, not a casual exemption. Safe GET handlers must not mutate business data.

SameSite cookies and CORS are not substitutes for antiforgery validation. Missing or invalid tokens must fail before mutation. Prefer same-origin hosting; if cross-origin development is needed, configure an exact origin allowlist and credentials intentionally, never a wildcard credential policy.

See [Microsoft's antiforgery guidance](https://learn.microsoft.com/en-us/aspnet/core/security/anti-request-forgery?view=aspnetcore-10.0) and [SameSite guidance](https://learn.microsoft.com/en-us/aspnet/core/security/samesite?view=aspnetcore-10.0).

## Resource authorization

Require authentication by default once protected endpoints exist, leaving only explicitly public endpoints anonymous. Obtain the owner identity from validated server-side claims, never a client-supplied UserId.

The accounts route group requires authorization. The API validates a non-empty Guid from the server-side NameIdentifier claim; no client field can select the owner. Application takes that server-supplied owner and the persistence adapter scopes every list, detail, rename, and archive query by it. Id-based operations additionally restrict by Id. Foreign resources and nonexistent resources return the identical 404 account_not_found contract, never 403 or an archive-state disclosure.

Create/rename DTOs have only editable fields and disallow unknown JSON members. Archive permits an absent/empty JSON object and also rejects unknown fields. Supplying ownerId, userId, id, status, balances, or currency on rename is rejected instead of silently ignored. Supported query parameters are allowlisted and must not repeat. Responses contain only id/name/currency/status. The API never binds entities or copies request values into tracked objects.

Rename and archive invoke the existing domain methods. SQL then updates only Name or Status with owner predicates; currency/ownership are not writable. Rename also requires Active in the SQL predicate, rejecting a concurrent archive. There is no deletion endpoint or physical-delete operation in the account port. Income/Expense queries and writes include explicit trusted-owner predicates; foreign and missing IDs both return NotFound. Future category, budget and transfer use cases still require explicit related-resource ownership checks; existing protection does not automatically secure future resources.

See [Microsoft's resource-based authorization guidance](https://learn.microsoft.com/en-us/aspnet/core/security/authorization/resource-based?view=aspnetcore-10.0).

## Transport and hosting

Keep HTTPS for development and production. Production must provide valid certificates, host restrictions, trusted proxy configuration where needed, and an appropriate HSTS policy. Do not enable Development in production or expose detailed errors and OpenAPI publicly by accident.

The pipeline is exception/status-code handling, production HSTS, HTTPS redirection, routing, rate limiting, authentication, the API CSRF guard, authorization, and framework antiforgery. /health short-circuits after HTTPS redirection/routing and never validates Identity cookies or connects to SQL. API requests also explicitly reject non-HTTPS processing if redirection cannot be configured. Auth responses use Cache-Control: no-store, and Kestrel limits request bodies to 16 KiB in the current API.

Future cookie and antiforgery configuration must explicitly require secure transport. Development certificates are local user assets and must never be committed.

## Validation and errors

Validate request types, required fields, lengths, enum values, dates, currency, precision, and amounts at appropriate boundaries. Enforce business invariants inside application/domain behavior. Use request contracts that accept only editable fields to prevent overposting.

EF Core SQL Server persistence uses parameterized operations. Sensitive-data logging is not enabled. Account CHECK constraints and the ownership foreign key complement domain validation. Return consistent problem responses without stack traces, SQL details, tokens, or secrets when use cases arrive. Add request size limits and cancellation handling when relevant endpoints exist.

## Idempotency and concurrency

Require idempotency keys for future retryable financial creation operations, including transfers. Scope keys by authenticated owner and operation. Persist the key, a canonical request fingerprint, result reference/response, and completion state with the business operation.

Reusing a key with the same payload should replay the original outcome; a different payload should be rejected as a conflict. Database uniqueness and transactional handling must cover concurrent duplicates. Define expiry and replay policy before implementation. A key stored only in process memory is insufficient.

Use explicit database transactions for multi-write operations and choose optimistic concurrency for conflicting edits. Define behavior for retries, rollbacks, and failures with persistence tests.

## Audit and secrets

Record relevant financial and security events with actor, resource, action, UTC time, and correlation identifier. Business audit entries commit with the financial write. Minimize stored details and protect access; define retention and erasure policies before persisting personal data.

Never log passwords, authentication cookies, antiforgery tokens, connection strings, or full financial request bodies. DefaultConnection currently comes from the API's existing User Secrets or environment configuration. User Secrets is a local development convenience, not an encrypted production vault. No real connection string is stored in the repository or supplied as a CLI argument. Production credentials require a secret store/environment configuration and least-privilege database access.

ASP.NET Core Data Protection uses application name VaultFinance and, locally, the normal Windows user-profile key repository protected by DPAPI. Run local hosting under the same Windows identity; an isolated process cannot necessarily decrypt that user's keys. Do not delete keys or switch production to ephemeral keys to mask that failure.

Before deployment, configure a durable repository outside the deployment directory, encrypted at rest with a certificate or an appropriate managed key service. Restrict access to the service identity. Instances of the same deployment must share protected keys and the same application name; separate environments require separate rings. Preserve old keys needed for unexpired cookies/CSRF tokens across restarts and rotations, and define backup, recovery, compromise response, and retention policies. Losing keys invalidates sessions and request tokens. Tests use a separate ephemeral provider only for isolation, never as a production fallback.

API startup never creates or migrates databases. The initial migration script checks for existing data/foreign tables, restricts writes to local VaultFinanceDb, and is explicitly pinned to InitialPersistence. The financial migration is not applied there by this increment.

SQL tests now require a separate SqlTestConnection for VaultFinanceJournalTests. The fixture permits explicit creation only when absent, verifies a database-level project ownership marker before migrations, and refuses unmarked/foreign databases. No development/system database is a test destination. Most synthetic data rolls back; a committed immutable history group used for independent-connection preservation checks is retained. There is no database drop/reset or destructive cleanup. See the [test lifecycle](financial-journal-persistence.md).

Financial journal tables reject UPDATE/DELETE through both EF guards and SQL triggers. Typed/composite FKs enforce owner/currency references, and scoped unique indexes protect stored reversal and idempotency identities. These are structural safeguards, not end-user authorization, operational idempotency or overdraft prevention. Privileged principals can disable triggers; the initial-opening writer uses an EXECUTE-only role, trusted owner context, account/key locks, canonical replay and one SQL commit for its batch/result/audit. The role denies direct history access; tests verify it under a non-owner principal. Development administrators retain privileges and are outside that guarantee. Income/Expense/Transfer use the same internal SQL transaction owner, preserving direct-DML denials while granting only fixed public entry points. Fresh locked funds/headroom checks and both archive orders are verified. Future correction/reversal writers must extend the approved invariants. No financial HTTP routes are enabled. See the [writer security/commit limits](financial-opening-writer.md).

## Planned verification

Internal [Transfer](financial-transfers.md) now reuses the same protocol with both-account ownership/state/currency checks under globally ordered account locks, source funds, destination headroom and exact conservation. One canonical operation, persistent result/rejection and audit commit atomically. Its public fixed-command wrapper alone receives an additional EXECUTE grant; private helpers/dispatcher and direct DML remain denied. Tests exercise foreign accounts in both positions, non-owner role permissions, full rollback, opposite-direction/mixed-writer races and real EF archive ordering. No financial HTTP endpoint exists; future transport must supply authenticated owner context and retain the global CSRF policy. Reversal/correction writers and production principal provisioning remain deferred. Shared journal-index contention and deadlocks remain possible.

Current tests cover registration, normalized duplicate rejection, password/email validation, successful and generic failed login, temporary lockout/recovery, minimal /me responses, browser logout, cookie flags, no-cache/CORS behavior, rate limiting, missing/invalid/cross-browser/old-identity CSRF tokens, and liveness without SQL. HTTP/Identity tests use real SQL Server through WebApplicationFactory, synthetic users/passwords, one transaction per test shared across request scopes, and rollback on disposal. Existing domain and persistence tests remain.

Account tests add anonymous 401 on all five routes, authenticated creation/rename/archive, strict JSON input, name/currency validation, owner-only lists, foreign-ID reads/writes returning 404, archived edit rejection, idempotent archive, absent/invalid CSRF, pagination/filter/sort bounds, no-tracking reads, and rejection of stale active-snapshot renames after archive. Two independent cookie jars represent distinct owners inside each rollback-only SQL test.

Future financial features still need cross-user resource tests, idempotency/concurrency tests, audit verification, and endpoint-specific authorization/CSRF checks. In-memory HTTP tests do not perform TLS negotiation; local HTTPS is checked separately without disabling certificate validation.

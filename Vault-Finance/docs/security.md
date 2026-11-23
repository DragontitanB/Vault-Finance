# Security Strategy

## Current implementation

The API exposes only anonymous process liveness and development-only OpenAPI, with HTTPS redirection. Infrastructure now registers Identity EF stores with Guid keys, secure application-cookie options, antiforgery services, and authentication/authorization/antiforgery middleware. There are no registration, login, logout, token-issuance, or financial routes. No cookies or request tokens are issued by an application endpoint. Resource authorization, financial HTTP request validation, idempotency, and auditing are not implemented. Immutable UserId and a database foreign key are not proof of authenticated ownership.

The sections below distinguish current preparation from controls still required before implementing authenticated financial endpoints.

## Identity and secure sessions

ApplicationUser derives from IdentityUser<Guid> in Infrastructure, with official Identity EF stores. UserManager, SignInManager, and default token providers are registered; no flow uses them through an HTTP route yet. Do not implement custom password hashing or expose identity persistence types as domain entities.

The application cookie is configured as __Host-VaultFinance with Path=/, no Domain, HttpOnly, SecurePolicy.Always, SameSite=Lax, and a 30-minute sliding lifetime. Redirect events return 401/403 instead of HTML login redirects. These settings prepare the future same-origin browser client; routes do not yet issue sessions. Session recovery, revocation, lockout policy, rate limits, and lifecycle behavior require review and tests before exposure. Do not place credentials in browser local storage.

Future API authentication failures should return appropriate 401/403 responses. Rate limits, password policy, recovery, email verification, and session lifecycle tests must be decided before exposing authentication to real users.

## CSRF and browser boundaries

Antiforgery is configured with header X-CSRF-TOKEN and a __Host-VaultFinance.Antiforgery cookie using HttpOnly, SecurePolicy.Always, Path=/, and SameSite=Strict. Middleware is installed, but there is no request-token endpoint or state-changing application route. Middleware registration alone does not require antiforgery validation on every future JSON endpoint: those endpoints must explicitly validate tokens or carry the appropriate endpoint metadata. Safe GET endpoints must have no state-changing effects.

SameSite cookies and CORS are not substitutes for antiforgery validation. Missing or invalid tokens must fail before mutation. Prefer same-origin hosting; if cross-origin development is needed, configure an exact origin allowlist and credentials intentionally, never a wildcard credential policy.

See [Microsoft's antiforgery guidance](https://learn.microsoft.com/en-us/aspnet/core/security/anti-request-forgery?view=aspnetcore-10.0) and [SameSite guidance](https://learn.microsoft.com/en-us/aspnet/core/security/samesite?view=aspnetcore-10.0).

## Resource authorization

Require authentication by default once protected endpoints exist, leaving only explicitly public endpoints anonymous. Obtain the owner identity from validated server-side claims, never a client-supplied UserId.

Scope reads and writes to the owner. Check the ownership of related accounts, categories, budgets, transactions, and both sides of a transfer. A guessed identifier must not expose another user's data. Return consistent forbidden/not-found responses according to a documented disclosure policy.

See [Microsoft's resource-based authorization guidance](https://learn.microsoft.com/en-us/aspnet/core/security/authorization/resource-based?view=aspnetcore-10.0).

## Transport and hosting

Keep HTTPS for development and production. Production must provide valid certificates, host restrictions, trusted proxy configuration where needed, and an appropriate HSTS policy. Do not enable Development in production or expose detailed errors and OpenAPI publicly by accident.

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

ASP.NET Core Data Protection currently uses the normal Windows user-profile key repository protected by DPAPI. Run local hosting under the same Windows identity; an isolated process cannot necessarily decrypt that user's keys. Do not delete keys or switch to ephemeral keys to mask that failure. Production needs an explicit durable key repository, encryption/access controls, application isolation, and a rotation/recovery policy before sessions are enabled.

API startup never creates or migrates databases. The initial migration script checks for existing data and foreign tables, restricts writes to local VaultFinanceDb, and performs no destructive cleanup. SQL tests use synthetic identities and rollback-only transactions; they never drop databases or delete existing records.

## Planned verification

Add cross-user read/write tests, unauthenticated access tests, missing/invalid CSRF token tests, cookie attribute checks, input boundary cases, concurrent duplicate request tests, rollback tests, and audit persistence tests as those features are introduced. The current health and OpenAPI tests do not verify those future protections.

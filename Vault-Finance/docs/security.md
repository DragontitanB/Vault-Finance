# Security Strategy

## Current implementation

The API provides only an anonymous process liveness endpoint, HTTPS redirection, a development HTTPS launch profile, and development-only OpenAPI JSON. No Identity registration, authentication, session cookies, antiforgery validation, resource authorization, financial validation, idempotency, or auditing is implemented.

The following sections define planned controls for later iterations. They are not claims of existing protection.

## Identity and secure sessions

Use ASP.NET Core Identity through Infrastructure for password hashing, account lifecycle, lockout, and session management. Do not implement custom password hashing or expose identity persistence types as domain entities.

Use server-managed cookie authentication for the future first-party browser client. Authentication cookies will be HttpOnly, Secure, narrowly scoped, and have an explicit SameSite policy, bounded lifetime, and session revocation behavior. Start from a same-origin deployment with SameSite=Lax; revisit deliberately if topology changes. Do not place session credentials in browser local storage.

Future API authentication failures should return appropriate 401/403 responses. Rate limits, password policy, recovery, email verification, and session lifecycle tests must be decided before exposing authentication to real users.

## CSRF and browser boundaries

Cookie-authenticated state-changing requests require ASP.NET Core antiforgery token validation. The browser will obtain a request token through a deliberate flow and return it in a custom header. The session cookie remains HttpOnly. Safe GET endpoints must have no state-changing effects.

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

Use parameterized EF Core queries when persistence arrives and avoid SQL string concatenation. Return consistent problem responses without stack traces, SQL details, tokens, or secrets. Add request size limits and cancellation handling when relevant endpoints exist.

## Idempotency and concurrency

Require idempotency keys for future retryable financial creation operations, including transfers. Scope keys by authenticated owner and operation. Persist the key, a canonical request fingerprint, result reference/response, and completion state with the business operation.

Reusing a key with the same payload should replay the original outcome; a different payload should be rejected as a conflict. Database uniqueness and transactional handling must cover concurrent duplicates. Define expiry and replay policy before implementation. A key stored only in process memory is insufficient.

Use explicit database transactions for multi-write operations and choose optimistic concurrency for conflicting edits. Define behavior for retries, rollbacks, and failures with persistence tests.

## Audit and secrets

Record relevant financial and security events with actor, resource, action, UTC time, and correlation identifier. Business audit entries commit with the financial write. Minimize stored details and protect access; define retention and erasure policies before persisting personal data.

Never log passwords, authentication cookies, antiforgery tokens, connection strings, or full financial request bodies. Production credentials will come from environment configuration or a secret store, with least-privilege database access. Developer User Secrets may be configured explicitly later. No real secrets or connection strings belong in tracked files.

## Planned verification

Add cross-user read/write tests, unauthenticated access tests, missing/invalid CSRF token tests, cookie attribute checks, input boundary cases, concurrent duplicate request tests, rollback tests, and audit persistence tests as those features are introduced. The current health and OpenAPI tests do not verify those future protections.

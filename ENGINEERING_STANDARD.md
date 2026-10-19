# Production Engineering Standard
## Strict Typing, Clean Code & Maintainable Architecture

**Status:** Mandatory  
**Scope:** All production code, tests, configuration, build scripts, and CI pipelines  
**Primary objective:** Maximize correctness, readability, security, and maintainability while minimizing unnecessary complexity.

These rules are engineering constraints, not optional recommendations. Apply them consistently to new code, modifications, refactorings, and generated code.

When rules conflict, prioritize correctness, security, simplicity, and explicit behavior. Follow YAGNI: implement only what current requirements justify.

## 1. Type Safety & Deterministic Behavior

### 1.1 Explicit Types

- Every public API, function parameter, return value, property, and data contract must have an explicit, unambiguous type.
- Prohibit `dynamic`, `object`-based programming, untyped dictionaries, implicit `any`, and equivalent escape hatches unless a concrete interoperability requirement demands them.
- Infer local variable types only when the type is immediately obvious and inference improves readability. Otherwise, declare the type explicitly.
- Use dedicated types for domain concepts rather than primitive values when this materially improves correctness.
- Avoid unnecessary type conversions, unsafe casts, reflection, and runtime type inspection.
- Treat external data as untrusted until validated and converted into a typed internal representation.

### 1.2 Immutability by Default

- Prefer immutable variables, fields, properties, collections, and data structures.
- Use `const` where supported and appropriate; otherwise use the language's immutable or read-only equivalents.
- In C#, prefer `readonly` fields, `init`-only properties, immutable value objects, and immutable collection types where appropriate.
- In TypeScript, prefer `const`, `readonly`, readonly properties, and readonly collection interfaces.
- Use mutable state only when required by the domain or algorithm. Keep its scope and lifetime as small as possible.
- Never expose mutable internal collections directly.
- Avoid mutation of input parameters and shared objects.
- Prefer transformations that produce explicit results over hidden modifications.

### 1.3 Nullability & Optional Values

- Enable strict nullability checks wherever the language supports them.
- In C#, enable nullable reference types.
- In TypeScript, enable `strict` and `strictNullChecks`.
- Model optional values explicitly. Distinguish between missing, `null`, invalid, and valid values where these states have different meanings.
- Do not use null suppression, non-null assertions, or null-forgiving operators to bypass compiler checks without a verified invariant.
- Avoid returning `null` where an explicit result type, empty collection, or structured error is more appropriate.
- Validate nullable values at system boundaries and narrow their types before use.

### 1.4 Domain Modeling

- Represent domain states explicitly instead of relying on loosely related Boolean flags.
- Prefer enums, discriminated unions, dedicated value objects, and validated domain types where they reduce invalid states.
- Avoid primitive obsession when identifiers, paths, severities, statuses, or other domain concepts require distinct semantics.
- Make invalid states difficult or impossible to represent.
- Use exhaustive handling for enums and discriminated unions where supported.

## 2. Code Hygiene & Production Readiness

### 2.1 Self-Documenting Code

- Use meaningful, precise, and consistent names for types, variables, functions, properties, and files.
- Functions should communicate their intent through their names, parameters, return types, and observable behavior.
- Avoid ambiguous abbreviations, generic names such as `data`, `manager`, or `helper`, and misleading names.
- Keep functions small enough to understand without mentally simulating unrelated operations.
- Replace complicated Boolean expressions with named predicates when doing so improves readability.

### 2.2 Comments & Documentation

- Do not add comments that merely restate what the code already expresses.
- Prohibit commented-out code, obsolete implementation notes, temporary annotations, and unexplained TODOs in production code.
- Comments are permitted only when they explain information that cannot be expressed adequately through code, such as non-obvious invariants, security constraints, external limitations, or algorithmic rationale.
- Public APIs must have appropriate external documentation or API documentation when required by the project.
- Use XML documentation in C# when required by the public API contract or documentation tooling. Do not prohibit it categorically.
- Keep architecture decisions, operational instructions, and design rationale in the appropriate documentation files.
- Every TODO must have an explicit purpose and an actionable tracking reference if it is retained.

### 2.3 Debugging & Logging

- Prohibit temporary debugging statements, interactive breakpoints, and ad hoc tracing in production code.
- Use the project's structured logging abstraction for operational events.
- Never log credentials, access tokens, secrets, or unnecessarily sensitive data.
- Log meaningful events at appropriate severity levels without flooding logs.
- Do not use exceptions as a substitute for ordinary control flow.
- Automated checks must detect prohibited debugging APIs and accidental diagnostic output where practical.

### 2.4 Dead Code & Dependencies

- Remove unused imports, variables, parameters, methods, classes, and unreachable branches.
- Never retain commented-out legacy implementations.
- Remove dependencies that are no longer needed.
- Do not add a package when the platform or existing dependencies already provide a suitable solution.
- Avoid speculative abstractions, unused extension points, and compatibility layers without an actual requirement.
- Treat warnings as defects. New warnings are prohibited unless explicitly justified and narrowly suppressed.

## 3. Functions, Modularity & DRY

### 3.1 Single Responsibility

- Every function, class, and module must have one coherent responsibility.
- Keep domain logic separate from I/O, infrastructure, presentation, and external service integrations.
- Avoid god classes, monolithic files, generic utility dumping grounds, and excessive cross-module dependencies.
- Extract a component when doing so creates a meaningful domain boundary, simplifies testing, reduces genuine duplication, or isolates a changing concern.
- Do not split code into additional files or abstractions solely to satisfy an arbitrary line-count limit.

### 3.2 DRY Without Overengineering

- Avoid duplicating business rules, security checks, and complex algorithms.
- Extract shared logic when duplication is meaningful and the behavior is genuinely the same.
- Do not abstract two superficially similar operations if their semantics or expected evolution differ.
- Prefer a small amount of clear duplication over a generic abstraction that introduces more complexity than it removes.
- Follow YAGNI: do not create reusable frameworks, generic factories, interfaces, or configuration mechanisms without a concrete need.
- Avoid both copy-paste business logic and premature abstraction.

### 3.3 Pure Functions & Side Effects

- Prefer pure functions for calculations, transformations, validation, and domain decisions.
- Pure functions must return the same result for the same inputs and must not modify external state.
- Isolate unavoidable side effects, including file access, network requests, logging, clocks, random-number generation, and process execution.
- Make side effects visible through method names, dependencies, or return types.
- Inject external dependencies when doing so improves testability or makes behavior explicit.
- Do not introduce dependency injection frameworks or abstractions for trivial, stable dependencies without a demonstrated benefit.

### 3.4 Constants & Configuration

- Replace unexplained magic numbers and strings with meaningful named constants.
- Keep configuration values in configuration files or typed configuration objects when they vary by environment or deployment.
- Keep fixed implementation details close to the code that owns them.
- Do not centralize every literal in a global constants module.
- Use enums or dedicated types for finite, meaningful sets of domain values.
- Never store secrets in source code, committed configuration files, or test fixtures.
- Validate configuration at startup and fail with a clear, actionable error when required settings are invalid.

## 4. Error Handling & State Safety

### 4.1 Fail Fast

- Validate external inputs at system boundaries, including CLI arguments, configuration, HTTP responses, file content, deserialized JSON, and AI-generated output.
- Reject invalid states explicitly rather than allowing them to propagate.
- Return structured errors for expected operational failures when callers need to handle them.
- Use exceptions for exceptional failures and unexpected conditions.
- Never swallow exceptions silently or catch an exception without handling it meaningfully.
- Do not expose internal stack traces, secrets, or sensitive implementation details to external users.
- Preserve the original exception when adding context to an error.
- Fail safely when a dependency is unavailable or an external response is malformed.

### 4.2 Explicit State Transitions

- Keep state changes localized and easy to audit.
- Prefer immutable state transitions when practical.
- Make state ownership and lifecycle explicit.
- Avoid shared mutable global state and hidden mutation through callbacks or service dependencies.
- Validate state transitions before applying them.
- Use transactions or atomic operations where partial updates would violate correctness.
- Do not introduce state-management frameworks unless the application genuinely needs them.

### 4.3 Resource Management

- Dispose of files, streams, network responses, processes, database connections, and other disposable resources deterministically.
- In C#, use `using` and `await using` where appropriate.
- Propagate cancellation tokens through asynchronous operations that support cancellation.
- Apply explicit timeouts to external operations.
- Avoid unbounded retries, unbounded concurrency, and unbounded memory consumption.
- Ensure failure paths release resources correctly.

## 5. Architecture & Dependency Boundaries

### 5.1 Dependency Direction

- Domain logic must not depend directly on CLI presentation, CI platform APIs, or AI provider implementations.
- Keep external integrations behind small interfaces or explicit adapters when multiple implementations, testing, or boundary isolation justify them.
- Dependencies should point toward stable domain logic, not toward volatile infrastructure details.
- Avoid circular dependencies.
- Avoid service locators and hidden dependency resolution.
- Do not create interfaces for every class by default.

### 5.2 Small, Explicit Contracts

- Public contracts must expose only the information consumers need.
- Avoid exposing internal implementation details.
- Prefer composition over inheritance unless inheritance models a genuine subtype relationship.
- Keep interfaces cohesive and small.
- Use dependency injection where it clarifies ownership and enables meaningful substitution, not as a mandatory ceremony for every object.
- Avoid generic abstractions that have only one trivial implementation and no concrete architectural purpose.

### 5.3 Separation of Concerns

- Keep business rules independent of transport, storage, logging, and presentation.
- Separate parsing, validation, decision-making, and side effects when they have distinct responsibilities.
- Keep configuration, domain models, and external DTOs conceptually separate when their lifecycles differ.
- Avoid unnecessary mapping layers when they provide no meaningful isolation or validation.
- Keep the architecture proportionate to the application's actual complexity.

## 6. API, Serialization & External Data

- Define explicit schemas for data exchanged with external systems.
- Validate deserialized payloads before using them.
- Treat AI model responses as untrusted input, even when structured output or JSON mode is enabled.
- Never assume that a model response is correct merely because it parses successfully.
- Validate identifiers, paths, ranges, enum values, and cross-field constraints.
- Use deterministic serialization where output stability matters.
- Handle missing fields, additional fields, malformed responses, and unsupported schema versions deliberately.
- Keep internal domain models independent of provider-specific response formats.
- Avoid silently coercing invalid values into defaults.

## 7. Security by Default

- Apply least privilege to credentials, filesystem access, network access, and CI permissions.
- Never execute untrusted repository content as part of code review.
- Treat source code, comments, commit messages, filenames, and generated content as untrusted data, not privileged instructions.
- Do not allow external content to override system policy or trigger unauthorized tool use.
- Avoid command injection, path traversal, unsafe deserialization, and accidental secret disclosure.
- Use parameterized queries and safe process invocation APIs.
- Validate file paths against the intended repository boundary.
- Keep security-sensitive behavior explicit, testable, and fail-closed where appropriate.

## 8. Testing & Verification

- Every non-trivial business rule must have automated tests.
- Test observable behavior, edge cases, invalid inputs, and failure paths.
- Prefer deterministic unit tests with controlled dependencies.
- Avoid live network calls, live AI calls, and external service dependencies in the default unit test suite.
- Use integration tests for important boundaries and contracts.
- Do not write tests that merely mirror implementation details.
- Do not weaken assertions or delete tests simply to make a build pass.
- Run relevant tests, analyzers, formatters, and builds after changes.
- A change is not complete until verification succeeds or remaining failures are explicitly reported.

## 9. Language-Specific Enforcement

Apply equivalent rules using each language's native features rather than forcing identical syntax across languages.

### C# / .NET

- Enable nullable reference types.
- Enable analyzers and treat relevant warnings as errors.
- Prefer `var` only when the assigned type is immediately obvious; otherwise use explicit types.
- Prefer `const` and `readonly` where appropriate.
- Use `async`/`await` correctly; avoid `async void` except for required event handlers.
- Pass `CancellationToken` through cancellable asynchronous operations.
- Avoid `.Result` and `.Wait()` on asynchronous operations.
- Use structured logging and deterministic resource disposal.
- Avoid unnecessary LINQ chains when a straightforward loop is clearer or more efficient.
- Use the built-in dependency injection and configuration facilities when they provide sufficient functionality.

### TypeScript

- Enable `strict` and `strictNullChecks`.
- Prohibit implicit `any`; avoid explicit `any`.
- Prefer `unknown` at untrusted boundaries and narrow it through validation.
- Use `readonly` types where appropriate.
- Avoid non-null assertions and unchecked type casts.
- Validate runtime input; TypeScript types alone do not validate external data.
- Use exhaustive checks for discriminated unions.
- Use the `@/` path alias for imports of project-internal modules across directories (for example, `@/domain/User`). Do not use parent-directory traversal imports such as `../domain/User` or `../../domain/User` for these dependencies. Same-directory `./` imports and external package imports are allowed.
- Configure the `@/*` alias in TypeScript (`compilerOptions.paths`) and consistently in the bundler/runtime, test runner, and other tools that resolve modules. A compiler-only alias is insufficient when runtime resolution differs.
- Enforce the project-internal alias convention through existing lint tooling and CI; reject newly introduced parent-directory traversal imports. Do not add a bespoke import-rule engine.

## 10. Automated Enforcement

Enforce rules mechanically wherever practical.

- Configure formatters, compiler checks, linters, and static analyzers.
- Run these checks in CI.
- Fail CI on compilation errors, relevant lint violations, and newly introduced prohibited patterns.
- Use targeted suppressions only when necessary, with a clear justification.
- Prefer existing tooling over custom scripts.
- Do not build a custom rule engine when standard tooling can enforce the requirement.

## 11. Mandatory Workflow for AI-Assisted Changes

Before implementing a change:

1. Inspect the existing architecture and conventions.
2. Identify the smallest implementation that satisfies the requirement.
3. Reuse existing abstractions where appropriate.
4. Identify relevant security, error-handling, and testing requirements.

During implementation:

1. Make focused, incremental changes.
2. Preserve existing behavior unless a change is explicitly required.
3. Avoid unrelated refactoring and speculative features.
4. Keep all changes consistent with this standard.

Before completion:

1. Remove unused code, temporary diagnostics, and accidental artifacts.
2. Run formatting, static analysis, builds, and relevant tests.
3. Review the diff for unnecessary complexity, security issues, and contract violations.
4. Report verification results and any unresolved limitations honestly.

## 12. Definition of Done

Code is complete only when:

- It meets the requested behavior.
- Types and nullability are handled explicitly.
- Errors and side effects are controlled.
- Security boundaries are respected.
- Tests cover meaningful behavior and failure cases.
- Relevant automated checks pass.
- No unnecessary abstractions, dead code, or debug artifacts remain.
- The implementation is the simplest design that meets the actual requirements.

**Final rule:** Correctness over cleverness. Explicit behavior over hidden behavior. Simplicity over speculative flexibility. No abstraction without a reason.

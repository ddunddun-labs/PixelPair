# Security Policy

## Supported versions

Until the first tagged release, security fixes are made on `master`. After releases begin, only the latest released version and current `master` are expected to receive security fixes.

## Security model

PixelPair is a local-first application. Its HTTP server binds to `127.0.0.1`, browser requests are restricted to PixelPair's own origin, and JSON operations that read or write arbitrary local filesystem paths require a per-run local agent token.

The token is a same-user local secret, not a sandbox boundary. A malicious process already running as the same OS user may be able to read PixelPair's local state and files. Layer locking is an editing affordance, not a security boundary. Importing a `.pxp` project replaces the current project state.

When evaluating a report, the intended trust boundary is therefore:

- untrusted websites must not be able to drive the localhost API;
- unauthenticated callers must not be able to use path-based import/export operations;
- malformed project or image inputs must not cause unsafe filesystem access or unintended code execution;
- PixelPair should not expose secrets, local paths, or user data through normal public-facing output.

## Reporting a vulnerability

Please do **not** open a public issue containing vulnerability details, proof-of-concept payloads, tokens, private paths, or user data.

Use GitHub's private vulnerability reporting / security advisory flow for this repository when it is available. If that option is not available, open a minimal public issue asking the maintainers for a private reporting channel, without including the vulnerability details.

A useful report includes:

- affected PixelPair version or commit;
- operating system and runtime environment;
- impact and expected security boundary;
- minimal reproduction steps;
- whether the issue requires a malicious website, local process, crafted image, or crafted `.pxp` file.

We will acknowledge actionable reports as soon as practical and coordinate a fix before public disclosure when appropriate.

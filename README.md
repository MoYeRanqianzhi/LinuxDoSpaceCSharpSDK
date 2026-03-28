# LinuxDoSpace C# SDK

This directory contains a C# SDK implementation for LinuxDoSpace mail stream protocol.

## Scope

- `Client`: one upstream token stream, reconnect loop, full listener, local bind/route/close
- `Suffix`: known suffix constants
- `MailMessage`: parsed mail model
- SDK errors: `LinuxDoSpaceException`, `AuthenticationException`, `StreamException`
- Local binding semantics:
  - exact and regex bindings share one ordered chain
  - first match always receives
  - `allowOverlap=false` stops matching
  - `allowOverlap=true` continues matching

Important:

- `Suffix.LinuxDoSpace` is semantic, not literal
- `Suffix.LinuxDoSpace` now resolves to the current token owner's canonical
  mail namespace: `<owner_username>-mail.linuxdo.space`
- `Suffix.WithSuffix("foo")` resolves to
  `<owner_username>-mailfoo.linuxdo.space`
- active semantic `-mail<suffix>` registrations are synchronized to
  `PUT /v1/token/email/filters`
- the legacy default alias `<owner_username>.linuxdo.space` still matches the
  default semantic binding automatically

## Local Verification Status

Current environment does not have .NET SDK installed, so this SDK was not compiled locally in this session.

## Build (when .NET SDK is available)

```bash
dotnet build
```

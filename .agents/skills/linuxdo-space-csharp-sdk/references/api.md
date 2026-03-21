# API Reference

## Paths

- SDK root: `../../../`
- Project file: `../../../LinuxDoSpaceSdk.csproj`
- Core implementation: `../../../Sdk.cs`
- Consumer README: `../../../README.md`

## Public surface

- Types: `Suffix`, `LinuxDoSpaceException`, `AuthenticationException`, `StreamException`, `MailMessage`, `MailBox`, `Client`
- Client:
  - constructor `Client(...)`
  - `ListenAsync(CancellationToken)`
  - `Bind(...)`
  - `Route(MailMessage)`
  - `CloseAsync()`
  - `DisposeAsync()`
- MailBox:
  - `ListenAsync(CancellationToken)`
  - `CloseAsync()`
  - `DisposeAsync()`
  - properties such as `Mode`, `Suffix`, `AllowOverlap`, `Prefix`, `Pattern`, `Address`, `Closed`

## Semantics

- `Bind(...)` requires exactly one of `prefix` or `pattern`.
- Regex bindings are full-match regexes.
- `Suffix.LinuxDoSpace` is semantic, not literal.
- Mailbox queues activate only while mailbox listen is active.


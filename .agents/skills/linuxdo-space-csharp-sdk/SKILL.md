---
name: linuxdo-space-csharp-sdk
description: Use when writing or fixing C# code that consumes or maintains the LinuxDoSpace C# SDK under sdk/csharp. Use for project-reference integration, Client/ListenAsync usage, mailbox bindings, route semantics, lifecycle/error handling, GitHub Release nupkg guidance, and local validation.
---

# LinuxDoSpace C# SDK

Read [references/consumer.md](references/consumer.md) first for normal SDK usage.
Read [references/api.md](references/api.md) for exact public C# API names.
Read [references/examples.md](references/examples.md) for task-shaped snippets.
Read [references/development.md](references/development.md) only when editing `sdk/csharp`.

## Workflow

1. Prefer the public namespace `LinuxDoSpace`.
2. The SDK root relative to this `SKILL.md` is `../../../`.
3. Preserve these invariants:
   - one `Client` owns one upstream HTTPS stream
   - `ListenAsync(CancellationToken)` is the full-stream consumer entrypoint
   - `Bind(...)` creates mailbox bindings locally
   - mailbox queues activate only while mailbox `ListenAsync(...)` is active
   - `Suffix.LinuxDoSpace` is semantic and resolves after `ready.owner_username`
   - exact and regex bindings share one ordered chain per suffix
   - `allowOverlap=false` stops at first match; `true` continues
   - remote `baseUrl` must use `https://`
4. Keep README, `Sdk.cs`, csproj metadata, and workflows aligned when behavior changes.
5. Validate with the commands in `references/development.md`.

## Do Not Regress

- Do not describe this SDK as already published on a public NuGet feed.
- Do not replace cancellation-token consumption with imaginary timeout APIs.
- Do not add hidden pre-listen mailbox buffering.


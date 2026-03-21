# Consumer Guide

## Integrate

Preferred local integration is a project reference to `LinuxDoSpaceSdk.csproj`.
Current release artifacts are `.nupkg` files on GitHub Release, not a public
NuGet feed.

Import shape:

```csharp
using LinuxDoSpace;
```

## Full stream

```csharp
await using var client = new Client(token);
await foreach (var item in client.ListenAsync(ct))
{
    Console.WriteLine(item.Address);
}
```

## Mailbox binding

```csharp
await using var mailbox = client.Bind(
    prefix: "alice",
    suffix: Suffix.LinuxDoSpace,
    allowOverlap: false
);

await foreach (var item in mailbox.ListenAsync(ct))
{
    Console.WriteLine(item.Subject);
}
```

## Key semantics

- Cancellation is driven by `CancellationToken`, not a built-in numeric timeout.
- `Route(message)` is read-only local matching only.
- Full-stream messages use the first recipient projection address.
- Mailbox messages use matched-recipient projection addresses.


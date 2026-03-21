# Task Templates

## Construct one client

```csharp
await using var client = new Client(token);
```

## Add one catch-all

```csharp
await using var mailbox = client.Bind(
    pattern: ".*",
    suffix: Suffix.LinuxDoSpace,
    allowOverlap: true
);
```

## Route one full-stream message

```csharp
var matches = client.Route(message);
```


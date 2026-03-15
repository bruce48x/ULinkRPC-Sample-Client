# ULinkRPC.Serializer.MemoryPack

MemoryPack based payload serializer for ULinkRPC.

## Install

```bash
dotnet add package ULinkRPC.Serializer.MemoryPack
```

## Usage

```csharp
using ULinkRPC.Serializer.MemoryPack;

var serializer = new MemoryPackRpcSerializer();
```

On `net10.0`, `ULinkRPC.Server` integration also adds:

```csharp
await RpcServerHostBuilder.Create()
    .UseMemoryPack()
    .UseTcp()
    .RunAsync();
```

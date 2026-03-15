# ULinkRPC.Transport.Kcp

KCP transport primitives for ULinkRPC.

## Install

```bash
dotnet add package ULinkRPC.Transport.Kcp
```

## Includes

- `KcpTransport`
- `KcpListener`
- `KcpAcceptResult`
- `KcpServerTransport`
- `UseKcp()` for `RpcServerHostBuilder` on `net10.0`

## Server Usage

```csharp
await RpcServerHostBuilder.Create()
    .UseCommandLine(args)
    .UseMemoryPack()
    .UseKcp(defaultPort: 20000)
    .RunAsync();
```

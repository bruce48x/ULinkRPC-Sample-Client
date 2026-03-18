# ULinkRPC.Transport.Tcp

TCP transport implementations for ULinkRPC.

## Install

```bash
dotnet add package ULinkRPC.Transport.Tcp
```

## Includes

- `TcpTransport` (client)
- `TcpServerTransport` (server)
- `UseTcp()` for `RpcServerHostBuilder` on `net10.0`

## Server Usage

```csharp
await RpcServerHostBuilder.Create()
    .UseCommandLine(args)
    .UseMemoryPack()
    .UseTcp(defaultPort: 20000)
    .RunAsync();
```

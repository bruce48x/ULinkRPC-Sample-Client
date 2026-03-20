# ULinkRPC.Transport.WebSocket

WebSocket client/server transport implementations for ULinkRPC.

## Install

```bash
dotnet add package ULinkRPC.Transport.WebSocket
```

## Includes

- `WsTransport`
- `WsServerTransport`
- `UseWebSocket()` for `RpcServerHostBuilder` on `net10.0`

## Server Usage

```csharp
await RpcServerHostBuilder.Create()
    .UseCommandLine(args)
    .UseJson()
    .UseWebSocket(defaultPort: 20000, path: "/ws")
    .RunAsync();
```

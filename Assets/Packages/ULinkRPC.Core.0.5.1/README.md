# ULinkRPC.Core

Shared abstractions and wire-level contracts for ULinkRPC.

`ULinkRPC.Core` does not depend on concrete serializer or transport implementations.
Use it together with `ULinkRPC.Client` / `ULinkRPC.Server` and optional serializer/transport packages.

## Install

```bash
dotnet add package ULinkRPC.Core
```

## Includes

- RPC attributes: `RpcServiceAttribute`, `RpcMethodAttribute`
- Transport and serializer abstractions: `ITransport`, `IRpcSerializer`, `IRpcClient`
- Envelopes and status types: `RpcRequestEnvelope`, `RpcResponseEnvelope`, `RpcStatus`, `RpcVoid`
- Envelope codec: `RpcEnvelopeCodec`
- Shared framing/security helpers: `LengthPrefix`, `TransportFrameCodec`, `TransformingTransport`, `TransportSecurityConfig`

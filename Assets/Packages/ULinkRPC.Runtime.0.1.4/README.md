# ULinkRPC.Runtime

ULinkRPC runtime library for Unity and .NET.

Targets:
- netstandard2.1 (Unity 2022 LTS / IL2CPP / HybridCLR)
- net8.0 (server)

Notes:
- Contracts are not shipped as NuGet; use your own Contracts Git repo.
- Transports: TCP / WebSocket / KCP.
- Optional compression + encryption at transport level.

Install:
```
dotnet add package ULinkRPC.Runtime
```

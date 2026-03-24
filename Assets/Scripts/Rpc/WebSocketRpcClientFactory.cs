#nullable enable

using System;
using ULinkRPC.Client;
using ULinkRPC.Core;
using ULinkRPC.Serializer.MemoryPack;
using ULinkRPC.Transport.WebSocket;

namespace Rpc
{
    public static class WebSocketRpcClientFactory
    {
        public static RpcClient Create(string host, int port, string path, RpcClient.RpcCallbackBindings callbacks)
        {
            return new RpcClient(
                new RpcClientOptions(
                    new WsTransport(BuildUrl(host, port, path)),
                    new MemoryPackRpcSerializer())
                {
                    KeepAlive = new RpcKeepAliveOptions
                    {
                        Enabled = true,
                        Interval = TimeSpan.FromSeconds(5),
                        Timeout = TimeSpan.FromSeconds(15)
                    }
                },
                callbacks);
        }

        public static string BuildUrl(string host, int port, string path)
        {
            var normalizedPath = string.IsNullOrWhiteSpace(path)
                ? "/ws"
                : path.StartsWith("/", StringComparison.Ordinal) ? path : "/" + path;

            if (host.StartsWith("ws://", StringComparison.OrdinalIgnoreCase) ||
                host.StartsWith("wss://", StringComparison.OrdinalIgnoreCase))
            {
                return $"{host.TrimEnd('/')}{normalizedPath}";
            }

            return $"ws://{host}:{port}{normalizedPath}";
        }
    }
}



using System;
using System.Threading;
using System.Threading.Tasks;
using Shared.Interfaces;
using Shared.Interfaces.Runtime.Generated;
using ULinkRPC.Client;
using ULinkRPC.Serializer.MemoryPack;
using ULinkRPC.Transport.Tcp;
using UnityEngine;

namespace Rpc.Testing
{
    public sealed class RpcCaller : MonoBehaviour
    {
        public string Host = "127.0.0.1";
        public int Port = 20000;

        private RpcClient? _client;
        private CancellationTokenSource? _cts;
        private IMyFirstService? _service;

        private async void Start()
        {
            await ConnectAndTestAsync();
        }

        private async void OnDestroy()
        {
            await CleanupAsync();
        }

        private async Task ConnectAndTestAsync()
        {
            if (_client is not null)
            {
                Debug.LogWarning("RpcCaller already connected.");
                return;
            }

            try
            {
                _cts = new CancellationTokenSource();

                // Create TCP transport
                var transport = new TcpTransport(Host, Port);

                // Create MemoryPack serializer
                var serializer = new MemoryPackRpcSerializer();

                // Create RPC client
                _client = new RpcClient(transport, serializer);

                await _client.StartAsync(_cts.Token);
                Debug.Log("Connected!");

                // Get service proxy
                var rpcApi = _client.CreateRpcApi();
                _service = rpcApi.Game.MyFirst;

                // Call RPC method: Sum(10, 20)
                int result = await _service.SumAsync(10, 20);
                Debug.Log($"Sum(10, 20) = {result}");
            }
            catch (Exception ex)
            {
                Debug.LogError($"RPC error: {ex}");
            }
        }

        private async Task CleanupAsync()
        {
            if (_client is null)
                return;

            try
            {
                await _client.DisposeAsync();
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"RPC cleanup error: {ex}");
            }
            finally
            {
                _client = null;
                _service = null;
                if (_cts is not null)
                {
                    _cts.Dispose();
                    _cts = null;
                }
            }
        }
    }
}

#nullable enable

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Shared.Interfaces;
using ULinkRPC.Client;
using ULinkRPC.Serializer.MemoryPack;
using ULinkRPC.Transport.WebSocket;
using UnityEngine;

namespace Rpc.Testing
{
    [Serializable]
    public sealed class RpcEndpointSettings
    {
        public string Host = "127.0.0.1";
        public int Port = 20000;
        public string Path = "/ws";

        public static RpcEndpointSettings CreateWebSocket(string host, int port, string path = "/ws")
        {
            return new RpcEndpointSettings
            {
                Host = host,
                Port = port,
                Path = path
            };
        }

        public string GetWebSocketUrl()
        {
            var normalizedPath = string.IsNullOrWhiteSpace(Path) ? "/ws" :
                Path.StartsWith("/", StringComparison.Ordinal) ? Path : "/" + Path;

            if (Host.StartsWith("ws://", StringComparison.OrdinalIgnoreCase) ||
                Host.StartsWith("wss://", StringComparison.OrdinalIgnoreCase))
                return $"{Host.TrimEnd('/')}{normalizedPath}";

            return $"ws://{Host}:{Port}{normalizedPath}";
        }
    }

    public sealed class RpcConnectionTester : MonoBehaviour
    {
        [SerializeField]
        private RpcEndpointSettings _endpoint = RpcEndpointSettings.CreateWebSocket("127.0.0.1", 20000);

        [Header("Login")] public string Account = "a";
        public string Password = "b";

        public float RequestIntervalSeconds = 1f;
        public bool AutoConnect = true;
        private readonly RpcClient.RpcCallbackBindings _callbacks;

        private readonly CancellationTokenSource _cts = new();
        private bool _cleanupStarted;
        private RpcClient? _connection;
        private IPlayerService? _player;
        private Task? _pollingTask;
        private bool _stopped;

        public RpcConnectionTester()
        {
            _callbacks = new RpcClient.RpcCallbackBindings();
            _callbacks.Add(new PlayerCallbacks(this));
        }

        private async void Start()
        {
            if (!AutoConnect)
                return;

            await ConnectAndTestAsync();
        }

        private void OnDisable()
        {
            BeginShutdown();
        }

        private void OnDestroy()
        {
            BeginShutdown();
            _cts.Dispose();
        }

        [ContextMenu("Connect And Test")]
        public async Task ConnectAndTestAsync()
        {
            if (_cleanupStarted || _connection is not null)
                return;

            Debug.Log($"[WS] Connecting to {_endpoint.GetWebSocketUrl()}");

            try
            {
                _connection = WebSocketRpcClientFactory.Create(_endpoint.Host, _endpoint.Port, _endpoint.Path, _callbacks);
                await _connection.ConnectAsync(_cts.Token);
                _connection.Disconnected += OnDisconnected;
                _player = _connection.Api.Shared.Player;

                var reply = await _player.LoginAsync(new LoginRequest
                {
                    Account = Account,
                    Password = Password
                });

                Debug.Log($"[WS] Login ok: account={Account}, code={reply.Code}, token={reply.Token}");
                _pollingTask = RunPollingAsync();
            }
            catch (Exception ex)
            {
                Debug.LogError($"[WS] Connect failed: {ex}");
                await CleanupAsync();
            }
        }

        private async Task RunPollingAsync()
        {
            var interval = Mathf.Max(0.1f, RequestIntervalSeconds);

            while (!_cts.IsCancellationRequested && !_stopped)
                try
                {
                    await _player!.Move(new MoveRequest { Direction = 1, PlayerId = Account });
                    if (_cts.IsCancellationRequested || _stopped)
                        return;

                    Debug.Log($"{Account} Moved");
                    await Task.Delay(TimeSpan.FromSeconds(interval), _cts.Token);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"[WS] Polling failed: {ex.Message}");
                    return;
                }
        }

        private void HandleNotify(string message)
        {
            if (_stopped)
                return;

            Debug.Log($"[WS] Push: {message}");
        }

        private void BeginShutdown()
        {
            if (_cleanupStarted)
                return;

            _cleanupStarted = true;
            _stopped = true;
            _cts.Cancel();

            if (_connection is not null)
                _connection.Disconnected -= OnDisconnected;

            _ = CleanupAsync();
        }

        private async Task CleanupAsync()
        {
            if (_pollingTask is not null)
                try
                {
                    await _pollingTask;
                }
                catch (OperationCanceledException)
                {
                }

            if (_connection is not null)
            {
                await _connection.DisposeAsync();
                _connection = null;
            }
        }

        private void OnDisconnected(Exception? ex)
        {
            if (_stopped)
                return;

            _stopped = true;
            _connection = null;

            if (ex is null)
                Debug.Log("[WS] Disconnected.");
            else
                Debug.LogWarning($"[WS] Disconnected: {ex.Message}");
        }

        private sealed class PlayerCallbacks : RpcClient.PlayerCallbackBase
        {
            private readonly RpcConnectionTester _owner;

            public PlayerCallbacks(RpcConnectionTester owner)
            {
                _owner = owner;
            }

            public override void OnMove(List<PlayerPosition> playerPositions)
            {
                Debug.Log($"OnMove {playerPositions.Count}");
            }
        }
    }
}



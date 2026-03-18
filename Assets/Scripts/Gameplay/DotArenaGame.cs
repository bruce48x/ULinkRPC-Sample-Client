#nullable enable

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Rpc.Generated;
using Shared.Interfaces;
using ULinkRPC.Client;
using ULinkRPC.Serializer.MemoryPack;
using ULinkRPC.Transport.Tcp;
using UnityEngine;

namespace SampleClient.Gameplay
{
    public static class DotArenaBootstrap
    {
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void EnsureGame()
        {
            if (UnityEngine.Object.FindObjectOfType<DotArenaGame>() != null)
            {
                return;
            }

            var bootstrap = new GameObject(nameof(DotArenaGame));
            bootstrap.AddComponent<DotArenaGame>();
        }
    }

    public sealed class DotArenaGame : MonoBehaviour, IPlayerCallback
    {
        private const int GridSize = 10;
        private const float WorldHalfSize = 5f;
        private const float LogicalMin = 0f;
        private const float LogicalMax = 9f;
        private const float LogicalCenterOffset = 4.5f;

        private static readonly Color BoardColor = new(0.08f, 0.1f, 0.14f, 1f);
        private static readonly Color GridColor = new(0.75f, 0.86f, 0.94f, 0.14f);
        private static readonly Color BorderColor = new(0.9f, 0.96f, 1f, 0.24f);
        private static readonly Color[] RemotePalette =
        {
            new(0.25f, 0.96f, 0.61f, 1f),
            new(1f, 0.38f, 0.45f, 1f),
            new(1f, 0.75f, 0.2f, 1f),
            new(0.32f, 0.75f, 1f, 1f),
            new(0.78f, 0.48f, 1f, 1f),
            new(1f, 0.55f, 0.3f, 1f)
        };

        [SerializeField] private string _host = "127.0.0.1";
        [SerializeField] private int _port = 20000;
        [SerializeField] private string _account = "a";
        [SerializeField] private string _password = "b";
        [SerializeField] private float _moveIntervalSeconds = 0.08f;

        private readonly Dictionary<string, DotView> _views = new();
        private readonly object _snapshotLock = new();
        private readonly CancellationTokenSource _cts = new();

        private List<PlayerPosition>? _pendingSnapshot;
        private RpcClient? _connection;
        private IPlayerService? _playerService;
        private Sprite _pixelSprite = null!;
        private float _nextMoveAt;
        private bool _isConnected;
        private bool _isConnecting;
        private string _status = "Connecting...";

        private async void Start()
        {
            ConfigureCamera();
            BuildArena();
            await ConnectAsync();
        }

        private void Update()
        {
            ApplyPendingSnapshot();
            HandleInput();
        }

        private void OnDestroy()
        {
            _cts.Cancel();
            _ = DisposeConnectionAsync();
            _cts.Dispose();
        }

        public void OnMove(List<PlayerPosition> playerPositions)
        {
            var snapshot = new List<PlayerPosition>(playerPositions.Count);
            foreach (var playerPosition in playerPositions)
            {
                snapshot.Add(new PlayerPosition
                {
                    PlayerId = playerPosition.PlayerId,
                    Position = playerPosition.Position
                });
            }

            lock (_snapshotLock)
            {
                _pendingSnapshot = snapshot;
            }
        }

        private async Task ConnectAsync()
        {
            if (_isConnecting || _isConnected)
            {
                return;
            }

            _isConnecting = true;
            _status = $"Connecting {_host}:{_port}";

            try
            {
                var callbacks = new RpcClient.RpcCallbackBindings();
                callbacks.Add(this);

                _connection = new RpcClient(
                    new RpcClientOptions(
                        new TcpTransport(_host, _port),
                        new MemoryPackRpcSerializer()),
                    callbacks);

                _connection.Disconnected += OnDisconnected;
                await _connection.ConnectAsync(_cts.Token);

                _playerService = _connection.Api.Shared.Player;
                var reply = await _playerService.LoginAsync(new LoginRequest
                {
                    Account = _account,
                    Password = _password
                });

                if (reply.Code != 0)
                {
                    _status = $"Login failed, code={reply.Code}";
                    await DisposeConnectionAsync();
                    return;
                }

                _isConnected = true;
                _status = $"Connected as {_account}";
            }
            catch (OperationCanceledException)
            {
                _status = "Connection canceled";
            }
            catch (Exception ex)
            {
                _status = $"Connect failed: {ex.Message}";
                await DisposeConnectionAsync();
            }
            finally
            {
                _isConnecting = false;
            }
        }

        private void HandleInput()
        {
            if (!_isConnected || _playerService == null || Time.time < _nextMoveAt)
            {
                return;
            }

            var direction = ReadDirection();
            if (direction == 0)
            {
                return;
            }

            _nextMoveAt = Time.time + Mathf.Max(0.02f, _moveIntervalSeconds);
            _ = SendMoveAsync(direction);
        }

        private async Task SendMoveAsync(int direction)
        {
            if (_playerService == null)
            {
                return;
            }

            try
            {
                await _playerService.Move(new MoveRequest
                {
                    PlayerId = _account,
                    Direction = direction
                });
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                _status = $"Move failed: {ex.Message}";
            }
        }

        private void ApplyPendingSnapshot()
        {
            List<PlayerPosition>? snapshot = null;
            lock (_snapshotLock)
            {
                if (_pendingSnapshot == null)
                {
                    return;
                }

                snapshot = _pendingSnapshot;
                _pendingSnapshot = null;
            }

            var activeIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (var playerPosition in snapshot)
            {
                activeIds.Add(playerPosition.PlayerId);

                if (!_views.TryGetValue(playerPosition.PlayerId, out var view))
                {
                    view = CreateView(playerPosition.PlayerId);
                    _views.Add(playerPosition.PlayerId, view);
                }

                view.SetPosition(ToWorldPosition(playerPosition.Position));
            }

            var removedIds = new List<string>();
            foreach (var entry in _views)
            {
                if (!activeIds.Contains(entry.Key))
                {
                    removedIds.Add(entry.Key);
                }
            }

            foreach (var removedId in removedIds)
            {
                Destroy(_views[removedId].Root);
                _views.Remove(removedId);
            }
        }

        private void OnDisconnected(Exception? ex)
        {
            _isConnected = false;
            _playerService = null;
            _status = ex == null ? "Disconnected" : $"Disconnected: {ex.Message}";
        }

        private async Task DisposeConnectionAsync()
        {
            if (_connection == null)
            {
                return;
            }

            var connection = _connection;
            _connection = null;
            _playerService = null;
            _isConnected = false;

            try
            {
                connection.Disconnected -= OnDisconnected;
                await connection.DisposeAsync();
            }
            catch
            {
            }
        }

        private void OnGUI()
        {
            const float width = 330f;
            const float height = 132f;

            var boxRect = new Rect(16f, 16f, width, height);
            var contentRect = new Rect(28f, 24f, width - 24f, height - 16f);

            var previousColor = GUI.color;
            GUI.color = new Color(0.04f, 0.06f, 0.08f, 0.88f);
            GUI.Box(boxRect, GUIContent.none);
            GUI.color = previousColor;

            var titleStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = 16,
                fontStyle = FontStyle.Bold,
                normal = { textColor = Color.white }
            };

            var bodyStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = 12,
                normal = { textColor = new Color(0.86f, 0.91f, 0.96f, 1f) }
            };

            GUI.Label(new Rect(contentRect.x, contentRect.y, contentRect.width, 24f), "Network Dot Arena", titleStyle);
            GUI.Label(new Rect(contentRect.x, contentRect.y + 24f, contentRect.width, 18f), $"状态: {_status}", bodyStyle);
            GUI.Label(new Rect(contentRect.x, contentRect.y + 44f, contentRect.width, 18f), $"账号: {_account}   地址: {_host}:{_port}", bodyStyle);
            GUI.Label(new Rect(contentRect.x, contentRect.y + 64f, contentRect.width, 18f), $"同步角色数: {_views.Count}", bodyStyle);
            GUI.Label(new Rect(contentRect.x, contentRect.y + 84f, contentRect.width, 18f), "逻辑坐标范围: 0..9", bodyStyle);
            GUI.Label(new Rect(contentRect.x, contentRect.y + 104f, contentRect.width, 18f), "W/A/S/D 只发送移动指令，位置由服务器推送。", bodyStyle);
        }

        private void ConfigureCamera()
        {
            var cameraObject = GameObject.FindWithTag("MainCamera");
            var mainCamera = cameraObject != null ? cameraObject.GetComponent<Camera>() : null;

            if (mainCamera == null)
            {
                cameraObject = new GameObject("Main Camera");
                cameraObject.tag = "MainCamera";
                mainCamera = cameraObject.AddComponent<Camera>();
                cameraObject.AddComponent<AudioListener>();
            }

            mainCamera.orthographic = true;
            mainCamera.orthographicSize = WorldHalfSize + 1.5f;
            mainCamera.backgroundColor = new Color(0.02f, 0.03f, 0.05f, 1f);
            mainCamera.clearFlags = CameraClearFlags.SolidColor;
            mainCamera.transform.position = new Vector3(0f, 0f, -10f);
            mainCamera.transform.rotation = Quaternion.identity;
        }

        private void BuildArena()
        {
            _pixelSprite = CreatePixelSprite();

            var arenaRoot = new GameObject("ArenaRoot");
            arenaRoot.transform.SetParent(transform, false);

            CreateRect(arenaRoot.transform, "Board", Vector2.zero, new Vector2(WorldHalfSize * 2f, WorldHalfSize * 2f), BoardColor, -20);

            for (var i = 0; i <= GridSize; i++)
            {
                var axis = -WorldHalfSize + i;
                CreateRect(arenaRoot.transform, $"Vertical-{i}", new Vector2(axis, 0f), new Vector2(0.05f, WorldHalfSize * 2f), GridColor, -10);
                CreateRect(arenaRoot.transform, $"Horizontal-{i}", new Vector2(0f, axis), new Vector2(WorldHalfSize * 2f, 0.05f), GridColor, -10);
            }

            CreateRect(arenaRoot.transform, "TopBorder", new Vector2(0f, WorldHalfSize), new Vector2((WorldHalfSize * 2f) + 0.18f, 0.18f), BorderColor, -5);
            CreateRect(arenaRoot.transform, "BottomBorder", new Vector2(0f, -WorldHalfSize), new Vector2((WorldHalfSize * 2f) + 0.18f, 0.18f), BorderColor, -5);
            CreateRect(arenaRoot.transform, "LeftBorder", new Vector2(-WorldHalfSize, 0f), new Vector2(0.18f, (WorldHalfSize * 2f) + 0.18f), BorderColor, -5);
            CreateRect(arenaRoot.transform, "RightBorder", new Vector2(WorldHalfSize, 0f), new Vector2(0.18f, (WorldHalfSize * 2f) + 0.18f), BorderColor, -5);
        }

        private int ReadDirection()
        {
            if (Input.GetKey(KeyCode.A))
            {
                return 3;
            }

            if (Input.GetKey(KeyCode.D))
            {
                return 1;
            }

            if (Input.GetKey(KeyCode.S))
            {
                return 2;
            }

            if (Input.GetKey(KeyCode.W))
            {
                return 4;
            }

            return 0;
        }

        private DotView CreateView(string playerId)
        {
            var viewRoot = new GameObject(playerId);
            viewRoot.transform.SetParent(transform, false);

            var renderer = viewRoot.AddComponent<SpriteRenderer>();
            renderer.sprite = _pixelSprite;
            renderer.color = ResolveColor(playerId);
            renderer.sortingOrder = 20;

            var size = playerId == _account ? 0.42f : 0.34f;
            viewRoot.transform.localScale = new Vector3(size, size, 1f);

            return new DotView(viewRoot);
        }

        private static Vector2 ToWorldPosition(Vector2 logicalPosition)
        {
            var x = Mathf.Clamp(logicalPosition.x, LogicalMin, LogicalMax) - LogicalCenterOffset;
            var y = Mathf.Clamp(logicalPosition.y, LogicalMin, LogicalMax) - LogicalCenterOffset;
            return new Vector2(x, y);
        }

        private Color ResolveColor(string playerId)
        {
            if (playerId == _account)
            {
                return RemotePalette[0];
            }

            var index = Mathf.Abs(playerId.GetHashCode()) % (RemotePalette.Length - 1);
            return RemotePalette[index + 1];
        }

        private void CreateRect(Transform parent, string objectName, Vector2 position, Vector2 size, Color color, int sortingOrder)
        {
            var rectangle = new GameObject(objectName);
            rectangle.transform.SetParent(parent, false);
            rectangle.transform.localPosition = new Vector3(position.x, position.y, 0f);
            rectangle.transform.localScale = new Vector3(size.x, size.y, 1f);

            var renderer = rectangle.AddComponent<SpriteRenderer>();
            renderer.sprite = _pixelSprite;
            renderer.color = color;
            renderer.sortingOrder = sortingOrder;
        }

        private static Sprite CreatePixelSprite()
        {
            var texture = new Texture2D(1, 1, TextureFormat.RGBA32, false)
            {
                filterMode = FilterMode.Point,
                wrapMode = TextureWrapMode.Clamp,
                hideFlags = HideFlags.HideAndDontSave
            };

            texture.SetPixel(0, 0, Color.white);
            texture.Apply();

            return Sprite.Create(texture, new Rect(0f, 0f, 1f, 1f), new Vector2(0.5f, 0.5f), 1f);
        }

        private sealed class DotView
        {
            public DotView(GameObject root)
            {
                Root = root;
            }

            public GameObject Root { get; }

            public void SetPosition(Vector2 position)
            {
                Root.transform.position = new Vector3(position.x, position.y, 0f);
            }
        }
    }
}


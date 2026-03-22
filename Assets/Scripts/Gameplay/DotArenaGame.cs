#nullable enable

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Shared.Interfaces;
using ULinkRPC.Client;
using UnityEngine;
using Object = UnityEngine.Object;

namespace SampleClient.Gameplay
{
    public static class DotArenaBootstrap
    {
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void EnsureGame()
        {
            if (Object.FindObjectOfType<DotArenaGame>() != null) return;

            var bootstrap = new GameObject(nameof(DotArenaGame));
            bootstrap.AddComponent<DotArenaGame>();
        }
    }

    public sealed class DotArenaGame : MonoBehaviour, IPlayerCallback
    {
        private const float ArenaHalfSize = 10f;
        private const float ArenaVisualPadding = 1.8f;
        private const float PlayerSize = 0.9f;
        private const float InputSendIntervalSeconds = 0.05f;
        private const float InterpolationDurationSeconds = 0.1f;

        private static readonly Color BackgroundColor = new(0.02f, 0.03f, 0.05f, 1f);
        private static readonly Color BoardColor = new(0.08f, 0.1f, 0.14f, 1f);
        private static readonly Color GridColor = new(0.75f, 0.86f, 0.94f, 0.1f);
        private static readonly Color BorderColor = new(1f, 0.84f, 0.31f, 0.24f);
        private static readonly Color DangerColor = new(1f, 0.24f, 0.24f, 0.08f);

        private static readonly Color[] RemotePalette =
        {
            new(0.2f, 0.96f, 0.67f, 1f),
            new(1f, 0.42f, 0.48f, 1f),
            new(1f, 0.74f, 0.18f, 1f),
            new(0.33f, 0.76f, 1f, 1f),
            new(0.88f, 0.49f, 1f, 1f),
            new(1f, 0.61f, 0.3f, 1f)
        };

        [SerializeField] private string _host = "127.0.0.1";
        [SerializeField] private int _port = 20000;
        [SerializeField] private string _path = "/ws";
        [SerializeField] private string _account = "a";
        [SerializeField] private string _password = "b";

        private readonly CancellationTokenSource _cts = new();
        private readonly object _callbackLock = new();
        private readonly Dictionary<string, DotView> _views = new(StringComparer.Ordinal);
        private readonly Dictionary<string, PlayerRenderState> _renderStates = new(StringComparer.Ordinal);

        private RpcClient? _connection;
        private IPlayerService? _playerService;
        private string _localPlayerId = string.Empty;
        private bool _isConnected;
        private bool _isConnecting;
        private int _inputTick;
        private bool _dashQueued;
        private float _nextInputAt;

        private WorldState? _pendingWorldState;
        private readonly Queue<PlayerDead> _pendingDeaths = new();
        private MatchEnd? _pendingMatchEnd;

        private Sprite _pixelSprite = null!;
        private string _status = "Connecting...";
        private string _eventMessage = "等待玩家加入";
        private float _eventMessageUntil;
        private int _lastWorldTick = -1;

        private async void Start()
        {
            ConfigureCamera();
            BuildArena();
            await ConnectAsync();
        }

        private void Update()
        {
            CaptureInputIntent();
            ApplyPendingCallbacks();
            UpdateViews();
            HandleInput();
        }

        private void OnDestroy()
        {
            _cts.Cancel();
            _ = DisposeConnectionAsync();
            _cts.Dispose();
        }

        private void OnGUI()
        {
            const float width = 400f;
            const float height = 160f;

            var boxRect = new Rect(16f, 16f, width, height);
            var contentRect = new Rect(28f, 24f, width - 24f, height - 16f);

            var previousColor = GUI.color;
            GUI.color = new Color(0.04f, 0.06f, 0.08f, 0.9f);
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

            GUI.Label(new Rect(contentRect.x, contentRect.y, contentRect.width, 24f), "ULinkRPC Dot Arena", titleStyle);
            GUI.Label(new Rect(contentRect.x, contentRect.y + 24f, contentRect.width, 18f), $"状态: {_status}", bodyStyle);
            GUI.Label(new Rect(contentRect.x, contentRect.y + 44f, contentRect.width, 18f),
                $"玩家: {(_localPlayerId.Length > 0 ? _localPlayerId : _account)}", bodyStyle);
            GUI.Label(new Rect(contentRect.x, contentRect.y + 64f, contentRect.width, 18f),
                $"服务端 Tick: {_lastWorldTick}   同步人数: {_views.Count}", bodyStyle);
            GUI.Label(new Rect(contentRect.x, contentRect.y + 84f, contentRect.width, 18f),
                $"地址: {Rpc.WebSocketRpcClientFactory.BuildUrl(_host, _port, _path)}", bodyStyle);
            GUI.Label(new Rect(contentRect.x, contentRect.y + 104f, contentRect.width, 18f),
                "W/A/S/D 移动, Space 冲刺。客户端只发输入，位置以服务端广播为准。", bodyStyle);
            GUI.Label(new Rect(contentRect.x, contentRect.y + 124f, contentRect.width, 18f),
                $"事件: {GetCurrentEventMessage()}", bodyStyle);
        }

        public void OnWorldState(WorldState worldState)
        {
            lock (_callbackLock)
            {
                _pendingWorldState = CloneWorldState(worldState);
            }
        }

        public void OnPlayerDead(PlayerDead deadEvent)
        {
            lock (_callbackLock)
            {
                _pendingDeaths.Enqueue(new PlayerDead
                {
                    PlayerId = deadEvent.PlayerId,
                    Tick = deadEvent.Tick
                });
            }
        }

        public void OnMatchEnd(MatchEnd matchEnd)
        {
            lock (_callbackLock)
            {
                _pendingMatchEnd = new MatchEnd
                {
                    WinnerPlayerId = matchEnd.WinnerPlayerId,
                    Tick = matchEnd.Tick
                };
            }
        }

        private async Task ConnectAsync()
        {
            if (_isConnecting || _isConnected) return;

            _isConnecting = true;
            _status = $"Connecting {Rpc.WebSocketRpcClientFactory.BuildUrl(_host, _port, _path)}";

            try
            {
                var callbacks = new RpcClient.RpcCallbackBindings();
                callbacks.Add(this);

                _connection = Rpc.WebSocketRpcClientFactory.Create(_host, _port, _path, callbacks);
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

                _localPlayerId = string.IsNullOrWhiteSpace(reply.PlayerId) ? _account : reply.PlayerId;
                _isConnected = true;
                _status = $"Connected as {_localPlayerId}";
                PushEvent("等待其他玩家加入");
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

        private void CaptureInputIntent()
        {
            if (Input.GetKeyDown(KeyCode.Space))
            {
                _dashQueued = true;
            }
        }

        private void ApplyPendingCallbacks()
        {
            WorldState? worldState = null;
            MatchEnd? matchEnd = null;
            var deadEvents = new List<PlayerDead>();

            lock (_callbackLock)
            {
                if (_pendingWorldState != null)
                {
                    worldState = _pendingWorldState;
                    _pendingWorldState = null;
                }

                while (_pendingDeaths.Count > 0)
                {
                    deadEvents.Add(_pendingDeaths.Dequeue());
                }

                if (_pendingMatchEnd != null)
                {
                    matchEnd = _pendingMatchEnd;
                    _pendingMatchEnd = null;
                }
            }

            if (worldState != null)
            {
                ApplyWorldState(worldState);
            }

            foreach (var deadEvent in deadEvents)
            {
                HandleDeadEvent(deadEvent);
            }

            if (matchEnd != null)
            {
                HandleMatchEnd(matchEnd);
            }
        }

        private void ApplyWorldState(WorldState worldState)
        {
            if (worldState.Tick < _lastWorldTick)
            {
                return;
            }

            _lastWorldTick = worldState.Tick;
            var activeIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (var player in worldState.Players)
            {
                activeIds.Add(player.PlayerId);

                if (!_views.TryGetValue(player.PlayerId, out var view))
                {
                    view = CreateView(player.PlayerId);
                    _views.Add(player.PlayerId, view);
                }

                if (!_renderStates.TryGetValue(player.PlayerId, out var renderState))
                {
                    renderState = new PlayerRenderState();
                    _renderStates.Add(player.PlayerId, renderState);
                }

                var currentPosition = view.GetPosition();
                var targetPosition = new Vector2(player.X, player.Y);
                renderState.PreviousPosition = currentPosition;
                renderState.TargetPosition = targetPosition;
                renderState.ReceivedAt = Time.time;
                renderState.Alive = player.Alive;
                renderState.State = player.State;

                view.ApplyPresentation(player.PlayerId == _localPlayerId, ResolveColor(player.PlayerId), player.State, player.Alive);
                if (_views.Count >= 2 && worldState.Players.Exists(static p => p.Alive))
                {
                    _eventMessage = "对局进行中";
                }
            }

            var removedIds = new List<string>();
            foreach (var playerId in _views.Keys)
            {
                if (!activeIds.Contains(playerId))
                {
                    removedIds.Add(playerId);
                }
            }

            foreach (var removedId in removedIds)
            {
                Destroy(_views[removedId].Root);
                _views.Remove(removedId);
                _renderStates.Remove(removedId);
            }
        }

        private void HandleDeadEvent(PlayerDead deadEvent)
        {
            if (_renderStates.TryGetValue(deadEvent.PlayerId, out var renderState))
            {
                renderState.Alive = false;
                renderState.State = PlayerLifeState.Dead;
            }

            if (_views.TryGetValue(deadEvent.PlayerId, out var view))
            {
                view.ApplyPresentation(deadEvent.PlayerId == _localPlayerId, ResolveColor(deadEvent.PlayerId), PlayerLifeState.Dead, false);
            }

            PushEvent(deadEvent.PlayerId == _localPlayerId
                ? "你被淘汰了"
                : $"{deadEvent.PlayerId} 被淘汰");
        }

        private void HandleMatchEnd(MatchEnd matchEnd)
        {
            PushEvent(matchEnd.WinnerPlayerId == _localPlayerId
                ? "本局胜利"
                : $"胜者: {matchEnd.WinnerPlayerId}");
        }

        private void UpdateViews()
        {
            foreach (var entry in _views)
            {
                if (!_renderStates.TryGetValue(entry.Key, out var renderState))
                {
                    continue;
                }

                var elapsed = Mathf.Clamp01((Time.time - renderState.ReceivedAt) / InterpolationDurationSeconds);
                var smoothed = elapsed * elapsed * (3f - (2f * elapsed));
                var position = Vector2.Lerp(renderState.PreviousPosition, renderState.TargetPosition, smoothed);
                entry.Value.SetPosition(position);
            }
        }

        private void HandleInput()
        {
            if (!_isConnected || _playerService == null || Time.time < _nextInputAt)
            {
                return;
            }

            _nextInputAt = Time.time + InputSendIntervalSeconds;

            var move = ReadMoveVector();
            var dash = _dashQueued;
            _dashQueued = false;

            _ = SendInputAsync(move, dash);
        }

        private async Task SendInputAsync(Vector2 move, bool dash)
        {
            if (_playerService == null)
            {
                return;
            }

            try
            {
                await _playerService.SubmitInput(new InputMessage
                {
                    PlayerId = _localPlayerId,
                    MoveX = move.x,
                    MoveY = move.y,
                    Dash = dash,
                    Tick = ++_inputTick
                });
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                _status = $"Input failed: {ex.Message}";
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
            if (_connection == null) return;

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
            mainCamera.orthographicSize = ArenaHalfSize + ArenaVisualPadding;
            mainCamera.backgroundColor = BackgroundColor;
            mainCamera.clearFlags = CameraClearFlags.SolidColor;
            mainCamera.transform.position = new Vector3(0f, 0f, -10f);
            mainCamera.transform.rotation = Quaternion.identity;
        }

        private void BuildArena()
        {
            _pixelSprite = CreatePixelSprite();

            var arenaRoot = new GameObject("ArenaRoot");
            arenaRoot.transform.SetParent(transform, false);

            CreateRect(arenaRoot.transform, "DangerZone", Vector2.zero,
                new Vector2((ArenaHalfSize + 1f) * 2f, (ArenaHalfSize + 1f) * 2f), DangerColor, -30);

            CreateRect(arenaRoot.transform, "Board", Vector2.zero, new Vector2(ArenaHalfSize * 2f, ArenaHalfSize * 2f),
                BoardColor, -20);

            for (var i = -8; i <= 8; i += 2)
            {
                CreateRect(arenaRoot.transform, $"Vertical-{i}", new Vector2(i, 0f),
                    new Vector2(0.05f, ArenaHalfSize * 2f), GridColor, -10);
                CreateRect(arenaRoot.transform, $"Horizontal-{i}", new Vector2(0f, i),
                    new Vector2(ArenaHalfSize * 2f, 0.05f), GridColor, -10);
            }

            CreateRect(arenaRoot.transform, "TopBorder", new Vector2(0f, ArenaHalfSize),
                new Vector2(ArenaHalfSize * 2f + 0.18f, 0.18f), BorderColor, -5);
            CreateRect(arenaRoot.transform, "BottomBorder", new Vector2(0f, -ArenaHalfSize),
                new Vector2(ArenaHalfSize * 2f + 0.18f, 0.18f), BorderColor, -5);
            CreateRect(arenaRoot.transform, "LeftBorder", new Vector2(-ArenaHalfSize, 0f),
                new Vector2(0.18f, ArenaHalfSize * 2f + 0.18f), BorderColor, -5);
            CreateRect(arenaRoot.transform, "RightBorder", new Vector2(ArenaHalfSize, 0f),
                new Vector2(0.18f, ArenaHalfSize * 2f + 0.18f), BorderColor, -5);
        }

        private Vector2 ReadMoveVector()
        {
            var x = 0f;
            var y = 0f;

            if (Input.GetKey(KeyCode.A)) x -= 1f;
            if (Input.GetKey(KeyCode.D)) x += 1f;
            if (Input.GetKey(KeyCode.S)) y -= 1f;
            if (Input.GetKey(KeyCode.W)) y += 1f;

            var move = new Vector2(x, y);
            return move.sqrMagnitude > 1f ? move.normalized : move;
        }

        private DotView CreateView(string playerId)
        {
            var viewRoot = new GameObject(playerId);
            viewRoot.transform.SetParent(transform, false);

            var renderer = viewRoot.AddComponent<SpriteRenderer>();
            renderer.sprite = _pixelSprite;
            renderer.color = ResolveColor(playerId);
            renderer.sortingOrder = 20;

            var label = new GameObject("Label");
            label.transform.SetParent(viewRoot.transform, false);
            label.transform.localPosition = new Vector3(0f, 0.9f, 0f);

            var text = label.AddComponent<TextMesh>();
            text.text = playerId;
            text.fontSize = 32;
            text.characterSize = 0.08f;
            text.anchor = TextAnchor.MiddleCenter;
            text.alignment = TextAlignment.Center;
            text.color = new Color(0.92f, 0.96f, 1f, 0.88f);

            var view = new DotView(viewRoot, renderer);
            view.ApplyPresentation(playerId == _localPlayerId, ResolveColor(playerId), PlayerLifeState.Idle, true);
            return view;
        }

        private Color ResolveColor(string playerId)
        {
            if (playerId == _localPlayerId) return RemotePalette[0];

            var index = Mathf.Abs(playerId.GetHashCode()) % (RemotePalette.Length - 1);
            return RemotePalette[index + 1];
        }

        private void CreateRect(Transform parent, string objectName, Vector2 position, Vector2 size, Color color,
            int sortingOrder)
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

        private string GetCurrentEventMessage()
        {
            if (_eventMessageUntil > 0f && Time.time > _eventMessageUntil)
            {
                _eventMessageUntil = 0f;
                if (_views.Count < 2)
                {
                    _eventMessage = "等待玩家加入";
                }
                else
                {
                    _eventMessage = "对局进行中";
                }
            }

            return _eventMessage;
        }

        private void PushEvent(string message, float durationSeconds = 3f)
        {
            _eventMessage = message;
            _eventMessageUntil = Time.time + durationSeconds;
        }

        private static WorldState CloneWorldState(WorldState source)
        {
            var clone = new WorldState
            {
                Tick = source.Tick
            };

            foreach (var player in source.Players)
            {
                clone.Players.Add(new PlayerState
                {
                    PlayerId = player.PlayerId,
                    X = player.X,
                    Y = player.Y,
                    Vx = player.Vx,
                    Vy = player.Vy,
                    State = player.State,
                    Alive = player.Alive
                });
            }

            return clone;
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

        private sealed class PlayerRenderState
        {
            public Vector2 PreviousPosition { get; set; }
            public Vector2 TargetPosition { get; set; }
            public float ReceivedAt { get; set; }
            public PlayerLifeState State { get; set; }
            public bool Alive { get; set; }
        }

        private sealed class DotView
        {
            private readonly SpriteRenderer _renderer;

            public DotView(GameObject root, SpriteRenderer renderer)
            {
                Root = root;
                _renderer = renderer;
            }

            public GameObject Root { get; }

            public Vector2 GetPosition()
            {
                var position = Root.transform.position;
                return new Vector2(position.x, position.y);
            }

            public void SetPosition(Vector2 position)
            {
                Root.transform.position = new Vector3(position.x, position.y, 0f);
            }

            public void ApplyPresentation(bool isLocalPlayer, Color baseColor, PlayerLifeState state, bool alive)
            {
                var color = baseColor;
                if (!alive)
                {
                    color = new Color(baseColor.r * 0.35f, baseColor.g * 0.35f, baseColor.b * 0.35f, 0.55f);
                }
                else if (state == PlayerLifeState.Dash)
                {
                    color = Color.Lerp(baseColor, Color.white, 0.3f);
                }
                else if (state == PlayerLifeState.Stunned)
                {
                    color = Color.Lerp(baseColor, new Color(1f, 0.9f, 0.45f, 1f), 0.45f);
                }

                _renderer.color = color;

                var scale = PlayerSize;
                if (isLocalPlayer)
                {
                    scale += 0.08f;
                }

                if (state == PlayerLifeState.Dash)
                {
                    scale += 0.12f;
                }

                Root.transform.localScale = new Vector3(scale, scale, 1f);
            }
        }
    }
}

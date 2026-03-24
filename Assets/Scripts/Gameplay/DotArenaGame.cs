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
        private const int WindowWidth = 1600;
        private const int WindowHeight = 800;
        private const float ArenaHalfSize = 10f;
        private const float ArenaVisualPadding = 1.8f;
        // Keep this aligned with the server-side collision radius in GameArenaRuntime.
        private const float PlayerRadius = 0.9f;
        private const float PlayerDiameter = PlayerRadius * 2f;
        private const float PlayerNameOffsetY = 0.1f;
        private const float PlayerScoreOffsetY = -0.14f;
        private const int PlayerSortingOrder = 20;
        private const int PlayerTextSortingOrder = 30;
        private const float PlayerTextDepth = -0.2f;
        private const float PlayerNameScale = 0.12f;
        private const float PlayerScoreScale = 0.1f;
        private const float PlayerTextCharacterSize = 0.08f;
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
        private Sprite _playerSprite = null!;
        private string _status = "Connecting...";
        private string _eventMessage = "等待玩家加入";
        private float _eventMessageUntil;
        private int _lastWorldTick = -1;
        private int _lastLoggedPlayerCount = -1;
        private bool _applicationQuitting;

        private async void Start()
        {
            ApplyLaunchOverrides();
            ConfigureWindow();
            InitializeConnectionMode();
            ConfigureCamera();
            BuildArena();

            if (ShouldAutoConnectOnStart())
            {
                await ConnectAsync();
            }
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
            if (!_applicationQuitting)
            {
                _cts.Cancel();
                _ = DisposeConnectionAsync();
            }

            _cts.Dispose();
        }

        private void OnApplicationQuit()
        {
            _applicationQuitting = true;
            DisposeConnectionSynchronously();
        }

        private void OnGUI()
        {
            const float width = 400f;
            var height = ShouldShowConnectControls() ? 248f : 160f;

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
                $"玩家: {(_localPlayerId.Length > 0 ? _localPlayerId : _account)}   积分: {GetLocalPlayerScoreText()}", bodyStyle);
            GUI.Label(new Rect(contentRect.x, contentRect.y + 64f, contentRect.width, 18f),
                $"服务端 Tick: {_lastWorldTick}   同步人数: {_views.Count}", bodyStyle);
            GUI.Label(new Rect(contentRect.x, contentRect.y + 84f, contentRect.width, 18f),
                $"地址: {Rpc.WebSocketRpcClientFactory.BuildUrl(_host, _port, _path)}", bodyStyle);

            if (ShouldShowConnectControls())
            {
                DrawConnectControls(contentRect, bodyStyle);
                GUI.Label(new Rect(contentRect.x, contentRect.y + 192f, contentRect.width, 18f),
                    $"事件: {GetCurrentEventMessage()}", bodyStyle);
                GUI.Label(new Rect(contentRect.x, contentRect.y + 212f, contentRect.width, 18f),
                    "连接成功后可用 W/A/S/D 移动, Space 冲刺。", bodyStyle);
                return;
            }

            GUI.Label(new Rect(contentRect.x, contentRect.y + 104f, contentRect.width, 18f),
                "W/A/S/D 移动, Space 冲刺。客户端只发输入，位置以服务端广播为准。", bodyStyle);
            GUI.Label(new Rect(contentRect.x, contentRect.y + 124f, contentRect.width, 18f),
                $"事件: {GetCurrentEventMessage()}", bodyStyle);

            DrawPlayerOverlays();
        }

        private void DrawPlayerOverlays()
        {
            var camera = Camera.main;
            if (camera == null || _views.Count == 0)
            {
                return;
            }

            var pixelsPerWorldUnit = Screen.height / (camera.orthographicSize * 2f);
            var diameterPixels = PlayerDiameter * pixelsPerWorldUnit;
            var labelWidth = Mathf.Max(96f, diameterPixels * 2f);
            var nameHeight = Mathf.Max(18f, diameterPixels * 0.36f);
            var scoreHeight = Mathf.Max(16f, diameterPixels * 0.3f);

            var nameStyle = new GUIStyle(GUI.skin.label)
            {
                alignment = TextAnchor.MiddleCenter,
                fontStyle = FontStyle.Bold,
                fontSize = Mathf.RoundToInt(Mathf.Clamp(diameterPixels * 0.24f, 14f, 22f)),
                clipping = TextClipping.Overflow,
                normal = { textColor = new Color(0.94f, 0.97f, 1f, 1f) }
            };

            var scoreStyle = new GUIStyle(GUI.skin.label)
            {
                alignment = TextAnchor.MiddleCenter,
                fontStyle = FontStyle.Bold,
                fontSize = Mathf.RoundToInt(Mathf.Clamp(diameterPixels * 0.22f, 13f, 20f)),
                clipping = TextClipping.Overflow,
                normal = { textColor = new Color(1f, 0.97f, 0.78f, 1f) }
            };

            foreach (var entry in _views)
            {
                if (!_renderStates.TryGetValue(entry.Key, out var renderState))
                {
                    continue;
                }

                var worldPosition = entry.Value.Root.transform.position;
                var screenPosition = camera.WorldToScreenPoint(worldPosition);
                if (screenPosition.z <= 0f)
                {
                    continue;
                }

                var centerX = screenPosition.x;
                var centerY = Screen.height - screenPosition.y;
                var nameRect = new Rect(centerX - (labelWidth * 0.5f), centerY - (nameHeight * 1.05f), labelWidth, nameHeight);
                var scoreRect = new Rect(centerX - (labelWidth * 0.5f), centerY + (scoreHeight * 0.05f), labelWidth, scoreHeight);

                GUI.Label(nameRect, entry.Key, nameStyle);
                GUI.Label(scoreRect, $"score: {FormatScore(renderState.Score)}", scoreStyle);
            }
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
                Debug.Log($"[DotArena] Connected as {_localPlayerId} -> {Rpc.WebSocketRpcClientFactory.BuildUrl(_host, _port, _path)}");
                PushEvent("等待其他玩家加入");
            }
            catch (OperationCanceledException)
            {
                _status = "Connection canceled";
            }
            catch (Exception ex)
            {
                _status = $"Connect failed: {ex.Message}";
                Debug.LogError($"[DotArena] Connect failed: {ex}");
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
            if (worldState.Players.Count != _lastLoggedPlayerCount)
            {
                _lastLoggedPlayerCount = worldState.Players.Count;
                Debug.Log($"[DotArena] WorldState tick={worldState.Tick}, players={worldState.Players.Count}, local={_localPlayerId}");
            }

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
                renderState.Score = player.Score;

                view.SetIdentity(player.PlayerId, player.Score);
                view.ApplyPresentation(ResolveColor(player.PlayerId), player.State, player.Alive);
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
                view.ApplyPresentation(ResolveColor(deadEvent.PlayerId), PlayerLifeState.Dead, false);
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
            _localPlayerId = string.Empty;
            _status = ex == null ? "Disconnected" : $"Disconnected: {ex.Message}";
            Debug.LogWarning($"[DotArena] {_status}");
        }

        private async Task DisposeConnectionAsync()
        {
            if (_connection == null) return;

            var connection = _connection;
            var playerService = _playerService;
            var shouldLogout = _isConnected && playerService != null;
            _connection = null;

            try
            {
                if (shouldLogout)
                {
                    await playerService!.LogoutAsync();
                }
            }
            catch
            {
            }

            try
            {
                connection.Disconnected -= OnDisconnected;
                await connection.DisposeAsync();
            }
            catch
            {
            }
            finally
            {
                _playerService = null;
                _isConnected = false;
                _localPlayerId = string.Empty;
            }
        }

        private void DisposeConnectionSynchronously()
        {
            try
            {
                DisposeConnectionAsync().GetAwaiter().GetResult();
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

        private void ConfigureWindow()
        {
#if !UNITY_EDITOR && UNITY_STANDALONE_WIN
            Screen.SetResolution(WindowWidth, WindowHeight, FullScreenMode.Windowed);
#endif
        }

        private void InitializeConnectionMode()
        {
            if (ShouldAutoConnectOnStart())
            {
                _status = "Connecting...";
                return;
            }

            _status = "Ready to connect";
            _eventMessage = "请先输入账号密码";
        }

        private void ApplyLaunchOverrides()
        {
            var launchArguments = Rpc.RpcLaunchArguments.ReadCurrentProcess();
            launchArguments.ApplyTo(ref _host, ref _port, ref _path);
            launchArguments.ApplyCredentials(ref _account, ref _password);

            if (launchArguments.HasOverrides)
            {
                Debug.Log($"[LaunchArgs] DotArenaGame host={_host}, port={_port}, path={_path}, account={_account}");
            }
        }

        private bool ShouldAutoConnectOnStart()
        {
            return Application.isEditor;
        }

        private bool ShouldShowConnectControls()
        {
            return !Application.isEditor && !_isConnected;
        }

        private void DrawConnectControls(Rect contentRect, GUIStyle bodyStyle)
        {
            const float labelWidth = 64f;
            const float fieldHeight = 22f;
            var fieldWidth = contentRect.width - labelWidth - 12f;
            var accountY = contentRect.y + 108f;
            var passwordY = contentRect.y + 138f;
            var buttonY = contentRect.y + 168f;

            GUI.Label(new Rect(contentRect.x, accountY + 2f, labelWidth, 18f), "账号", bodyStyle);
            GUI.Label(new Rect(contentRect.x, passwordY + 2f, labelWidth, 18f), "密码", bodyStyle);

            var previousEnabled = GUI.enabled;
            GUI.enabled = !_isConnecting;
            _account = GUI.TextField(new Rect(contentRect.x + labelWidth, accountY, fieldWidth, fieldHeight), _account);
            _password = GUI.PasswordField(new Rect(contentRect.x + labelWidth, passwordY, fieldWidth, fieldHeight), _password, '*');

            var buttonLabel = _isConnecting ? "Connecting..." : "Connect";
            if (GUI.Button(new Rect(contentRect.x, buttonY, 120f, 24f), buttonLabel))
            {
                _ = ConnectAsync();
            }

            GUI.enabled = previousEnabled;
        }

        private void BuildArena()
        {
            _pixelSprite = CreatePixelSprite();
            _playerSprite = CreateCircleSprite();

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
            renderer.sprite = _playerSprite;
            renderer.color = ResolveColor(playerId);
            renderer.sortingOrder = PlayerSortingOrder;

            var nameLabel = new GameObject("NameLabel");
            nameLabel.transform.SetParent(viewRoot.transform, false);
            nameLabel.transform.localPosition = new Vector3(0f, PlayerNameOffsetY, PlayerTextDepth);
            nameLabel.transform.localScale = Vector3.one * PlayerNameScale;

            var nameText = nameLabel.AddComponent<TextMesh>();
            nameText.text = playerId;
            nameText.fontSize = 48;
            nameText.characterSize = PlayerTextCharacterSize;
            nameText.anchor = TextAnchor.MiddleCenter;
            nameText.alignment = TextAlignment.Center;
            nameText.fontStyle = FontStyle.Bold;
            nameText.color = new Color(0.92f, 0.96f, 1f, 0.92f);
            ConfigureTextRenderer(nameText.GetComponent<MeshRenderer>(), PlayerTextSortingOrder);

            var scoreLabel = new GameObject("ScoreLabel");
            scoreLabel.transform.SetParent(viewRoot.transform, false);
            scoreLabel.transform.localPosition = new Vector3(0f, PlayerScoreOffsetY, PlayerTextDepth);
            scoreLabel.transform.localScale = Vector3.one * PlayerScoreScale;

            var scoreText = scoreLabel.AddComponent<TextMesh>();
            scoreText.text = "1";
            scoreText.fontSize = 44;
            scoreText.characterSize = PlayerTextCharacterSize;
            scoreText.anchor = TextAnchor.MiddleCenter;
            scoreText.alignment = TextAlignment.Center;
            scoreText.fontStyle = FontStyle.Bold;
            scoreText.color = new Color(1f, 0.97f, 0.78f, 0.95f);
            ConfigureTextRenderer(scoreText.GetComponent<MeshRenderer>(), PlayerTextSortingOrder);

            var view = new DotView(viewRoot, renderer, nameText, scoreText);
            view.SetIdentity(playerId, 1);
            view.ApplyPresentation(ResolveColor(playerId), PlayerLifeState.Idle, true);
            return view;
        }

        private Color ResolveColor(string playerId)
        {
            var index = GetStableColorIndex(playerId);
            return RemotePalette[index];
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

        private static void ConfigureTextRenderer(MeshRenderer? renderer, int sortingOrder)
        {
            if (renderer == null)
            {
                return;
            }

            renderer.sortingOrder = sortingOrder;
            renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            renderer.receiveShadows = false;
            renderer.lightProbeUsage = UnityEngine.Rendering.LightProbeUsage.Off;
            renderer.reflectionProbeUsage = UnityEngine.Rendering.ReflectionProbeUsage.Off;
        }

        private string GetLocalPlayerScoreText()
        {
            if (_localPlayerId.Length == 0)
            {
                return "0";
            }

            return _renderStates.TryGetValue(_localPlayerId, out var renderState)
                ? FormatScore(renderState.Score)
                : "0";
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
                Tick = source.Tick,
                RespawnDelaySeconds = source.RespawnDelaySeconds
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
                    Alive = player.Alive,
                    RespawnRemainingSeconds = player.RespawnRemainingSeconds,
                    Score = player.Score
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

        private static Sprite CreateCircleSprite()
        {
            const int textureSize = 64;
            var texture = new Texture2D(textureSize, textureSize, TextureFormat.RGBA32, false)
            {
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp,
                hideFlags = HideFlags.HideAndDontSave
            };

            var center = (textureSize - 1) * 0.5f;
            var radius = textureSize * 0.5f;
            var edgeSoftness = 1.25f;

            for (var y = 0; y < textureSize; y++)
            {
                for (var x = 0; x < textureSize; x++)
                {
                    var dx = x - center;
                    var dy = y - center;
                    var distance = Mathf.Sqrt((dx * dx) + (dy * dy));
                    var alpha = Mathf.Clamp01((radius - distance) / edgeSoftness);
                    texture.SetPixel(x, y, new Color(1f, 1f, 1f, alpha));
                }
            }

            texture.Apply();

            return Sprite.Create(
                texture,
                new Rect(0f, 0f, textureSize, textureSize),
                new Vector2(0.5f, 0.5f),
                textureSize);
        }

        private static int GetStableColorIndex(string playerId)
        {
            unchecked
            {
                var hash = 2166136261u;
                foreach (var ch in playerId)
                {
                    hash ^= ch;
                    hash *= 16777619u;
                }

                return (int)(hash % (uint)RemotePalette.Length);
            }
        }

        private static string FormatScore(int score)
        {
            return score.ToString();
        }

        private sealed class PlayerRenderState
        {
            public Vector2 PreviousPosition { get; set; }
            public Vector2 TargetPosition { get; set; }
            public float ReceivedAt { get; set; }
            public PlayerLifeState State { get; set; }
            public bool Alive { get; set; }
            public int Score { get; set; }
        }

        private sealed class DotView
        {
            private readonly SpriteRenderer _renderer;
            private readonly TextMesh _nameText;
            private readonly TextMesh _scoreText;

            public DotView(GameObject root, SpriteRenderer renderer, TextMesh nameText, TextMesh scoreText)
            {
                Root = root;
                _renderer = renderer;
                _nameText = nameText;
                _scoreText = scoreText;
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

            public void SetIdentity(string playerId, int score)
            {
                _nameText.text = playerId;
                _scoreText.text = FormatScore(score);
            }

            public void ApplyPresentation(Color baseColor, PlayerLifeState state, bool alive)
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
                Root.transform.localScale = new Vector3(PlayerDiameter, PlayerDiameter, 1f);
            }
        }
    }
}











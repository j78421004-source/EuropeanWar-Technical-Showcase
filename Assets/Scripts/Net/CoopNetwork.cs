using EuropeanWar.Core;
using FishNet.Connection;
using FishNet.Managing;
using FishNet.Object;
using FishNet.Transporting;
using FishNet.Transporting.Tugboat;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.SceneManagement;

namespace EuropeanWar.Net
{
    /// <summary>
    /// Кооп-бой на FishNet. По выбору из NetSession запускает хост (сервер + свой клиент) или клиент,
    /// каждому подключившемуся спавнит сетевой аватар и показывает состояние сети.
    /// Клиент ждёт хоста (D-030): переподключается, пока хост не откроет бой (быстрый или с карты кампании),
    /// а когда хост закончил бой — перезагружает сцену и ждёт следующий.
    /// В одиночной игре ничего не делает: сеть не стартует.
    /// </summary>
    [RequireComponent(typeof(NetworkManager))]
    public sealed class CoopNetwork : MonoBehaviour
    {
        [SerializeField, Tooltip("Сетевой аватар игрока: префаб с NetworkObject и NetAvatar")]
        private NetworkObject avatarPrefab;
        [SerializeField, Tooltip("Сколько секунд ждать ответа на одну попытку подключения")]
        private float connectTimeout = 15f;
        [SerializeField, Tooltip("Пауза между попытками подключения клиента к хосту, с")]
        private float retryDelay = 1.5f;

        public static CoopNetwork Instance { get; private set; }
        /// <summary>Итог прошлого боя для экрана ожидания: переживает перезагрузку сцены клиентом.</summary>
        private static string s_waitNote;

        private NetworkManager _nm;
        private Tugboat _tugboat;
        private bool _started, _subscribed, _serverUp, _clientUp, _everConnected, _shuttingDown;
        private string _error;
        private float _startTime, _attemptStart, _retryAt = -1f;
        private int _nextSlot, _attempts;
        private GUIStyle _small, _big, _hint, _title;

        public bool IsRunning => _started && !_shuttingDown;
        public bool IsConnected => _clientUp;
        public bool ServerUp => _serverUp;
        public string Error => _error;
        /// <summary>Клиент ещё ждёт, пока хост откроет бой (D-030).</summary>
        public bool WaitingForHost => _started && !_shuttingDown && _error == null && NetSession.IsClientOnly && !_everConnected;
        /// <summary>Сколько попыток подключения сделано в этом бою.</summary>
        public int ConnectAttempts => _attempts;
        public long PingMs => _nm != null && _nm.TimeManager != null ? _nm.TimeManager.RoundTripTime : 0L;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStatics() => s_waitNote = null;

        private void Awake()
        {
            Instance = this;
            _nm = GetComponent<NetworkManager>();
            _tugboat = GetComponent<Tugboat>();
        }

        private void Start()
        {
            if (!NetSession.IsOnline) return;
            if (NetSession.SmokeSeconds > 0f) CoopSmokeProbe.EnsureExists(this);
            if (_tugboat == null || avatarPrefab == null)
            {
                Fail("Сеть в сцене не настроена: нет транспорта или префаба аватара.");
                return;
            }

            Application.runInBackground = true;
            // FishNet reapplies its own limits on connect/disconnect; both roles must agree.
            PerformanceSettings.Apply();
            _nm.ServerManager.SetFrameRate(PerformanceSettings.TargetFrameRate);
            _nm.ClientManager.SetFrameRate(PerformanceSettings.TargetFrameRate);
            _nm.ServerManager.OnServerConnectionState += OnServerState;
            _nm.ClientManager.OnClientConnectionState += OnClientState;
            _nm.SceneManager.OnClientLoadedStartScenes += OnClientLoadedStartScenes;
            _subscribed = true;

            _tugboat.SetPort(NetSession.Port);
            _tugboat.SetClientAddress(NetSession.IsHost ? NetSession.LocalAddress : NetSession.Address);
            _started = true;
            _startTime = Time.unscaledTime;
            Debug.Log("[Кооп] Старт: " + NetSession.Describe());

            if (NetSession.IsHost && !_nm.ServerManager.StartConnection())
            {
                Fail("Не удалось запустить сервер на порту " + NetSession.Port + ".");
                return;
            }
            Connect();
        }

        private void Connect()
        {
            _retryAt = -1f;
            _attempts++;
            _attemptStart = Time.unscaledTime;
            if (_attempts == 2 || _attempts % 10 == 0)
                Debug.Log("[Кооп] Хост ещё не открыл бой — попытка " + _attempts + " (" + NetSession.Address + ":" + NetSession.Port + ")");
            _nm.ClientManager.StartConnection();
        }

        private static bool EnterPressed()
        {
            Keyboard keyboard = Keyboard.current;
            return keyboard != null && (keyboard.enterKey.wasPressedThisFrame || keyboard.numpadEnterKey.wasPressedThisFrame);
        }

        private void Update()
        {
            if ((_error != null || WaitingForHost) && EnterPressed())
            {
                LeaveToMenu();
                return;
            }
            if (_error != null || !_started || _shuttingDown) return;
            if (_retryAt >= 0f)
            {
                if (Time.unscaledTime >= _retryAt) Connect();
                return;
            }
            // Starting several players on a slow machine can stall the local listen-client for a while.
            float attemptTimeout = NetSession.IsHost ? Mathf.Max(60f, connectTimeout) : connectTimeout;
            if (_everConnected || Time.unscaledTime - _attemptStart <= attemptTimeout) return;
            if (NetSession.IsClientOnly)
            {
                // Попытка зависла — обрываем и пробуем снова: хост мог ещё не открыть бой.
                _retryAt = Time.unscaledTime + retryDelay;
                if (_nm.ClientManager.Started) _nm.ClientManager.StopConnection();
            }
            else Fail("Хост не отвечает: " + NetSession.Address + ":" + NetSession.Port + ".");
        }

        private void OnServerState(ServerConnectionStateArgs args)
        {
            if (args.ConnectionState == LocalConnectionState.Started)
            {
                _serverUp = true;
                Debug.Log("[Кооп] Сервер слушает порт " + NetSession.Port);
            }
            else if (args.ConnectionState == LocalConnectionState.Stopped)
            {
                bool wasUp = _serverUp;
                _serverUp = false;
                if (!_shuttingDown) Fail(wasUp ? "Сервер остановился." : "Не удалось открыть порт " + NetSession.Port + " (занят другой программой?).");
            }
        }

        private void OnClientState(ClientConnectionStateArgs args)
        {
            if (args.ConnectionState == LocalConnectionState.Started)
            {
                _clientUp = true;
                _everConnected = true;
                _retryAt = -1f;
                CoopSmokeProbe.RecordConnection(this);
                Debug.Log("[Кооп] Подключено за " + (Time.unscaledTime - _startTime).ToString("0.0") + " с, попыток: " + _attempts);
            }
            else if (args.ConnectionState == LocalConnectionState.Stopped)
            {
                _clientUp = false;
                if (_shuttingDown) return;
                if (NetSession.IsClientOnly && !_everConnected)
                {
                    // Хост ещё не открыл бой — пробуем снова (D-030).
                    _retryAt = Time.unscaledTime + retryDelay;
                    return;
                }
                if (NetSession.WaitsForHost && _everConnected)
                {
                    ReloadAndWait();
                    return;
                }
                if (_everConnected) Fail(NetSession.IsHost ? "Сеть остановлена." : "Связь с хостом потеряна.");
                else Fail("Не удалось подключиться к " + NetSession.Address + ":" + NetSession.Port + ".");
            }
        }

        /// <summary>Клиент: хост закончил бой (вернулся на карту или вышел) — перезагрузить бой и ждать следующий (D-030).</summary>
        private void ReloadAndWait()
        {
            CoopSmokeProbe.RecordReload(this);
            int outcome = NetAvatar.RemoteBattleResult;
            s_waitNote = NetCodec.IsVictory(outcome) ? Localization.Text("coop.waitVictory", "Прошлый бой: победа.")
                : NetCodec.IsRetreat(outcome) ? Localization.Text("coop.waitRetreat", "Прошлый бой: отступление.")
                : outcome == 0 ? Localization.Text("coop.waitDefeat", "Прошлый бой: поражение.")
                : Localization.Text("coop.waitHostLeft", "Хост вышел из боя.");
            Debug.Log("[Кооп] Хост закончил бой — ждём следующий. " + s_waitNote);
            _shuttingDown = true;
            BattleSetup.Clear();
            Time.timeScale = 1f;
            SceneManager.LoadScene(NetSession.BattleScene);
        }

        private void OnClientLoadedStartScenes(NetworkConnection conn, bool asServer)
        {
            if (!asServer) return;
            int slot = _nextSlot++;
            NetworkObject nob = _nm.GetPooledInstantiated(avatarPrefab, Vector3.zero, Quaternion.identity, true);
            _nm.ServerManager.Spawn(nob, conn);
            _nm.SceneManager.AddOwnerToDefaultScene(nob);
            NetAvatar avatar = nob.GetComponent<NetAvatar>();
            if (avatar != null) avatar.ServerSetup(slot);
            Debug.Log("[Кооп] В бой вошёл игрок " + (slot + 1));
        }

        private void Fail(string message)
        {
            if (_error == null) _error = message;
            CoopSmokeProbe.RecordNetworkError(_error);
            Debug.LogWarning("[Кооп] " + message);
            Shutdown();
        }

        private void Shutdown()
        {
            if (_shuttingDown) return;
            _shuttingDown = true;
            if (_nm == null) return;
            if (_nm.ClientManager != null && _nm.ClientManager.Started) _nm.ClientManager.StopConnection();
            if (_nm.ServerManager != null && _nm.ServerManager.Started) _nm.ServerManager.StopConnection(true);
        }

        /// <summary>Хост кооп-кампании: закрыть сеть и вернуться на карту, сохранив итог боя для кампании (D-030).</summary>
        public static void ReturnToCampaign(string scene)
        {
            if (Instance != null) Instance.Shutdown();
            NetSession.EndCampaignBattle();
            Time.timeScale = 1f;
            SceneManager.LoadScene(scene);
        }

        /// <summary>Выйти из кооп-боя в главное меню (останавливает сеть, сбрасывает бой и кооп-кампанию).</summary>
        public static void LeaveToMenu()
        {
            if (Instance != null) Instance.Shutdown();
            s_waitNote = null;
            NetSession.Clear();
            BattleSetup.Clear();
            Time.timeScale = 1f;
            SceneManager.LoadScene(NetSession.MenuScene);
        }

        private void OnDestroy()
        {
            if (_started) Shutdown();
            if (_subscribed && _nm != null)
            {
                if (_nm.ServerManager != null) _nm.ServerManager.OnServerConnectionState -= OnServerState;
                if (_nm.ClientManager != null) _nm.ClientManager.OnClientConnectionState -= OnClientState;
                if (_nm.SceneManager != null) _nm.SceneManager.OnClientLoadedStartScenes -= OnClientLoadedStartScenes;
            }
            if (Instance == this) Instance = null;
        }

        private void EnsureStyles()
        {
            if (_small != null) return;
            _small = new GUIStyle(GUI.skin.label) { fontSize = 15, fontStyle = FontStyle.Bold };
            _small.normal.textColor = new Color(0.85f, 0.95f, 1f);
            _hint = new GUIStyle(_small) { alignment = TextAnchor.MiddleCenter, wordWrap = true };
            _big = new GUIStyle(GUI.skin.label) { fontSize = 26, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleCenter, wordWrap = true };
            _big.normal.textColor = new Color(1f, 0.55f, 0.45f);
            _title = new GUIStyle(GUI.skin.label) { fontSize = 30, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleCenter, wordWrap = true };
            _title.normal.textColor = new Color(0.95f, 0.95f, 0.92f);
        }

        private void OnGUI()
        {
            if (!NetSession.IsOnline) return;
            if (_shuttingDown && _error == null) return;
            EnsureStyles();
            bool waiting = WaitingForHost;
            GUI.depth = waiting ? -10 : 0; // экран ожидания — поверх интерфейса боя
            if (waiting)
            {
                if (Time.unscaledTime - _startTime > 0.7f) DrawWaiting();
                return;
            }

            string role = NetSession.IsHost
                ? Localization.Text("coop.host", "хост")
                : Localization.Text("coop.client", "клиент");
            string state = _error != null ? Localization.Text("coop.noLink", "нет связи")
                : _clientUp ? Localization.Text("coop.playersPing", "игроков: {0} · пинг {1} мс", NetAvatar.All.Count, PingMs)
                : Localization.Text("coop.connecting", "подключение…");
            Color previousColor = GUI.color;
            GUI.color = new Color(0.03f, 0.04f, 0.06f, 0.8f);
            GUI.DrawTexture(new Rect(8f, 60f, 354f, NetSession.IsHost ? 52f : 28f), Texture2D.whiteTexture);
            GUI.color = previousColor;
            GUI.Label(new Rect(12f, 62f, 360f, 24f), Localization.Text("coop.coop", "Кооп") + " · " + role + " · " + state, _small);
            if (NetSession.IsHost && _serverUp && _error == null)
                GUI.Label(new Rect(12f, 86f, 360f, 24f), Localization.Text("coop.friendHint", "Подключение друга: IP хоста, UDP {0}", NetSession.Port), _small);

            if (_error == null) return;
            float w = Screen.width, h = Screen.height;
            GUI.Label(new Rect(20f, h * 0.38f, w - 40f, 70f), _error, _big);
            GUI.Label(new Rect(0f, h * 0.38f + 74f, w, 28f), "Enter — в главное меню", _hint);
        }

        private void DrawWaiting()
        {
            float w = Screen.width, h = Screen.height, y = h * 0.3f;
            Color old = GUI.color;
            GUI.color = new Color(0.04f, 0.05f, 0.07f, 0.97f);
            GUI.DrawTexture(new Rect(0f, 0f, w, h), Texture2D.whiteTexture);
            GUI.color = old;
            int waited = Mathf.FloorToInt(Time.unscaledTime - _startTime);
            GUI.Label(new Rect(20f, y, w - 40f, 44f), Localization.Text("coop.waitingHost", "Ждём, пока хост начнёт бой"), _title);
            if (!string.IsNullOrEmpty(s_waitNote)) GUI.Label(new Rect(20f, y + 50f, w - 40f, 28f), s_waitNote, _hint);
            GUI.Label(new Rect(20f, y + 84f, w - 40f, 28f), Localization.Text("coop.waitAddress",
                "Хост {0}:{1} · ждём {2}:{3} · попытка {4}", NetSession.Address, NetSession.Port,
                (waited / 60), (waited % 60).ToString("00"), _attempts), _hint);
            GUI.Label(new Rect(20f, y + 118f, w - 40f, 52f),
                Localization.Text("coop.hostStartHint", "Хост открывает бой в «Кооп-бой → Быстрый бой» или с карты своей кампании — вы подключитесь сами."), _hint);
            GUI.Label(new Rect(20f, y + 176f, w - 40f, 28f), Localization.Text("coop.enterMenu", "Enter — в главное меню"), _hint);
        }
    }
}

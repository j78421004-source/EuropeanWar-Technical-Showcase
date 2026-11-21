using System;
using System.Globalization;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace EuropeanWar.Net
{
    /// <summary>Режим сетевой игры.</summary>
    public enum NetMode
    {
        Offline,
        Host,
        Client
    }

    /// <summary>
    /// Ключи запуска кооп-боя из командной строки. Разбор отделён от Unity, чтобы его можно было проверить тестами.
    ///   -ewHost                 создать кооп-бой (хост)
    ///   -ewJoin 192.168.0.10    подключиться к хосту
    ///   -ewPort 7777            порт (UDP)
    ///   -ewSmoke 30 -ewOut x.json [-ewStopFile stop.flag]   автотест сети в собранной игре
    ///   -ewWaitHost             клиент ждёт хоста и в автотесте (кооп-кампания, D-030)
    ///   -ewSmokeActors 60       бенчмарк D-031: всего бойцов (ИИ и игроки) в кооп-бою; 2..200, вне диапазона — прижимается
    /// </summary>
    public struct NetLaunchArgs
    {
        public NetMode Mode;
        public string Address;
        public ushort Port;
        public float SmokeSeconds;
        public string SmokeOut;
        public string StopFile;
        public bool WaitForHost;
        public bool PerfOnly;
        public int SmokeActors;
        public bool SmokeCommands;

        public bool HasMode => Mode != NetMode.Offline;

        public static NetLaunchArgs Parse(string[] args)
        {
            var r = new NetLaunchArgs { Mode = NetMode.Offline, Address = NetSession.LocalAddress, Port = NetSession.DefaultPort };
            if (args == null) return r;
            for (int i = 0; i < args.Length; i++)
            {
                string key = args[i] == null ? string.Empty : args[i].Trim().ToLowerInvariant();
                string next = i + 1 < args.Length ? args[i + 1] : null;
                bool nextIsValue = !string.IsNullOrWhiteSpace(next) && !next.TrimStart().StartsWith("-");
                switch (key)
                {
                    case "-ewhost":
                        r.Mode = NetMode.Host;
                        r.Address = NetSession.LocalAddress;
                        break;
                    case "-ewjoin":
                        if (nextIsValue)
                        {
                            r.Mode = NetMode.Client;
                            r.Address = next.Trim();
                            i++;
                        }
                        break;
                    case "-ewport":
                        if (ushort.TryParse(next, NumberStyles.Integer, CultureInfo.InvariantCulture, out ushort port) && port > 0)
                        {
                            r.Port = port;
                            i++;
                        }
                        break;
                    case "-ewsmoke":
                        if (float.TryParse(next, NumberStyles.Float, CultureInfo.InvariantCulture, out float seconds) && seconds > 0f && !float.IsInfinity(seconds))
                        {
                            r.SmokeSeconds = seconds;
                            i++;
                        }
                        break;
                    case "-ewsmokeactors":
                        // D-031: значение прижимается к 2..200, неразобранное значение игнорируется.
                        if (nextIsValue && int.TryParse(next, NumberStyles.Integer, CultureInfo.InvariantCulture, out int actors))
                        {
                            if (actors < 2) actors = 2;
                            else if (actors > 200) actors = 200;
                            r.SmokeActors = actors;
                            i++;
                        }
                        break;
                    case "-ewwaithost":
                        r.WaitForHost = true;
                        break;
                    case "-ewsmokecommands":
                        r.SmokeCommands = true;
                        break;
                    case "-ewperfonly":
                        r.PerfOnly = true;
                        break;
                    case "-ewout":
                        if (nextIsValue)
                        {
                            r.SmokeOut = next.Trim();
                            i++;
                        }
                        break;
                    case "-ewstopfile":
                        if (nextIsValue)
                        {
                            r.StopFile = next.Trim();
                            i++;
                        }
                        break;
                }
            }
            return r;
        }
    }

    /// <summary>
    /// Выбор режима кооп-боя между меню и сценой боя: офлайн, хост или клиент.
    /// Задаётся из главного меню или ключами командной строки (тогда игра сразу открывает бой).
    /// </summary>
    public static class NetSession
    {
        public const ushort DefaultPort = 7777;
        public const string LocalAddress = "127.0.0.1";
        public const string BattleScene = "TestBattle";
        public const string MenuScene = "MainMenu";

        public static NetMode Mode { get; private set; }
        public static string Address { get; private set; } = LocalAddress;
        public static ushort Port { get; private set; } = DefaultPort;
        /// <summary>Режим задан ключами командной строки (автотест или ярлык).</summary>
        public static bool FromCommandLine { get; private set; }
        /// <summary>Автотест сети: сколько секунд играть; 0 — обычная игра.</summary>
        public static float SmokeSeconds { get; private set; }
        public static string SmokeOut { get; private set; }
        public static string StopFile { get; private set; }
        public static bool WaitForHost { get; private set; }
        public static bool PerfOnly { get; private set; }
        /// <summary>
        /// Бенчмарк D-031: сколько всего бойцов расставить (ИИ и игроки), 0 — обычная численность.
        /// Задаётся только ключом командной строки и сбросится в Clear(); применим только при SmokeSeconds &gt; 0.
        /// </summary>
        public static int SmokeActors { get; private set; }
        public static bool SmokeCommands { get; private set; }

        /// <summary>Численность бенчмарка активна: автотест на хосте (клиенты ИИ не спавнят).</summary>
        public static bool BenchmarkActors => SmokeActors > 0 && SmokeSeconds > 0f && IsHost;

        public static bool IsOnline => Mode != NetMode.Offline;
        public static bool IsHost => Mode == NetMode.Host;
        public static bool IsClientOnly => Mode == NetMode.Client;

        /// <summary>
        /// Кооп-кампания (D-030): хост ведёт свою кампанию. На карте сеть выключена,
        /// а каждый бой с карты открывается кооп-боем, к которому подключается друг.
        /// </summary>
        public static bool CampaignHost { get; private set; }
        /// <summary>Клиент ждёт следующие бои; в автотесте это включается ключом -ewWaitHost.</summary>
        public static bool WaitsForHost => IsClientOnly && (SmokeSeconds <= 0f || WaitForHost);

        /// <summary>Начать кооп-кампанию хостом. Пока на карте — офлайн.</summary>
        public static void HostCampaign(ushort port = DefaultPort)
        {
            Mode = NetMode.Offline;
            Address = LocalAddress;
            Port = port == 0 ? DefaultPort : port;
            CampaignHost = true;
        }

        /// <summary>Бой с карты: в кооп-кампании открыть сеть хостом. true — бой будет кооп-боем.</summary>
        public static bool BeginCampaignBattle()
        {
            if (!CampaignHost) return false;
            Mode = NetMode.Host;
            Address = LocalAddress;
            return true;
        }

        /// <summary>Возврат на карту после боя: сеть выключена, кооп-кампания продолжается. Быстрый кооп-бой не трогает.</summary>
        public static void EndCampaignBattle()
        {
            if (CampaignHost) Mode = NetMode.Offline;
        }

        public static void Host(ushort port = DefaultPort)
        {
            Mode = NetMode.Host;
            CampaignHost = false;
            WaitForHost = false;
            Address = LocalAddress;
            Port = port == 0 ? DefaultPort : port;
        }

        public static void Join(string address, ushort port = DefaultPort)
        {
            if (string.IsNullOrWhiteSpace(address)) throw new ArgumentException("Не указан адрес хоста", nameof(address));
            Mode = NetMode.Client;
            CampaignHost = false;
            WaitForHost = false;
            Address = address.Trim();
            Port = port == 0 ? DefaultPort : port;
        }

        public static void Clear()
        {
            Mode = NetMode.Offline;
            Address = LocalAddress;
            Port = DefaultPort;
            FromCommandLine = false;
            SmokeSeconds = 0f;
            SmokeOut = null;
            StopFile = null;
            WaitForHost = false;
            SmokeActors = 0;
            SmokeCommands = false;
            PerfOnly = false;
            CampaignHost = false;
        }

        public static string Describe()
        {
            switch (Mode)
            {
                case NetMode.Host: return (CampaignHost ? "хост кооп-кампании, порт " : "хост, порт ") + Port;
                case NetMode.Client: return "клиент, хост " + Address + ":" + Port;
                default: return CampaignHost ? "кооп-кампания, на карте сеть выключена" : "офлайн";
            }
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetOnLaunch() => Clear();

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void ReadCommandLine()
        {
            NetLaunchArgs a = NetLaunchArgs.Parse(Environment.GetCommandLineArgs());
            if (!a.HasMode) return;
            Mode = a.Mode;
            Address = a.Address;
            Port = a.Port;
            SmokeSeconds = a.SmokeSeconds;
            SmokeOut = a.SmokeOut;
            StopFile = a.StopFile;
            WaitForHost = a.WaitForHost;
            SmokeActors = a.SmokeActors;
            SmokeCommands = a.SmokeCommands;
            PerfOnly = a.PerfOnly;
            FromCommandLine = true;
            Debug.Log("[Кооп] Запуск из командной строки: " + Describe());
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void OpenBattleFromCommandLine()
        {
            if (!FromCommandLine) return;
            // Бенчмарк D-031: проба рендера/времени кадра ставится до загрузки боя и живёт отдельным DDOL-объектом.
            if (SmokeSeconds > 0f && SmokeActors > 0) FramePacingProbe.EnsureExists();
            if (SceneManager.GetActiveScene().name != BattleScene) SceneManager.LoadScene(BattleScene);
        }
    }
}

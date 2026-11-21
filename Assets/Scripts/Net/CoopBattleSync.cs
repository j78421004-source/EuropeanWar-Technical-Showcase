using System.Collections.Generic;
using EuropeanWar.AI;
using EuropeanWar.Combat;
using EuropeanWar.Sim;
using FishNet.Managing;
using FishNet.Object;
using UnityEngine;

namespace EuropeanWar.Net
{
    /// <summary>
    /// Хост кооп-боя: для каждого бойца ИИ спавнит сетевую копию (NetSoldier) его страны, чтобы клиент
    /// видел тот же бой. Новых бойцов ищет дважды в секунду — подкрепления тоже попадут в сеть.
    /// В одиночной игре и у клиента ничего не делает.
    /// </summary>
    public sealed class CoopBattleSync : MonoBehaviour
    {
        [SerializeField, Tooltip("Сетевые копии бойцов по странам (NetworkObject с NetSoldier)")]
        private NetworkObject[] soldierPrefabs = new NetworkObject[0];
        [SerializeField, Min(0.1f), Tooltip("Как часто искать новых бойцов, с")]
        private float scanInterval = 0.5f;

        private readonly HashSet<SoldierAI> _synced = new HashSet<SoldierAI>();
        private readonly List<SoldierAI> _found = new List<SoldierAI>();
        private NetworkManager _nm;
        private float _nextScan;
        private int _nextId;
        private bool _warnedNoPrefab;

        /// <summary>Сколько бойцов уже отправлено в сеть (счётчик за бой).</summary>
        public int SpawnedTotal => _nextId;

        private void Awake() => _nm = GetComponent<NetworkManager>();

        private void Update()
        {
            if (!NetSession.IsHost || _nm == null || !_nm.IsServerStarted) return;
            if (Time.unscaledTime < _nextScan) return;
            _nextScan = Time.unscaledTime + scanInterval;
            Scan();
        }

        private void Scan()
        {
            _synced.RemoveWhere(s => s == null);
            _found.Clear();
            var all = TeamMember.All;
            for (int i = 0; i < all.Count; i++)
            {
                TeamMember m = all[i];
                if (m == null) continue;
                SoldierAI ai = m.GetComponent<SoldierAI>();
                if (ai != null && !_synced.Contains(ai)) _found.Add(ai);
            }
            int before = _nextId;
            for (int i = 0; i < _found.Count; i++) Spawn(_found[i]);
            if (_nextId != before)
                Debug.Log($"[Кооп] Бойцов ИИ в сети: {_nextId} (+{_nextId - before})");
        }

        private void Spawn(SoldierAI ai)
        {
            _synced.Add(ai);
            NetworkObject prefab = PrefabFor(ai.Member != null ? ai.Member.Country.Tag : string.Empty);
            if (prefab == null) return;
            Transform t = ai.transform;
            NetworkObject nob = _nm.GetPooledInstantiated(prefab, t.position, t.rotation, true);
            _nm.ServerManager.Spawn(nob);
            nob.GetComponent<NetSoldier>().ServerBind(ai, _nextId++);
        }

        private NetworkObject PrefabFor(string countryTag)
        {
            NetworkObject fallback = null;
            for (int i = 0; i < soldierPrefabs.Length; i++)
            {
                NetworkObject p = soldierPrefabs[i];
                if (p == null) continue;
                NetSoldier s = p.GetComponent<NetSoldier>();
                if (s == null) continue;
                if (fallback == null) fallback = p;
                if (s.PrefabCountry == countryTag) return p;
            }
            if (!_warnedNoPrefab)
            {
                _warnedNoPrefab = true;
                Debug.LogWarning($"[Кооп] Нет сетевой копии бойца для страны {countryTag} — берётся первая из списка.");
            }
            return fallback;
        }
    }
}

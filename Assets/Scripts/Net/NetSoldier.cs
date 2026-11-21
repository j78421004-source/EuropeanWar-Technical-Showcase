using System;
using System.Collections.Generic;
using EuropeanWar.AI;
using EuropeanWar.Combat;
using EuropeanWar.Core;
using EuropeanWar.Sim;
using FishNet.Connection;
using FishNet.Object;
using FishNet.Object.Synchronizing;
using UnityEngine;

namespace EuropeanWar.Net
{
    /// <summary>
    /// Сетевая копия бойца ИИ в кооп-бою (M2, шаг 2; D-028). ИИ живёт только у хоста: там копия
    /// спрятана и каждый кадр повторяет настоящего бойца. Место и поворот (в том числе падение
    /// при смерти) идут через NetworkTransform; здоровье, рост (присед) и положение автомата — через SyncVar.
    /// У клиента копия — видимый боец своей страны. Урон решает хост, поэтому местные попадания её не ранят.
    /// </summary>
    [RequireComponent(typeof(Health))]
    public sealed class NetSoldier : NetworkBehaviour
    {
        public readonly SyncVar<int> Id = new SyncVar<int>(-1);
        public readonly SyncVar<string> Country = new SyncVar<string>(string.Empty);
        public readonly SyncVar<float> Hp = new SyncVar<float>(100f);
        public readonly SyncVar<byte> Stance = new SyncVar<byte>(200);
        public readonly SyncVar<ushort> Aim = new SyncVar<ushort>(0);

        [SerializeField, Tooltip("Страна этого префаба: по ней хост выбирает копию")]
        private string country = string.Empty;
        [SerializeField] private Transform visualRoot;
        [SerializeField] private Transform weaponHolder;
        [SerializeField] private CapsuleCollider bodyCollider;
        [SerializeField, Tooltip("Автомат копии: на нём у клиента видны выстрелы бойца (D-029)")]
        private Weapon weapon;

        private const float StandHeight = 1.8f;
        /// <summary>Больше этого за одно попадание клиент прислать не может (защита от мусора).</summary>
        private const float MaxRelayedDamage = 150f;
        private static readonly List<NetSoldier> s_all = new List<NetSoldier>();
        private Weapon _sourceWeapon;

        // Счётчики на весь процесс — для кооп-теста (D-029 / D-031).
        /// <summary>Клиент: сколько своих попаданий по копиям отправлено хосту.</summary>
        public static int HitsRelayed { get; private set; }
        /// <summary>Хост: сколько попаданий клиента применено к настоящим бойцам.</summary>
        public static int HitsApplied { get; private set; }
        /// <summary>Хост: сколько бойцов добито попаданиями клиента.</summary>
        public static int KillsByRemote { get; private set; }
        /// <summary>Клиент: сколько подтверждений убийства пришло от хоста (D-031).</summary>
        public static int KillsConfirmed { get; private set; }
        /// <summary>Хост: сколько выстрелов бойцов отправлено клиентам.</summary>
        public static int ShotsSent { get; private set; }
        /// <summary>Клиент: сколько выстрелов бойцов показано.</summary>
        public static int ShotsShown { get; private set; }

        /// <summary>Клиент: хост подтвердил, что попадание этого игрока добило бойца (D-031).</summary>
        public static event Action KillConfirmed;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetCounters()
        {
            HitsRelayed = 0; HitsApplied = 0; KillsByRemote = 0; KillsConfirmed = 0; ShotsSent = 0; ShotsShown = 0;
            KillConfirmed = null;
        }

        private Health _health;
        private TeamMember _member;
        private Renderer[] _renderers;
        private SoldierAI _source;
        private Health _sourceHealth;
        private Transform _sourceVisual, _sourceHolder;
        private SoldierPoseAnimator _pose;
        private bool _visible;

        /// <summary>Все копии бойцов в этой копии игры (у хоста — спрятанные).</summary>
        public static IReadOnlyList<NetSoldier> All => s_all;
        public string PrefabCountry => country;
        /// <summary>Копия видна и участвует в бою как боец (только у клиента).</summary>
        public bool IsVisibleProxy => _visible;
        public Health HealthComponent => _health;
        public SoldierAI Source => _source;

        private void Awake()
        {
            _health = GetComponent<Health>();
            _member = GetComponent<TeamMember>();
            _renderers = GetComponentsInChildren<Renderer>(true);
            _pose = GetComponentInChildren<SoldierPoseAnimator>(true);
            if (_pose != null && weaponHolder != null) _pose.BindWeaponHolder(weaponHolder);
            _health.Invulnerable = true;
            Hp.OnChange += OnHpChanged;
            Stance.OnChange += OnStanceChanged;
            Aim.OnChange += OnAimChanged;
            Country.OnChange += OnCountryChanged;
        }

        public override void OnStartServer()
        {
            base.OnStartServer();
            if (!s_all.Contains(this)) s_all.Add(this);
            SetVisible(false);
        }

        public override void OnStartClient()
        {
            base.OnStartClient();
            if (!s_all.Contains(this)) s_all.Add(this);
            if (IsServerInitialized) return; // у хоста есть настоящий боец
            _visible = true;
            SetVisible(true);
            ApplyCountry(Country.Value);
            ApplyStance(Stance.Value);
            ApplyAim(Aim.Value);
            ApplyHp(Hp.Value);
            _health.DamageRelay = OnLocalHit; // урон по копии решает хост (D-029)
        }

        public override void OnStopClient()
        {
            base.OnStopClient();
            if (!IsServerInitialized) s_all.Remove(this);
            _health.DamageRelay = null;
        }

        public override void OnStopServer()
        {
            base.OnStopServer();
            s_all.Remove(this);
        }

        private void OnDestroy()
        {
            s_all.Remove(this);
            if (_sourceWeapon != null) _sourceWeapon.Shot -= OnSourceShot;
        }

        // --- выстрелы и попадания (D-029) ---

        private void OnSourceShot(Vector3 from, Vector3 to)
        {
            if (!IsServerInitialized || !IsSpawned) return;
            ShotsSent++;
            ObserversShot(from, to);
        }

        /// <summary>Клиенты видят вспышку и трассер и слышат выстрел на копии бойца.</summary>
        [ObserversRpc(ExcludeServer = true)]
        private void ObserversShot(Vector3 from, Vector3 to)
        {
            if (!_visible) return;
            ShotsShown++;
            if (weapon != null) weapon.PlayRemoteShot(from, to);
        }

        /// <summary>Клиент попал в копию: урон здесь не вычитается, а уходит хосту.</summary>
        private void OnLocalHit(DamageInfo info)
        {
            if (!_visible || !IsSpawned) return;
            HitsRelayed++;
            ServerHit(info.Amount, info.Point, info.Direction);
        }

        /// <summary>Хост: применить попадание клиента к настоящему бойцу. Источник урона — аватар стрелявшего.</summary>
        [ServerRpc(RequireOwnership = false)]
        private void ServerHit(float amount, Vector3 point, Vector3 direction, NetworkConnection sender = null)
        {
            if (_sourceHealth == null || !_sourceHealth.IsAlive) return;
            if (sender == null || !sender.IsActive) return;
            if (float.IsNaN(amount) || float.IsInfinity(amount)) return;
            amount = Mathf.Clamp(amount, 0f, MaxRelayedDamage);
            if (amount <= 0f) return;
            GameObject source = NetAvatar.FindOwnedBy(sender);
            if (source == null) return;
            if (!_sourceHealth.TakeDamage(new DamageInfo(amount, point, direction, source))) return;
            HitsApplied++;
            if (!_sourceHealth.IsAlive)
            {
                KillsByRemote++;
                if (!sender.IsLocalClient) TargetKillConfirmed(sender);
            }
        }

        [TargetRpc]
        private void TargetKillConfirmed(NetworkConnection conn)
        {
            KillsConfirmed++;
            KillConfirmed?.Invoke();
        }

        /// <summary>Сервер: привязать копию к настоящему бойцу. Вызывать сразу после Spawn.</summary>
        public void ServerBind(SoldierAI source, int id)
        {
            _source = source;
            if (source == null) return;
            _sourceHealth = source.GetComponent<Health>();
            _sourceVisual = source.transform.Find("Visual");
            _sourceHolder = source.transform.Find("WeaponHolder");
            if (_sourceWeapon != null) _sourceWeapon.Shot -= OnSourceShot;
            _sourceWeapon = source.GetComponentInChildren<Weapon>();
            if (_sourceWeapon != null) _sourceWeapon.Shot += OnSourceShot;
            Id.Value = id;
            if (source.Member != null) Country.Value = source.Member.Country.Tag;
            CopyFromSource();
        }

        private void LateUpdate()
        {
            if (!IsServerInitialized) return;
            if (_source == null)
            {
                // Настоящего бойца убрали со сцены — убираем и копию у всех.
                if (IsSpawned && Id.Value >= 0) Despawn();
                return;
            }
            CopyFromSource();
        }

        private void CopyFromSource()
        {
            Transform s = _source.transform;
            transform.SetPositionAndRotation(s.position, s.rotation);
            if (_sourceHealth != null && !Mathf.Approximately(Hp.Value, _sourceHealth.Current))
                Hp.Value = _sourceHealth.Current;
            float heightScale = _source != null
                ? _source.HeightScale
                : (_sourceVisual != null ? _sourceVisual.localScale.y : 1f);
            byte stance = NetCodec.PackScale(heightScale);
            if (Stance.Value != stance) Stance.Value = stance;
            if (_sourceHolder != null)
            {
                ushort aim = NetCodec.PackAim(_sourceHolder.localPosition.y, _sourceHolder.localEulerAngles.x);
                if (Aim.Value != aim) Aim.Value = aim;
            }
        }

        private void OnHpChanged(float prev, float next, bool asServer)
        {
            if (!asServer && _visible) ApplyHp(next);
        }

        private void OnStanceChanged(byte prev, byte next, bool asServer)
        {
            if (!asServer && _visible) ApplyStance(next);
        }

        private void OnAimChanged(ushort prev, ushort next, bool asServer)
        {
            if (!asServer && _visible) ApplyAim(next);
        }

        private void OnCountryChanged(string prev, string next, bool asServer)
        {
            if (!asServer && _visible) ApplyCountry(next);
        }

        private void SetVisible(bool visible)
        {
            if (_renderers == null) _renderers = GetComponentsInChildren<Renderer>(true);
            for (int i = 0; i < _renderers.Length; i++)
                if (_renderers[i] != null) _renderers[i].enabled = visible;
            if (bodyCollider != null) bodyCollider.enabled = visible && _health.IsAlive;
            if (_member != null) _member.enabled = visible;
        }

        private void ApplyHp(float hp)
        {
            _health.SetCurrent(hp);
            if (bodyCollider != null) bodyCollider.enabled = _visible && _health.IsAlive;
        }

        private void ApplyStance(byte packed)
        {
            float scale = Mathf.Clamp(NetCodec.UnpackScale(packed), 0.3f, 1.2f);
            if (_pose != null)
                _pose.SetCrouchScale(scale);
            else if (visualRoot != null)
                visualRoot.localScale = new Vector3(1f, scale, 1f);
            if (bodyCollider == null) return;
            float h = StandHeight * scale;
            bodyCollider.height = h;
            bodyCollider.center = new Vector3(0f, h * 0.5f, 0f);
        }

        private void ApplyAim(ushort packed)
        {
            if (weaponHolder == null || packed == 0) return;
            NetCodec.UnpackAim(packed, out float height, out float pitch);
            Vector3 p = weaponHolder.localPosition;
            weaponHolder.localPosition = new Vector3(p.x, height, p.z);
            weaponHolder.localRotation = Quaternion.Euler(pitch, 0f, 0f);
        }

        private void ApplyCountry(string tag)
        {
            if (_member == null || string.IsNullOrEmpty(tag)) return;
            var id = new CountryId(tag);
            if (_member.Country != id) _member.Country = id;
        }
    }
}

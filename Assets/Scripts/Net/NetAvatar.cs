using System.Collections.Generic;
using EuropeanWar.AI;
using EuropeanWar.Combat;
using EuropeanWar.Core;
using EuropeanWar.Player;
using EuropeanWar.Sim;
using FishNet;
using FishNet.Connection;
using FishNet.Object;
using FishNet.Object.Synchronizing;
using UnityEngine;

namespace EuropeanWar.Net
{
    /// <summary>
    /// Сетевой аватар игрока в кооп-бою. У владельца повторяет его настоящего игрока и спрятан
    /// (рендеры, коллайдер и TeamMember выключены). У остальных — видимый боец с подписью, в него стреляет ИИ.
    /// Здоровье (D-028): урон второму игроку считает хост на его аватаре и пересылает владельцу;
    /// здоровье хоста берётся с его игрока. Всем остальным значение приходит через SyncVar Hp.
    /// </summary>
    public sealed class NetAvatar : NetworkBehaviour
    {
        /// <summary>Номер игрока: 0 — хост, 1 — второй игрок.</summary>
        public readonly SyncVar<int> Slot = new SyncVar<int>(-1);
        public readonly SyncVar<string> Country = new SyncVar<string>(string.Empty);
        public readonly SyncVar<float> Hp = new SyncVar<float>(100f);
        public readonly SyncVar<byte> Stance = new SyncVar<byte>(200);
        public readonly SyncVar<ushort> Aim = new SyncVar<ushort>(0);
        /// <summary>Звание этого игрока (D-032). У своего игрока хост ставит ранг кампании, у остальных — рядовой.</summary>
        public readonly SyncVar<RankId> Rank = new SyncVar<RankId>(RankId.Private);
        /// <summary>Общий снимок отряда игрока (D-032): текущий приказ у всех игроков одинаковый, публикует хост.</summary>
        public readonly SyncVar<SoldierOrder> SharedSquadOrder = new SyncVar<SoldierOrder>(SoldierOrder.FollowLeader);
        /// <summary>Сколько ИИ отряда живы по данным хоста; -1 — снимок ещё не публиковался (отряд не готов).</summary>
        public readonly SyncVar<int> SharedSquadAlive = new SyncVar<int>(-1);
        public readonly SyncVar<float> ReloadMultiplier = new SyncVar<float>(1f);
        public readonly SyncVar<int> ArtilleryRemaining = new SyncVar<int>(0);
        /// <summary>Страна противника в этом бою (у хоста — из запроса карты): клиент подстраивает под неё счёт (D-030).</summary>
        public readonly SyncVar<string> EnemyCountry = new SyncVar<string>(string.Empty);
        /// <summary>Вводная боя с карты хоста («вы атакуете …»); пусто — быстрый бой.</summary>
        public readonly SyncVar<string> Briefing = new SyncVar<string>(string.Empty);

        [SerializeField, Tooltip("Коллайдер тела: у владельца и у павшего аватара выключен")]
        private Collider bodyCollider;
        [SerializeField, Tooltip("Фигура бойца: падает, когда игрок погиб")]
        private Transform visualRoot;
        [SerializeField, Tooltip("Автомат аватара: на нём второй игрок видит выстрелы этого игрока (D-029)")]
        private Weapon weapon;
        [SerializeField] private Transform weaponHolder;
        private Weapon _sourceWeapon;
        private float _appliedReloadMultiplier = -1f;

        private const float FallSeconds = 0.45f;
        private const float PoseSendInterval = 0.1f;
        private const float MinHeightScale = 0.3f;
        private const float MaxHeightScale = 1.2f;
        private static readonly List<NetAvatar> s_all = new List<NetAvatar>();
        /// <summary>Последний опубликованный снимок отряда (D-032): отдаётся и позже подключившимся при спавне аватара.</summary>
        private static SoldierOrder s_sharedOrder = SoldierOrder.FollowLeader;
        private static int s_sharedAlive = -1;
        private static int s_lastBattleOutcome = -1;
        private Transform _source;
        private PlayerLife _sourceLife;
        private Health _sourceHealth;
        private PlayerMotor _sourceMotor;
        private PlayerCameraRig _sourceCameraRig;
        private Health _health;
        private TeamMember _member;
        private CapsuleCollider _capsule;
        private float _standHeight;
        private Vector3 _holderStandLocal;
        private SoldierPoseAnimator _pose;
        private float _nextPoseSend;
        private byte _lastStance;
        private ushort _lastAim;
        private bool _poseSent;
        private float _fall;
        private bool _battleResultPending;
        private GUIStyle _label, _shadow;

        /// <summary>Все аватары, видимые этой копии игры (включая свой).</summary>
        public static IReadOnlyList<NetAvatar> All => s_all;

        /// <summary>Свой аватар (этой копии игры) или null, пока не подключились.</summary>
        public static NetAvatar Mine
        {
            get
            {
                for (int i = 0; i < s_all.Count; i++)
                    if (s_all[i] != null && s_all[i].IsOwner) return s_all[i];
                return null;
            }
        }

        public string DisplayName => Slot.Value <= 0 ? Localization.Text("coop.playerHost", "Хост") : Localization.Text("coop.playerNumber", "Игрок {0}", Slot.Value + 1);
        // Счётчики на весь процесс (не на аватар): аватар ушедшего клиента удаляется, а итог нужен в отчёте.
        /// <summary>Хост: сколько попаданий по аватарам отправлено их владельцам.</summary>
        public static int HitsSent { get; private set; }
        /// <summary>Владелец: сколько попаданий пришло от хоста.</summary>
        public static int HitsReceived { get; private set; }
        public static int RespawnsSent { get; private set; }
        public static int RespawnsReceived { get; private set; }
        /// <summary>Владелец: сколько своих выстрелов отправлено в сеть.</summary>
        public static int ShotsSent { get; private set; }
        /// <summary>Сколько выстрелов другого игрока показано на его аватаре.</summary>
        public static int ShotsShown { get; private set; }
        /// <summary>Owner pose snapshots sent, including direct writes by the host owner.</summary>
        public static int PoseUpdatesSent { get; private set; }
        /// <summary>Owner pose snapshots received by the server RPC.</summary>
        public static int PoseUpdatesReceived { get; private set; }
        /// <summary>Non-owner SyncVar changes, counted once on a host.</summary>
        public static int StanceChangesObserved { get; private set; }
        public static int AimChangesObserved { get; private set; }
        /// <summary>Хост: сколько снимков отряда с изменениями опубликовано во все аватары (D-032).</summary>
        public static int SquadSnapshotsPublished { get; private set; }
        /// <summary>Хост: скольким игрокам разослан итог боя.</summary>
        public static int BattleEndsSent { get; private set; }
        public static int BattleEndsAcknowledged { get; private set; }
        /// <summary>Клиент: итог боя от хоста. -1 — бой идёт, 0 — поражение, 1 — победа, 2 — отступление (NetCodec).</summary>
        public static int RemoteBattleResult { get; private set; } = -1;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetCounters()
        {
            HitsSent = 0; HitsReceived = 0; RespawnsSent = 0; RespawnsReceived = 0;
            ShotsSent = 0; ShotsShown = 0; BattleEndsSent = 0; BattleEndsAcknowledged = 0; RemoteBattleResult = -1;
            PoseUpdatesSent = 0; PoseUpdatesReceived = 0; StanceChangesObserved = 0; AimChangesObserved = 0;
            SquadSnapshotsPublished = 0;
            s_sharedOrder = SoldierOrder.FollowLeader;
            s_sharedAlive = -1;
            s_lastBattleOutcome = -1;
        }

        public static void ClearRemoteBattleResult() => RemoteBattleResult = -1;

        public static void ResetSharedSquadState()
        {
            s_sharedOrder = SoldierOrder.FollowLeader;
            s_sharedAlive = -1;
            s_lastBattleOutcome = -1;
        }

        public static void ResetBattleOutcome() => s_lastBattleOutcome = -1;

        public static bool HasPendingBattleResults
        {
            get
            {
                for (int i = 0; i < s_all.Count; i++)
                {
                    NetAvatar avatar = s_all[i];
                    if (avatar != null && avatar.IsServerInitialized && avatar._battleResultPending &&
                        avatar.Owner != null && avatar.Owner.IsActive) return true;
                }
                return false;
            }
        }

        private void Awake()
        {
            _health = GetComponent<Health>();
            _member = GetComponent<TeamMember>();
            _capsule = bodyCollider as CapsuleCollider;
            _standHeight = _capsule != null ? _capsule.height : 1.8f;
            _holderStandLocal = weaponHolder != null ? weaponHolder.localPosition : Vector3.zero;
            _pose = GetComponentInChildren<SoldierPoseAnimator>(true);
            if (_pose != null && weaponHolder != null) _pose.BindWeaponHolder(weaponHolder);
            if (_health != null)
            {
                _health.Invulnerable = true;
                _health.Damaged += OnServerDamaged;
                _health.Changed += OnHealthChanged;
            }
            Hp.OnChange += OnHpChanged;
            Country.OnChange += OnCountryChanged;
            Stance.OnChange += OnStanceChanged;
            Aim.OnChange += OnAimChanged;
            ReloadMultiplier.OnChange += OnReloadMultiplierChanged;
        }

        public override void OnStartClient()
        {
            base.OnStartClient();
            if (!s_all.Contains(this)) s_all.Add(this);
            ApplyView();
        }

        public override void OnStopClient()
        {
            base.OnStopClient();
            s_all.Remove(this);
            if (_sourceLife != null) _sourceLife.Respawned -= OnOwnerRespawned;
            if (_sourceWeapon != null) _sourceWeapon.Shot -= OnOwnerShot;
            _sourceLife = null;
            _sourceWeapon = null;
            _appliedReloadMultiplier = -1f;
            _source = null;
            _sourceHealth = null;
            _sourceMotor = null;
            _sourceCameraRig = null;
            _poseSent = false;
            _nextPoseSend = 0f;
        }

        private void OnDestroy()
        {
            s_all.Remove(this);
            if (_sourceLife != null) _sourceLife.Respawned -= OnOwnerRespawned;
            if (_sourceWeapon != null) _sourceWeapon.Shot -= OnOwnerShot;
            if (_health != null)
            {
                _health.Damaged -= OnServerDamaged;
                _health.Changed -= OnHealthChanged;
            }
            Hp.OnChange -= OnHpChanged;
            Country.OnChange -= OnCountryChanged;
            Stance.OnChange -= OnStanceChanged;
            Aim.OnChange -= OnAimChanged;
            ReloadMultiplier.OnChange -= OnReloadMultiplierChanged;
        }

        /// <summary>Вызывается на сервере сразу после спавна.</summary>
        public void ServerSetup(int slot)
        {
            Slot.Value = slot;
            Country.Value = BattleSetup.PlayerCountry.Tag;
            EnemyCountry.Value = BattleSetup.EnemyCountry.Tag;
            ReloadMultiplier.Value = BattleSetup.Effects.ReloadMultiplier;
            ArtilleryRemaining.Value = BattleSetup.Effects.ArtilleryStrikes;
            // Звание (D-032): свой игрок (локальное соединение) — с рангом кампании хоста, любой другой — рядовой.
            // По номеру слота не судим: слот 0 не гарантирует локального владельца.
            Rank.Value = Owner != null && Owner.IsLocalClient ? BattleSetup.PlayerRank : RankId.Private;
            // Позднее подключение: сразу отдаём последний опубликованный снимок отряда, чтобы не ждать изменений.
            if (s_sharedAlive >= 0)
            {
                SharedSquadOrder.Value = s_sharedOrder;
                SharedSquadAlive.Value = s_sharedAlive;
            }
            var r = BattleSetup.Request;
            Briefing.Value = r == null ? string.Empty
                : r.PlayerAttacks ? Localization.Text("coop.briefAttack", "Бой с карты хоста: вы атакуете — {0}.", r.EnemyName)
                : Localization.Text("coop.briefDefend", "Бой с карты хоста: {0} атакует!", r.EnemyName);
            // Late join after the host has already ended the battle: deliver the stored outcome.
            if (s_lastBattleOutcome >= 0 && Owner != null && !Owner.IsLocalClient)
            {
                _battleResultPending = true;
                TargetBattleEnded(Owner, s_lastBattleOutcome);
            }
        }

        /// <summary>Хост видит аватар второго игрока: именно здесь считается урон по нему.</summary>
        private bool IsDamageAuthority => IsServerInitialized && !IsOwner;

        private void ApplyView()
        {
            bool mine = IsOwner;
            foreach (Renderer r in GetComponentsInChildren<Renderer>(true)) r.enabled = !mine;
            if (_member != null) _member.enabled = !mine;
            ApplyCountry(Country.Value);
            if (_health != null)
            {
                _health.Invulnerable = !IsDamageAuthority;
                if (!mine && !IsServerInitialized) _health.SetCurrent(Hp.Value);
            }
            UpdateCollider();
            if (mine) FindSource();
            else
            {
                ApplyStance(Stance.Value);
                ApplyAim(Aim.Value);
            }
        }

        private void FindSource()
        {
            PlayerLife player = FindFirstObjectByType<PlayerLife>();
            if (player == _sourceLife && player != null) return;
            if (_sourceLife != null) _sourceLife.Respawned -= OnOwnerRespawned;
            _sourceLife = player;
            _source = player != null ? player.transform : null;
            _sourceHealth = player != null ? player.GetComponent<Health>() : null;
            _sourceMotor = player != null ? player.GetComponent<PlayerMotor>() : null;
            _sourceCameraRig = player != null ? player.GetComponent<PlayerCameraRig>() : null;
            if (_sourceLife != null) _sourceLife.Respawned += OnOwnerRespawned;
            if (_sourceWeapon != null) _sourceWeapon.Shot -= OnOwnerShot;
            PlayerShooter shooter = player != null ? player.GetComponent<PlayerShooter>() : null;
            _sourceWeapon = shooter != null ? shooter.CurrentWeapon : null;
            _appliedReloadMultiplier = -1f;
            if (_sourceWeapon != null) _sourceWeapon.Shot += OnOwnerShot;
        }

        private void LateUpdate()
        {
            if (!IsOwner) return;
            if (_source == null)
            {
                FindSource();
                if (_source == null) return;
            }
            transform.SetPositionAndRotation(_source.position, _source.rotation);
            ApplySupplyToOwner();
            SendOwnerPose();
            // Аватар хоста: здоровье берётся с его настоящего игрока.
            if (IsServerInitialized && _sourceHealth != null && !Mathf.Approximately(Hp.Value, _sourceHealth.Current))
                Hp.Value = _sourceHealth.Current;
        }

        private void Update()
        {
            if (!IsClientStarted || IsOwner) return;
            UpdateCollider();
            float target = Hp.Value <= 0f ? 1f : 0f;
            if (Mathf.Approximately(_fall, target)) return;
            _fall = Mathf.MoveTowards(_fall, target, Time.deltaTime / FallSeconds);
            if (_pose != null)
            {
                _pose.SetDead(target > 0.5f);
                return;
            }
            if (visualRoot != null) visualRoot.localRotation = Quaternion.Euler(-80f * _fall * _fall, 0f, 0f);
        }

        private void UpdateCollider()
        {
            if (bodyCollider == null) return;
            bool on = !IsOwner && Hp.Value > 0f;
            if (bodyCollider.enabled != on) bodyCollider.enabled = on;
        }

        private void SendOwnerPose()
        {
            if (!IsSpawned || _sourceMotor == null || _sourceCameraRig == null ||
                Time.unscaledTime < _nextPoseSend) return;
            float height = _sourceMotor.CurrentHeight;
            float standHeight = _sourceMotor.StandHeight;
            float pitch = _sourceCameraRig.Pitch;
            if (float.IsNaN(height) || float.IsInfinity(height) ||
                float.IsNaN(standHeight) || float.IsInfinity(standHeight) || standHeight <= 0.01f ||
                float.IsNaN(pitch) || float.IsInfinity(pitch)) return;

            byte stance = NetCodec.PackScale(Mathf.Clamp(height / standHeight, MinHeightScale, MaxHeightScale));
            // First-person presentation moves the source weapon; only copy motor/camera state.
            float scale = NetCodec.UnpackScale(stance);
            ushort aim = NetCodec.PackAim(_holderStandLocal.y * scale, Mathf.Clamp(pitch, -90f, 90f));
            if (_poseSent && stance == _lastStance && aim == _lastAim) return;
            _lastStance = stance;
            _lastAim = aim;
            _poseSent = true;
            _nextPoseSend = Time.unscaledTime + PoseSendInterval;
            PoseUpdatesSent++;
            if (IsServerInitialized) SetPose(stance, aim);
            else ServerPose(stance, aim);
        }

        [ServerRpc(RequireOwnership = true)]
        private void ServerPose(byte stance, ushort aim)
        {
            PoseUpdatesReceived++;
            SetPose(stance, aim);
        }

        private void SetPose(byte stance, ushort aim)
        {
            float scale = Mathf.Clamp(NetCodec.UnpackScale(stance), MinHeightScale, MaxHeightScale);
            stance = NetCodec.PackScale(scale);
            NetCodec.UnpackAim(aim, out _, out float pitch);
            // Packed inputs are finite; canonical height prevents arbitrary client weapon offsets.
            aim = NetCodec.PackAim(_holderStandLocal.y * scale, Mathf.Clamp(pitch, -90f, 90f));
            if (Stance.Value != stance) Stance.Value = stance;
            if (Aim.Value != aim) Aim.Value = aim;
        }

        private void OnStanceChanged(byte prev, byte next, bool asServer)
        {
            if (IsOwner || (!asServer && IsServerInitialized)) return;
            if (prev != next) StanceChangesObserved++;
            ApplyStance(next);
        }

        private void OnAimChanged(ushort prev, ushort next, bool asServer)
        {
            if (IsOwner || (!asServer && IsServerInitialized)) return;
            if (prev != next) AimChangesObserved++;
            ApplyAim(next);
        }

        private void OnReloadMultiplierChanged(float prev, float next, bool asServer) => ApplySupplyToOwner();

        private void ApplySupplyToOwner()
        {
            if (!IsOwner || _sourceWeapon == null || _appliedReloadMultiplier == ReloadMultiplier.Value) return;
            _sourceWeapon.SetReloadMultiplier(ReloadMultiplier.Value);
            _appliedReloadMultiplier = ReloadMultiplier.Value;
        }

        public static void ServerPublishBattleSupport(float multiplier, int artilleryRemaining)
        {
            if (!InstanceFinder.IsServer) return;
            foreach (NetAvatar avatar in s_all)
            {
                if (avatar == null || !avatar.IsServerInitialized) continue;
                avatar.ReloadMultiplier.Value = multiplier;
                avatar.ArtilleryRemaining.Value = artilleryRemaining;
            }
        }

        private void ApplyStance(byte packed)
        {
            float scale = Mathf.Clamp(NetCodec.UnpackScale(packed), MinHeightScale, MaxHeightScale);
            if (_pose != null)
                _pose.SetCrouchScale(scale);
            else if (visualRoot != null)
                visualRoot.localScale = new Vector3(1f, scale, 1f);
            if (_capsule == null) return;
            float height = Mathf.Max(_capsule.radius * 2f, _standHeight * scale);
            _capsule.height = height;
            Vector3 center = _capsule.center;
            center.y = height * 0.5f;
            _capsule.center = center;
        }

        private void ApplyAim(ushort packed)
        {
            if (weaponHolder == null || packed == 0) return;
            NetCodec.UnpackAim(packed, out float height, out float pitch);
            height = Mathf.Clamp(height, 0f, _holderStandLocal.y * MaxHeightScale);
            weaponHolder.localPosition = new Vector3(_holderStandLocal.x, height, _holderStandLocal.z);
            weaponHolder.localRotation = Quaternion.Euler(Mathf.Clamp(pitch, -90f, 90f), 0f, 0f);
        }

        // --- урон и возрождение ---

        private void OnServerDamaged(DamageInfo info)
        {
            if (!IsDamageAuthority || Owner == null || !Owner.IsActive) return;
            HitsSent++;
            TargetDamaged(Owner, info.Amount, _health.Current, info.Point, info.Direction);
        }

        private void OnHealthChanged(float current, float max)
        {
            if (IsDamageAuthority && !Mathf.Approximately(Hp.Value, current)) Hp.Value = current;
        }

        [TargetRpc]
        private void TargetDamaged(NetworkConnection conn, float amount, float hpAfter, Vector3 point, Vector3 direction)
        {
            if (_sourceHealth == null) FindSource();
            if (_sourceHealth == null) return;
            HitsReceived++;
            _sourceHealth.TakeDamage(new DamageInfo(amount, point, direction, null));
            // Истина — у хоста: если местное здоровье разошлось, выравниваем.
            if (Mathf.Abs(_sourceHealth.Current - hpAfter) > 0.5f) _sourceHealth.SetCurrent(hpAfter);
        }

        private void OnOwnerRespawned()
        {
            if (!IsOwner || IsServerInitialized) return; // у хоста здоровье и так берётся с игрока
            RespawnsSent++;
            ServerRespawned();
        }

        [ServerRpc]
        private void ServerRespawned()
        {
            RespawnsReceived++;
            if (_health != null) _health.ResetHealth();
        }

        // --- выстрелы и итог боя (D-029) ---

        private void OnOwnerShot(Vector3 from, Vector3 to)
        {
            if (!IsOwner || !IsSpawned) return;
            ShotsSent++;
            if (IsServerInitialized) ObserversShot(from, to);
            else ServerShot(from, to);
        }

        [ServerRpc]
        private void ServerShot(Vector3 from, Vector3 to)
        {
            ObserversShot(from, to);
        }

        /// <summary>Все, кроме стрелявшего, видят вспышку и трассер и слышат выстрел на его аватаре.</summary>
        [ObserversRpc(ExcludeOwner = true)]
        private void ObserversShot(Vector3 from, Vector3 to)
        {
            ShotsShown++;
            if (weapon != null) weapon.PlayRemoteShot(from, to);
        }

        /// <summary>Хост: объект аватара игрока по его соединению (источник урона для ИИ).</summary>
        public static GameObject FindOwnedBy(NetworkConnection conn)
        {
            if (conn == null) return null;
            for (int i = 0; i < s_all.Count; i++)
                if (s_all[i] != null && s_all[i].Owner == conn) return s_all[i].gameObject;
            return null;
        }

        /// <summary>Хост: разослать итог боя вторым игрокам.</summary>
        public static void ServerAnnounceBattleEnd(bool victory, bool retreated = false)
        {
            int outcome = NetCodec.EncodeOutcome(victory, retreated);
            s_lastBattleOutcome = outcome;
            for (int i = 0; i < s_all.Count; i++)
            {
                NetAvatar a = s_all[i];
                if (a == null || !a.IsServerInitialized || a.IsOwner || a.Owner == null || !a.Owner.IsActive) continue;
                a._battleResultPending = true;
                BattleEndsSent++;
                a.TargetBattleEnded(a.Owner, outcome);
            }
        }

        [TargetRpc]
        private void TargetBattleEnded(NetworkConnection conn, int outcome)
        {
            RemoteBattleResult = outcome;
            CoopSmokeProbe.RecordBattleOutcome(outcome);
            ServerAcknowledgeBattleResult();
        }

        /// <summary>
        /// Хост: опубликовать общий снимок отряда (приказ и число живых ИИ, D-032) во все живые аватары,
        /// включая свой — значения приходят через SyncVar, без RPC. Пишет только изменения, а последний
        /// снимок запоминается для аватаров, которых спавнят позже. Только для сервера.
        /// </summary>
        public static void ServerPublishSquadState(SoldierOrder order, int alive)
        {
            if (!InstanceFinder.IsServer) return;
            s_sharedOrder = order;
            s_sharedAlive = alive;
            bool changed = false;
            for (int i = 0; i < s_all.Count; i++)
            {
                NetAvatar a = s_all[i];
                if (a == null || !a.IsServerInitialized) continue;
                if (a.SharedSquadOrder.Value != order) { a.SharedSquadOrder.Value = order; changed = true; }
                if (a.SharedSquadAlive.Value != alive) { a.SharedSquadAlive.Value = alive; changed = true; }
            }
            if (changed) SquadSnapshotsPublished++;
        }

        [ServerRpc]
        private void ServerAcknowledgeBattleResult()
        {
            if (!_battleResultPending) return;
            _battleResultPending = false;
            BattleEndsAcknowledged++;
        }

        // --- вид у наблюдателей ---

        private void OnHpChanged(float prev, float next, bool asServer)
        {
            if (asServer || _health == null || IsServerInitialized || IsOwner) return;
            _health.SetCurrent(next);
        }

        private void OnCountryChanged(string prev, string next, bool asServer)
        {
            ApplyCountry(next);
        }

        private void ApplyCountry(string tag)
        {
            if (_member == null || string.IsNullOrEmpty(tag)) return;
            var id = new CountryId(tag);
            if (_member.Country != id) _member.Country = id;
        }

        private void OnGUI()
        {
            if (IsOwner || !IsClientStarted) return;
            Camera cam = Camera.main;
            if (cam == null) return;
            Vector3 screen = cam.WorldToScreenPoint(transform.position + Vector3.up * 2.15f);
            if (screen.z <= 0f || screen.z > 200f) return;
            if (_label == null)
            {
                _label = new GUIStyle(GUI.skin.label) { fontSize = 15, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleCenter };
                _label.normal.textColor = new Color(0.55f, 0.9f, 1f);
                _shadow = new GUIStyle(_label);
                _shadow.normal.textColor = new Color(0f, 0f, 0f, 0.8f);
            }
            var rect = new Rect(screen.x - 90f, Screen.height - screen.y - 12f, 180f, 24f);
            string text = Hp.Value <= 0f ? DisplayName + " — " + Localization.Text("coop.wounded", "ранен") : DisplayName;
            GUI.Label(new Rect(rect.x + 1f, rect.y + 1f, rect.width, rect.height), text, _shadow);
            GUI.Label(rect, text, _label);
        }
    }
}

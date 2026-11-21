using System;
using System.Collections;
using EuropeanWar.Core;
using UnityEngine;
using UnityEngine.Rendering;
using Random = UnityEngine.Random;

namespace EuropeanWar.Combat
{
    /// <summary>
    /// Огнестрельное оружие (hitscan: пуля долетает мгновенно, как в большинстве шутеров).
    /// 1) По лучу прицела (центр экрана) находим, куда целится стрелок.
    /// 2) Стреляем из дула в эту точку с небольшим случайным разбросом.
    /// Не знает, кто стреляет: игрок или ИИ — прицел и отдачу передают снаружи.
    /// </summary>
    public class Weapon : MonoBehaviour
    {
        [Header("Ссылки")]
        [SerializeField, Tooltip("Характеристики. Пусто — тестовый автомат")]
        private WeaponData data;
        [SerializeField, Tooltip("Точка вылета пули. Пусто — сам объект оружия")]
        private Transform muzzle;
        [SerializeField, Tooltip("Владелец: в его коллайдеры пули не попадают. Пусто — корневой объект")]
        private GameObject owner;
        [SerializeField, Tooltip("Во что могут попадать пули")]
        private LayerMask hitMask = Physics.DefaultRaycastLayers;

        [Header("Трассеры")]
        [SerializeField] private bool showTracers = true;
        [SerializeField] private float tracerDuration = 0.05f;
        [SerializeField] private float tracerWidth = 0.025f;
        [SerializeField] private Color tracerColor = new Color(1f, 0.85f, 0.45f, 1f);

        [Header("Следы попаданий")]
        [SerializeField] private bool showImpacts = true;
        [SerializeField] private float impactSize = 0.06f;
        [SerializeField] private float impactLifetime = 5f;
        [SerializeField] private Color impactColor = new Color(0.12f, 0.1f, 0.08f, 1f);

        private const int TracerPoolSize = 8;
        private const int ImpactPoolSize = 12;

        private int _ammo;
        private bool _reloading;
        private float _reloadStartTime;
        private float _reloadMultiplier = 1f;
        private float _nextShotTime;
        private readonly RaycastHit[] _hits = new RaycastHit[32];

        private LineRenderer[] _tracers;
        private float[] _tracerHideTime;
        private int _nextTracer;
        private GameObject[] _impactMarks;
        private float[] _impactHideTime;
        private Material _tracerMaterial;
        private Material _impactMaterial;
        private Mesh _sphereMesh;
        private GameObject _effectsRoot;

        public WeaponData Data => data;
        public int AmmoInMagazine => _ammo;
        public int MagazineSize => data != null ? data.magazineSize : 0;
        public bool IsReloading => _reloading;
        /// <summary>Множитель времени перезарядки (боевой эффект с карты; по умолчанию 1).</summary>
        public float ReloadMultiplier => _reloadMultiplier;
        /// <summary>Время перезарядки с учётом множителя, не меньше 0.1 с.</summary>
        public float EffectiveReloadTime => data != null ? Mathf.Max(0.1f, data.reloadTime * _reloadMultiplier) : 0f;
        /// <summary>Прогресс перезарядки 0..1.</summary>
        public float ReloadProgress => _reloading && EffectiveReloadTime > 0f
            ? Mathf.Clamp01((Time.time - _reloadStartTime) / EffectiveReloadTime) : 0f;
        public GameObject Owner => owner;
        public Transform Muzzle => muzzle;
        /// <summary>Боец-владелец, закэшированный в Awake: «слух» ИИ не делает GetComponent на каждый выстрел.</summary>
        public TeamMember OwnerTeamMember { get; private set; }

        public event Action Fired;
        public event Action ReloadStarted;
        public event Action ReloadFinished;
        /// <summary>Изменились патроны: (в магазине, размер магазина).</summary>
        public event Action<int, int> AmmoChanged;
        /// <summary>Попали во что-то с здоровьем: (урон, дистанция, убили ли).</summary>
        public event Action<DamageInfo, float, bool> DamageDealt;

        /// <summary>Любое оружие на сцене выстрелило: (оружие, точка выстрела). По этому ИИ «слышит» стрельбу.</summary>
        public static event Action<Weapon, Vector3> AnyFired;
        /// <summary>Выстрел: (дуло, куда долетела пуля). По нему сеть показывает выстрел второму игроку (D-029).</summary>
        public event Action<Vector3, Vector3> Shot;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStatics() => AnyFired = null;

        private void Awake()
        {
            if (data == null) data = WeaponData.CreateDefaultRifle();
            if (muzzle == null) muzzle = transform;
            if (owner == null) owner = transform.root.gameObject;
            OwnerTeamMember = owner != null ? owner.GetComponent<TeamMember>() : null;
            _ammo = data.magazineSize;
        }

        private void OnDisable()
        {
            // Если оружие убрали посреди перезарядки — отменяем её.
            if (_reloading)
            {
                StopAllCoroutines();
                _reloading = false;
            }
            HideAllTracers();
            ReleaseImpacts();
        }

        private void OnDestroy()
        {
            DestroyOwned(_effectsRoot);
            DestroyOwned(_tracerMaterial);
            DestroyOwned(_impactMaterial);
            if (_impactMarks != null)
            {
                for (int i = 0; i < _impactMarks.Length; i++) DestroyOwned(_impactMarks[i]);
                _impactMarks = null;
                _impactHideTime = null;
            }
        }

        private void Update()
        {
            if (_tracers != null)
            {
                for (int i = 0; i < _tracers.Length; i++)
                {
                    if (_tracers[i] != null && _tracers[i].enabled && Time.time >= _tracerHideTime[i])
                        _tracers[i].enabled = false;
                }
            }
            HideExpiredImpacts();
        }

        // ---------- Стрельба ----------

        /// <summary>Попытаться выстрелить. false — если рано (темп), перезарядка или пустой магазин.</summary>
        public bool TryFire(IAimProvider aim, IRecoilReceiver recoil)
        {
            if (data == null || _reloading || Time.time < _nextShotTime) return false;
            if (_ammo <= 0)
            {
                StartReload();
                return false;
            }

            // Темп стрельбы без «потерь» на частоте кадров.
            float now = Time.time;
            if (now - _nextShotTime > Time.deltaTime + 0.001f) _nextShotTime = now;
            _nextShotTime += data.SecondsBetweenShots;

            _ammo--;
            AmmoChanged?.Invoke(_ammo, data.magazineSize);

            // 1) Куда целимся.
            Ray aimRay = aim != null ? aim.GetAimRay() : new Ray(muzzle.position, muzzle.forward);
            Vector3 aimPoint = FindClosestHit(aimRay.origin, aimRay.direction, data.range, out RaycastHit aimHit)
                ? aimHit.point
                : aimRay.GetPoint(data.range);

            bool aiming = aim != null && aim.IsAiming;
            Vector3 origin = muzzle.position;

            // Ствол упёрся в стену (дуло внутри препятствия)? Тогда пуля попадает в эту стену,
            // а не пролетает сквозь неё.
            Vector3 toMuzzle = origin - aimRay.origin;
            float muzzleDistance = toMuzzle.magnitude;
            if (aim != null && muzzleDistance > 0.01f &&
                FindClosestHit(aimRay.origin, toMuzzle / muzzleDistance, muzzleDistance, out RaycastHit block))
            {
                ApplyHit(block, aimRay.direction);
                SpawnImpact(block);
                Shot?.Invoke(origin, block.point);
            }
            else
            {
                // 2) Летим из дула в точку прицела.
                Vector3 toAim = aimPoint - origin;
                Vector3 direction = toAim.sqrMagnitude > 0.0001f && Vector3.Dot(toAim, aimRay.direction) > 0f
                    ? toAim.normalized
                    : aimRay.direction; // цель вплотную/позади дула — стреляем просто вдоль прицела

                direction = ApplySpread(direction, aiming ? data.aimSpreadDeg : data.hipSpreadDeg);

                Vector3 end = origin + direction * data.range;
                if (FindClosestHit(origin, direction, data.range, out RaycastHit hit))
                {
                    end = hit.point;
                    ApplyHit(hit, direction);
                    SpawnImpact(hit);
                }
                SpawnTracer(origin, end);
                Shot?.Invoke(origin, end);
            }

            recoil?.AddRecoil(data.recoilPitch * (aiming ? 0.7f : 1f),
                Random.Range(-data.recoilYawRandom, data.recoilYawRandom));

            Fired?.Invoke();
            AnyFired?.Invoke(this, origin);
            if (_ammo <= 0) StartReload(); // магазин пуст — сразу перезаряжаемся
            return true;
        }

        /// <summary>
        /// Показать чужой выстрел, пришедший по сети: трассер, вспышка и звук (через Fired),
        /// без урона и патронов. ИИ хоста «слышит» выстрел второго игрока через AnyFired (D-029).
        /// </summary>
        public void PlayRemoteShot(Vector3 from, Vector3 to)
        {
            if (!isActiveAndEnabled) return;
            SpawnTracer(from, to);
            Fired?.Invoke();
            AnyFired?.Invoke(this, from);
        }

        /// <summary>Мгновенно заполнить магазин (например, при возрождении).</summary>
        public void Refill()
        {
            if (data == null) return;
            if (_reloading)
            {
                StopAllCoroutines();
                _reloading = false;
            }
            _ammo = data.magazineSize;
            AmmoChanged?.Invoke(_ammo, data.magazineSize);
        }

        /// <summary>
        /// Задать множитель перезарядки. Положительные конечные значения ужимаются в 0.25..2;
        /// NaN, бесконечность и значения ≤ 0 сбрасывают множитель в 1.
        /// </summary>
        public void SetReloadMultiplier(float multiplier)
        {
            if (float.IsNaN(multiplier) || float.IsInfinity(multiplier) || multiplier <= 0f)
                _reloadMultiplier = 1f;
            else
                _reloadMultiplier = Mathf.Clamp(multiplier, 0.25f, 2f);
        }

        /// <summary>Начать перезарядку (если она нужна и ещё не идёт).</summary>
        public void StartReload()
        {
            if (data == null || _reloading || _ammo >= data.magazineSize || !isActiveAndEnabled) return;
            StartCoroutine(ReloadRoutine());
        }

        private IEnumerator ReloadRoutine()
        {
            _reloading = true;
            _reloadStartTime = Time.time;
            ReloadStarted?.Invoke();

            yield return new WaitForSeconds(EffectiveReloadTime);

            _ammo = data.magazineSize;
            _reloading = false;
            ReloadFinished?.Invoke();
            AmmoChanged?.Invoke(_ammo, data.magazineSize);
        }

        /// <summary>Случайное отклонение внутри конуса.</summary>
        private static Vector3 ApplySpread(Vector3 direction, float spreadDeg)
        {
            if (spreadDeg <= 0f) return direction;
            float radius = Mathf.Tan(spreadDeg * 0.5f * Mathf.Deg2Rad);
            Vector2 r = Random.insideUnitCircle * radius;
            Quaternion basis = Quaternion.LookRotation(direction);
            return (direction + basis * new Vector3(r.x, r.y, 0f)).normalized;
        }

        /// <summary>Ближайшее попадание по лучу, не считая коллайдеров владельца.</summary>
        private bool FindClosestHit(Vector3 origin, Vector3 direction, float distance, out RaycastHit closest)
        {
            closest = default;
            int count = Physics.RaycastNonAlloc(origin, direction, _hits, distance, hitMask, QueryTriggerInteraction.Ignore);
            float best = float.MaxValue;
            bool found = false;
            for (int i = 0; i < count; i++)
            {
                Collider col = _hits[i].collider;
                if (col == null || IsOwnerCollider(col)) continue;
                if (_hits[i].distance < best)
                {
                    best = _hits[i].distance;
                    closest = _hits[i];
                    found = true;
                }
            }
            return found;
        }

        private bool IsOwnerCollider(Collider col) =>
            owner != null && col.transform.IsChildOf(owner.transform);

        private void ApplyHit(RaycastHit hit, Vector3 direction)
        {
            IDamageable target = hit.collider.GetComponentInParent<IDamageable>();
            if (target != null && target.IsAlive)
            {
                var info = new DamageInfo(data.GetDamageAtDistance(hit.distance), hit.point, direction, owner);
                if (target.TakeDamage(info))
                    DamageDealt?.Invoke(info, hit.distance, !target.IsAlive);
            }

            Rigidbody body = hit.rigidbody;
            if (body != null && !body.isKinematic)
                body.AddForceAtPosition(direction * data.impactForce, hit.point, ForceMode.Impulse);
        }

        // ---------- Эффекты ----------

        private Material CreateUnlitMaterial(Color color)
        {
            Shader shader = Shader.Find("Universal Render Pipeline/Unlit");
            if (shader == null) shader = Shader.Find("Sprites/Default");
            if (shader == null) return null;

            var mat = new Material(shader);
            if (mat.HasProperty("_BaseColor")) mat.SetColor("_BaseColor", color);
            if (mat.HasProperty("_Color")) mat.SetColor("_Color", color);
            return mat;
        }

        private Transform EffectsRoot()
        {
            if (_effectsRoot == null) _effectsRoot = new GameObject($"[Эффекты] {name}");
            return _effectsRoot.transform;
        }

        private void SpawnTracer(Vector3 from, Vector3 to)
        {
            if (!showTracers || tracerDuration <= 0f) return;

            if (_tracers == null)
            {
                _tracerMaterial = CreateUnlitMaterial(tracerColor);
                if (_tracerMaterial == null) { showTracers = false; return; }

                _tracers = new LineRenderer[TracerPoolSize];
                _tracerHideTime = new float[TracerPoolSize];
                for (int i = 0; i < TracerPoolSize; i++)
                {
                    var go = new GameObject("Tracer");
                    go.transform.SetParent(EffectsRoot(), false);
                    var lr = go.AddComponent<LineRenderer>();
                    lr.sharedMaterial = _tracerMaterial;
                    lr.positionCount = 2;
                    lr.useWorldSpace = true;
                    lr.startWidth = tracerWidth;
                    lr.endWidth = tracerWidth * 0.5f;
                    lr.startColor = tracerColor;
                    lr.endColor = tracerColor;
                    lr.shadowCastingMode = ShadowCastingMode.Off;
                    lr.receiveShadows = false;
                    lr.enabled = false;
                    _tracers[i] = lr;
                }
            }

            LineRenderer tracer = _tracers[_nextTracer];
            _tracerHideTime[_nextTracer] = Time.time + tracerDuration;
            _nextTracer = (_nextTracer + 1) % _tracers.Length;
            if (tracer == null) return;

            tracer.SetPosition(0, from);
            tracer.SetPosition(1, to);
            tracer.enabled = true;
        }

        private void HideAllTracers()
        {
            if (_tracers == null) return;
            foreach (var t in _tracers)
                if (t != null) t.enabled = false;
        }

        private void SpawnImpact(RaycastHit hit)
        {
            if (!showImpacts) return;

            if (_sphereMesh == null)
            {
                // Берём стандартную сферу Unity, сам временный объект сразу удаляем.
                var temp = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                temp.SetActive(false);
                _sphereMesh = temp.GetComponent<MeshFilter>().sharedMesh;
                DestroyOwned(temp);
            }
            if (_impactMaterial == null)
            {
                _impactMaterial = CreateUnlitMaterial(impactColor);
                if (_impactMaterial == null) { showImpacts = false; return; }
            }
            if (_impactMarks == null)
            {
                _impactMarks = new GameObject[ImpactPoolSize];
                _impactHideTime = new float[ImpactPoolSize];
            }

            int slot = PickImpactSlot();
            GameObject mark = _impactMarks[slot];
            if (mark == null)
            {
                mark = CreateImpactMark();
                _impactMarks[slot] = mark;
            }
            _impactHideTime[slot] = Time.time + impactLifetime;

            // Отцепляем и задаём мировые позицию/масштаб, как у только что созданного объекта,
            // затем крепим к предмету с сохранением мировых значений — след двигается вместе с ним
            // (например, с падающей мишенью или ящиком).
            Transform t = mark.transform;
            t.SetParent(null, false);
            t.SetPositionAndRotation(hit.point + hit.normal * 0.005f, Quaternion.identity);
            t.localScale = Vector3.one * impactSize;
            t.SetParent(hit.collider.transform, true);
            mark.SetActive(true);
        }

        /// <summary>Слот пула следов: свободный (истёкший), иначе — самый старый, его и переиспользуем.</summary>
        private int PickImpactSlot()
        {
            float now = Time.time;
            int oldest = 0;
            float oldestHide = float.MaxValue;
            for (int i = 0; i < _impactHideTime.Length; i++)
            {
                if (_impactHideTime[i] <= now) return i;
                if (_impactHideTime[i] < oldestHide)
                {
                    oldestHide = _impactHideTime[i];
                    oldest = i;
                }
            }
            return oldest;
        }

        private GameObject CreateImpactMark()
        {
            var mark = new GameObject("Impact");
            mark.SetActive(false);
            mark.AddComponent<MeshFilter>().sharedMesh = _sphereMesh;
            var mr = mark.AddComponent<MeshRenderer>();
            mr.sharedMaterial = _impactMaterial;
            mr.shadowCastingMode = ShadowCastingMode.Off;
            return mark;
        }

        /// <summary>Спрятать следы с истёкшим сроком жизни (таймеры крутятся в Update, без Destroy на выстрел).</summary>
        private void HideExpiredImpacts()
        {
            if (_impactMarks == null) return;
            float now = Time.time;
            for (int i = 0; i < _impactMarks.Length; i++)
            {
                GameObject mark = _impactMarks[i];
                if (mark != null && mark.activeSelf && _impactHideTime[i] <= now)
                    mark.SetActive(false);
            }
        }

        /// <summary>Вернуть все следы в пул (оружие выключили — например, боец погиб).</summary>
        private void ReleaseImpacts()
        {
            if (_impactMarks == null) return;
            for (int i = 0; i < _impactMarks.Length; i++)
            {
                _impactHideTime[i] = 0f;
                GameObject mark = _impactMarks[i];
                if (mark != null && mark.activeSelf) mark.SetActive(false);
            }
        }

        /// <summary>Destroy в игре, DestroyImmediate в редакторе (edit-mode тесты, пресеты, инструменты).</summary>
        private static void DestroyOwned(UnityEngine.Object obj)
        {
            if (obj == null) return;
            if (Application.isPlaying) Destroy(obj);
            else DestroyImmediate(obj);
        }
    }
}

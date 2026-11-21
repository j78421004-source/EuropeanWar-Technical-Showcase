using UnityEngine;

namespace EuropeanWar.Net
{
    /// <summary>
    /// Упаковка значений для сети: меньше байт на бойца, точность закреплена тестами.
    /// </summary>
    public static class NetCodec
    {
        /// <summary>Шаг упаковки роста: 0,005 полного роста (меньше сантиметра).</summary>
        public const float ScaleStep = 1f / 200f;
        /// <summary>Шаг упаковки наклона оружия: 180° / 255 ≈ 0,7°.</summary>
        public const float PitchStep = 180f / 255f;
        /// <summary>Шаг упаковки высоты оружия: 1 см.</summary>
        public const float HeightStep = 0.01f;

        /// <summary>Доля роста бойца (стоя 1, присев ~0,6) → байт. Вне 0..1,275 обрезается.</summary>
        public static byte PackScale(float scale)
        {
            if (float.IsNaN(scale)) return 200;
            return (byte)Mathf.Clamp(Mathf.RoundToInt(scale / ScaleStep), 0, 255);
        }

        public static float UnpackScale(byte packed) => packed * ScaleStep;

        /// <summary>
        /// Держатель оружия: высота (0..2,55 м, шаг 1 см) в старшем байте, наклон (−90..90°) в младшем.
        /// Наклон принимается в любом виде (например 350° = −10°).
        /// </summary>
        public static ushort PackAim(float height, float pitch)
        {
            if (float.IsNaN(height)) height = 0f;
            if (float.IsNaN(pitch)) pitch = 0f;
            int h = Mathf.Clamp(Mathf.RoundToInt(height / HeightStep), 0, 255);
            float p = Mathf.Clamp(Mathf.DeltaAngle(0f, pitch), -90f, 90f);
            int pb = Mathf.Clamp(Mathf.RoundToInt((p + 90f) / PitchStep), 0, 255);
            return (ushort)((h << 8) | pb);
        }

        public static void UnpackAim(ushort packed, out float height, out float pitch)
        {
            height = (packed >> 8) * HeightStep;
            pitch = (packed & 0xFF) * PitchStep - 90f;
        }

        /// <summary>Итог боя для второго игрока: 0 — поражение, 1 — победа, 2 — отступление (никогда не победа).</summary>
        public static int EncodeOutcome(bool victory, bool retreated) => retreated ? 2 : victory ? 1 : 0;
        public static bool IsVictory(int outcome) => outcome == 1;
        public static bool IsRetreat(int outcome) => outcome == 2;
    }
}

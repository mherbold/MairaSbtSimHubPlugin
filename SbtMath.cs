using System;

namespace MairaSimHub.SbtPlugin
{
    // Math helpers ported from MAIRA's MathZ class.
    // Only the portions needed by the SBT plugin are included here.
    //
    // NOTE: MathF is not available in .NET Framework 4.8.
    //       All float math is performed by casting System.Math results.
    internal static class SbtMath
    {
        // Standard gravity constant (m/s²).
        public const float OneG = 9.80665f;

        // -----------------------------------------------------------------------
        // SoftLimiter
        //
        // Ported directly from MathZ.SoftLimiter in the MAIRA source.
        //
        // Values inside [-Threshold, +Threshold] pass through unchanged.
        // Values beyond that region are progressively compressed by a
        // sine-eased knee so the output approaches ±1 smoothly instead of
        // hard-clipping.  The result can very slightly exceed ±1 for very
        // large inputs, but it never grows without bound.
        // -----------------------------------------------------------------------

        private const  float SoftLimiterWidth = 1.13333f;

        // These are computed once at class-load time because Math.PI is double
        // and MathF.PI is unavailable on .NET Framework 4.8.
        private static readonly float _softLimiterHalfWidth;
        private static readonly float _softLimiterThreshold;
        private static readonly float _softLimiterWidthOverPi;

        static SbtMath()
        {
            _softLimiterHalfWidth  = SoftLimiterWidth * 0.5f;
            _softLimiterWidthOverPi = SoftLimiterWidth / (float)Math.PI;

            // Threshold = 1 - 0.5 * (halfWidth - width / PI)
            _softLimiterThreshold  = 1f - 0.5f * (_softLimiterHalfWidth - _softLimiterWidthOverPi);
        }

        public static float SoftLimiter(float value)
        {
            float absValue = Math.Abs(value);

            if (absValue <= _softLimiterThreshold)
                return value;

            // Sine-eased compression knee (mirrors MAIRA exactly).
            float sinArg = (float)Math.PI * (absValue - _softLimiterThreshold + _softLimiterHalfWidth) / SoftLimiterWidth;

            float magnitude = 1f
                + 0.5f * (absValue - _softLimiterThreshold - _softLimiterHalfWidth)
                + 0.5f * _softLimiterWidthOverPi * (float)Math.Sin(sinArg);

            return (float)Math.Sign(value) * magnitude;
        }
    }
}

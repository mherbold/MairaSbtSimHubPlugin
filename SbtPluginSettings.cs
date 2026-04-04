namespace MairaSimHub.SbtPlugin
{
    // Settings model for the MAIRA SBT SimHub plugin.
    // Serialised to/from JSON by SimHub via ReadCommonSettings / SaveCommonSettings.
    // All defaults and valid ranges mirror the MAIRA SeatBeltTensioner settings.
    public class SbtPluginSettings
    {
        // -----------------------------------------------------------------------
        // Connection
        // -----------------------------------------------------------------------

        // Master on/off switch for the entire plugin.
        public bool SbtEnabled { get; set; } = true;

        // Attempt to locate and connect to the SBT device automatically at startup.
        public bool AutoConnect { get; set; } = true;

        // -----------------------------------------------------------------------
        // Calibration angles  (degrees → converted to tenths for serial commands)
        //
        //   Minimum : default 60°,  valid range 0–90°
        //   Neutral : default 90°,  clamped between Minimum and Maximum
        //   Maximum : default 120°, valid range 90–180°
        // -----------------------------------------------------------------------

        public float MinimumAngle { get; set; } = 60f;
        public float NeutralAngle { get; set; } = 90f;
        public float MaximumAngle { get; set; } = 120f;

        // Maximum movement the firmware allows per 10 ms motion-update tick
        // (velocity limiter).  Range 5–50; sent as the ML command value.
        public float MaxMotorSpeed { get; set; } = 25f;

        // -----------------------------------------------------------------------
        // Axis scaling: G value that maps to normalised output = ±1.
        //   Default 10 G each;  valid range 0.1–50 G.
        // -----------------------------------------------------------------------

        public float SurgeMaxG { get; set; } = 10f;
        public float SwayMaxG  { get; set; } = 10f;
        public float HeaveMaxG { get; set; } = 10f;

        // -----------------------------------------------------------------------
        // Gravity subtraction
        //
        // When enabled, the gravity component along the corresponding body axis
        // is subtracted from the raw accelerometer reading before normalisation.
        // This removes the static 1 G contribution so only dynamic forces remain.
        //
        // Defaults match MAIRA: Heave subtract gravity ON, Surge/Sway OFF.
        // -----------------------------------------------------------------------

        public bool SurgeSubtractGravity { get; set; } = false;
        public bool SwaySubtractGravity  { get; set; } = false;
        public bool HeaveSubtractGravity { get; set; } = true;

        // -----------------------------------------------------------------------
        // Axis inversion
        // -----------------------------------------------------------------------

        public bool SurgeInvert { get; set; } = false;
        public bool SwayInvert  { get; set; } = false;
        public bool HeaveInvert { get; set; } = false;
    }
}

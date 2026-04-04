using GameReaderCommon;
using SimHub.Plugins;
using System;
using System.Windows.Media;

namespace MairaSimHub.SbtPlugin
{
    // -------------------------------------------------------------------------
    // MairaSbtPlugin
    //
    // SimHub plugin that drives the MAIRA Seat Belt Tensioner hardware directly
    // over USB serial without requiring the MAIRA application to be running.
    //
    // Implements:
    //   IPlugin          – Init / End lifecycle
    //   IDataPlugin      – DataUpdate called every SimHub telemetry frame (~60 Hz)
    //   IWPFSettingsV2   – Returns a WPF settings panel for the SimHub UI
    //
    // Telemetry properties consumed from GameData.NewData
    // (all from GameReaderCommon.StatusDataBase):
    //
    //   AccelerationSurge  (Nullable<double>, m/s²)
    //       Body-frame longitudinal acceleration, positive = forward.
    //       Includes the gravity contribution projected onto the longitudinal axis.
    //       Source maps to iRacing LongAccel and SimHub's normalised equivalent.
    //
    //   AccelerationSway   (Nullable<double>, m/s²)
    //       Body-frame lateral acceleration, positive = leftward (right-turn centripetal).
    //       Includes gravity projection on the lateral axis.
    //
    //   AccelerationHeave  (Nullable<double>, m/s²)
    //       Body-frame vertical acceleration, positive = upward.
    //       At rest on flat ground ≈ +9.81 m/s² (specific force includes normal-force reaction).
    //
    //   OrientationPitch   (double, degrees)
    //       Pitch angle.  Positive = nose-up.
    //       Treated as degrees to match IMotionInputData.OrientationPitchDegrees.
    //       Converted to radians internally for gravity-decomposition trig.
    //
    //   OrientationRoll    (double, degrees)
    //       Roll angle.  Positive = left-side-up.
    //       Same unit treatment as OrientationPitch.
    // -------------------------------------------------------------------------

    [PluginDescription("Drives the MAIRA Seat Belt Tensioner hardware over USB serial using SimHub telemetry. No MAIRA app required.")]
    [PluginAuthor("MAIRA")]
    [PluginName("MAIRA SBT")]
    public class MairaSbtPlugin : IPlugin, IDataPlugin, IWPFSettingsV2
    {
        // -----------------------------------------------------------------------
        // SimHub plugin interface
        // -----------------------------------------------------------------------

        public PluginManager PluginManager { get; set; }

        // Returning null is valid; SimHub shows a generic icon.
        public ImageSource PictureIcon => null;

        public string LeftMenuTitle => "MAIRA SBT";

        // -----------------------------------------------------------------------
        // Public state (SettingsControl reads these)
        // -----------------------------------------------------------------------

        public SbtPluginSettings Settings { get; private set; }

        public bool IsConnected => _isConnected;

        // -----------------------------------------------------------------------
        // Private fields
        // -----------------------------------------------------------------------

        private readonly SbtSerialHelper _serial = new SbtSerialHelper();
        private bool _isConnected = false;

        // Suppress sending duplicate SL commands when the position has not changed.
        private int _lastSentLeftTenths  = -1;
        private int _lastSentRightTenths = -1;

        // Telemetry accumulators — summed each DataUpdate frame, averaged before
        // each SBT output update.  Mirrors MAIRA's per-frame accumulation.
        private float _longAccelSum = 0f;
        private float _latAccelSum  = 0f;
        private float _vertAccelSum = 0f;
        private float _pitchSum     = 0f;
        private float _rollSum      = 0f;
        private int   _sampleCount  = 0;

        // Divides the ~60 Hz DataUpdate rate to ~20 Hz SBT output,
        // matching MAIRA's UpdateInterval = 3.
        private const int UpdateInterval = 3;
        private int _updateCounter = UpdateInterval + 2; // offset so accumulator fills before first fire

        // -----------------------------------------------------------------------
        // IPlugin / IDataPlugin lifecycle
        // -----------------------------------------------------------------------

        public void Init(PluginManager pluginManager)
        {
            SimHub.Logging.Current.Info("[MairaSbtPlugin] Init");

            Settings = this.ReadCommonSettings<SbtPluginSettings>(
                "SbtPluginSettings",
                () => new SbtPluginSettings());

            if (!Settings.SbtEnabled)
            {
                SimHub.Logging.Current.Info("[MairaSbtPlugin] SBT disabled in settings — skipping device scan.");
                return;
            }

            _serial.PortClosed += OnPortClosed;
            _serial.FindDevice();

            if (Settings.AutoConnect && _serial.DeviceFound)
                Connect();
        }

        public void DataUpdate(PluginManager pluginManager, ref GameData data)
        {
            if (!_isConnected || !Settings.SbtEnabled)
                return;

            // Accumulate telemetry each frame while the game is providing live data.
            if (data.GameRunning && data.NewData != null)
            {
                // Nullable<double> fields default to 0 if the active game reader
                // does not supply them.  Cast to float for all internal arithmetic.
                _longAccelSum += (float)(data.NewData.AccelerationSurge ?? 0.0);
                _latAccelSum  += (float)(data.NewData.AccelerationSway   ?? 0.0);
                _vertAccelSum += (float)(data.NewData.AccelerationHeave  ?? 0.0);

                // Degrees — converted to radians inside RunSbtUpdate before trig.
                _pitchSum += (float)data.NewData.OrientationPitch;
                _rollSum  += (float)data.NewData.OrientationRoll;

                _sampleCount++;
            }

            _updateCounter--;
            if (_updateCounter <= 0)
            {
                _updateCounter = UpdateInterval;
                RunSbtUpdate();
            }
        }

        public void End(PluginManager pluginManager)
        {
            SimHub.Logging.Current.Info("[MairaSbtPlugin] End");
            Disconnect();
            this.SaveCommonSettings("SbtPluginSettings", Settings);
        }

        public System.Windows.Controls.Control GetWPFSettingsControl(PluginManager pluginManager)
        {
            return new SettingsControl(this);
        }

        // -----------------------------------------------------------------------
        // Connection management
        // -----------------------------------------------------------------------

        public bool Connect()
        {
            SimHub.Logging.Current.Info("[MairaSbtPlugin] Connect requested");

            // Re-scan if we do not yet have a port (e.g. device was plugged in after Init).
            if (!_serial.DeviceFound)
            {
                _serial.FindDevice();
                if (!_serial.DeviceFound)
                {
                    SimHub.Logging.Current.Warn("[MairaSbtPlugin] MAIRA SBT not found on any COM port.");
                    return false;
                }
            }

            _isConnected = _serial.Open();

            if (_isConnected)
            {
                _lastSentLeftTenths  = -1;
                _lastSentRightTenths = -1;
                SendCalibration();
                SendMaxMovement();
                SimHub.Logging.Current.Info("[MairaSbtPlugin] Connected to MAIRA SBT.");
            }

            return _isConnected;
        }

        public void Disconnect()
        {
            _isConnected = false;
            _lastSentLeftTenths  = -1;
            _lastSentRightTenths = -1;
            _serial.Close();
            SimHub.Logging.Current.Info("[MairaSbtPlugin] Disconnected.");
        }

        private void OnPortClosed(object sender, EventArgs e)
        {
            SimHub.Logging.Current.Warn("[MairaSbtPlugin] Serial port closed unexpectedly — marking disconnected.");
            _isConnected         = false;
            _lastSentLeftTenths  = -1;
            _lastSentRightTenths = -1;
        }

        // -----------------------------------------------------------------------
        // Serial command senders
        //
        // All commands match the SBT.ino firmware protocol exactly.
        // Values are 4-digit zero-padded integers representing tenths of a degree.
        //
        //   NLxxxxRyyyy  – set neutral position (both sides symmetrical here)
        //   ALxxxxRyyyy  – set minimum position limit
        //   BLxxxxRyyyy  – set maximum position limit
        //   MLxxxxRyyyy  – set max movement per firmware motion-update (velocity limiter)
        //   SLxxxxRyyyy  – set current belt target positions
        // -----------------------------------------------------------------------

        public void SendCalibration()
        {
            if (!_isConnected)
                return;

            int minimumTenths = ClampInt((int)Math.Round(Settings.MinimumAngle * 10.0), 0,    900);
            int neutralTenths = ClampInt((int)Math.Round(Settings.NeutralAngle * 10.0), 0,   1800);
            int maximumTenths = ClampInt((int)Math.Round(Settings.MaximumAngle * 10.0), 900, 1800);

            // Ensure neutral sits within [minimum, maximum] after individual clamping.
            neutralTenths = ClampInt(neutralTenths, minimumTenths, maximumTenths);

            _serial.WriteLine($"NL{neutralTenths:D4}R{neutralTenths:D4}");
            _serial.WriteLine($"AL{minimumTenths:D4}R{minimumTenths:D4}");
            _serial.WriteLine($"BL{maximumTenths:D4}R{maximumTenths:D4}");
        }

        public void SendMaxMovement()
        {
            if (!_isConnected)
                return;

            int maxMovement = ClampInt((int)Math.Round(Settings.MaxMotorSpeed), 5, 50);
            _serial.WriteLine($"ML{maxMovement:D4}R{maxMovement:D4}");
        }

        private void SendSetPosition(int leftTenths, int rightTenths)
        {
            int minimumTenths = ClampInt((int)Math.Round(Settings.MinimumAngle * 10.0), 0,    900);
            int maximumTenths = ClampInt((int)Math.Round(Settings.MaximumAngle * 10.0), 900, 1800);

            leftTenths  = ClampInt(leftTenths,  minimumTenths, maximumTenths);
            rightTenths = ClampInt(rightTenths, minimumTenths, maximumTenths);

            // Skip the serial write if both values are identical to what was last sent.
            if (leftTenths == _lastSentLeftTenths && rightTenths == _lastSentRightTenths)
                return;

            _lastSentLeftTenths  = leftTenths;
            _lastSentRightTenths = rightTenths;

            _serial.WriteLine($"SL{leftTenths:D4}R{rightTenths:D4}");
        }

        // -----------------------------------------------------------------------
        // RunSbtUpdate  — called at ~20 Hz (every UpdateInterval DataUpdate frames)
        //
        // Reproduces MAIRA's SeatBeltTensioner.Update() method exactly:
        //   1. Average accumulated telemetry samples.
        //   2. Decompose gravity into body-frame components from pitch and roll.
        //   3. Optionally subtract gravity on each axis.
        //   4. Normalise each axis to [-1, 1] using the configured max-G values.
        //   5. Apply optional inversion per axis.
        //   6. Combine into per-shoulder signals.
        //   7. Apply soft limiter.
        //   8. Map to tenths-of-a-degree using piecewise linear mapping around neutral.
        //   9. Send SL command if positions changed.
        // -----------------------------------------------------------------------
        private void RunSbtUpdate()
        {
            if (!_isConnected || _sampleCount == 0)
                return;

            // Step 1 — Average the accumulated samples.
            float longAccelAvg = _longAccelSum / _sampleCount;
            float latAccelAvg  = _latAccelSum  / _sampleCount;
            float vertAccelAvg = _vertAccelSum / _sampleCount;
            float pitchDegAvg  = _pitchSum     / _sampleCount;
            float rollDegAvg   = _rollSum      / _sampleCount;

            // Reset accumulators for the next window.
            _longAccelSum = 0f;
            _latAccelSum  = 0f;
            _vertAccelSum = 0f;
            _pitchSum     = 0f;
            _rollSum      = 0f;
            _sampleCount  = 0;

            // Step 2 — Convert orientation from degrees to radians, then decompose gravity.
            //
            // SimHub normalises OrientationPitch/Roll to degrees (confirmed via
            // IMotionInputData.OrientationPitchDegrees / OrientationRollDegrees).
            // iRacing uses radians natively; SimHub converts them before storing.
            float pitch = pitchDegAvg * ((float)Math.PI / 180f);
            float roll  = rollDegAvg  * ((float)Math.PI / 180f);

            float cosPitch = (float)Math.Cos(pitch);
            float sinPitch = (float)Math.Sin(pitch);
            float cosRoll  = (float)Math.Cos(roll);
            float sinRoll  = (float)Math.Sin(roll);

            // Gravity contributions in car body frame (what the accelerometer reads at rest).
            //   Pitch positive = nose-up  →  forward axis tilted into gravity field
            //   Roll  positive = left-up  →  lateral axis tilted into gravity field
            float gravLong = SbtMath.OneG * -sinPitch;            // longitudinal gravity component
            float gravLat  = SbtMath.OneG *  cosPitch * sinRoll;  // lateral gravity component
            float gravVert = SbtMath.OneG *  cosPitch * cosRoll;  // vertical gravity component (≈9.81 when flat)

            // Step 3 — Optionally subtract gravity from each axis.
            float longAccel = Settings.SurgeSubtractGravity ? longAccelAvg - gravLong : longAccelAvg;
            float latAccel  = Settings.SwaySubtractGravity  ? latAccelAvg  - gravLat  : latAccelAvg;
            float vertAccel = Settings.HeaveSubtractGravity ? vertAccelAvg - gravVert : vertAccelAvg;

            // Step 4 — Normalise each axis to [-1, 1] (mirrors MAIRA exactly).
            //
            //   Surge: braking tightens both belts → normalised from −longAccel
            //          positive surge = braking (longAccel < 0) → belts tighten
            //   Sway:  positive biases right belt tighter, left belt looser
            //          positive sway = left-hand corner (latAccel > 0)
            //   Heave: normalised from −vertAccel; crests (low G) tighten both belts
            float surgeNorm = ClampFloat(-longAccel / SbtMath.OneG / Settings.SurgeMaxG, -1f, 1f);
            float swayNorm  = ClampFloat( latAccel  / SbtMath.OneG / Settings.SwayMaxG,  -1f, 1f);
            float heaveNorm = ClampFloat(-vertAccel / SbtMath.OneG / Settings.HeaveMaxG, -1f, 1f);

            // Step 5 — Optional per-axis inversion.
            if (Settings.SurgeInvert) surgeNorm = -surgeNorm;
            if (Settings.SwayInvert)  swayNorm  = -swayNorm;
            if (Settings.HeaveInvert) heaveNorm = -heaveNorm;

            // Step 6 — Combine into per-shoulder signals (mirrors MAIRA exactly).
            //   Left shoulder:  surge + heave − sway
            //   Right shoulder: surge + heave + sway
            float leftCombined  = surgeNorm + heaveNorm - swayNorm;
            float rightCombined = surgeNorm + heaveNorm + swayNorm;

            // Step 7 — Soft limiter (ported from MathZ.SoftLimiter in MAIRA).
            float limitedLeft  = SbtMath.SoftLimiter(leftCombined);
            float limitedRight = SbtMath.SoftLimiter(rightCombined);

            // Step 8 — Map normalised signal to tenths-of-a-degree.
            //
            // Piecewise linear around neutral:
            //   positive signal maps [0, 1] → [neutral, maximum]
            //   negative signal maps [-1, 0] → [minimum, neutral]
            // This preserves the full range on each side regardless of where
            // neutral sits within [minimum, maximum].
            int minimumTenths = ClampInt((int)Math.Round(Settings.MinimumAngle * 10.0), 0,    900);
            int neutralTenths = ClampInt((int)Math.Round(Settings.NeutralAngle * 10.0), 0,   1800);
            int maximumTenths = ClampInt((int)Math.Round(Settings.MaximumAngle * 10.0), 900, 1800);
            neutralTenths = ClampInt(neutralTenths, minimumTenths, maximumTenths);

            int leftTargetTenths;
            if (limitedLeft >= 0f)
                leftTargetTenths = ClampInt(
                    (int)Math.Round(limitedLeft  * (maximumTenths - neutralTenths) + neutralTenths),
                    minimumTenths, maximumTenths);
            else
                leftTargetTenths = ClampInt(
                    (int)Math.Round(limitedLeft  * (neutralTenths - minimumTenths) + neutralTenths),
                    minimumTenths, maximumTenths);

            int rightTargetTenths;
            if (limitedRight >= 0f)
                rightTargetTenths = ClampInt(
                    (int)Math.Round(limitedRight * (maximumTenths - neutralTenths) + neutralTenths),
                    minimumTenths, maximumTenths);
            else
                rightTargetTenths = ClampInt(
                    (int)Math.Round(limitedRight * (neutralTenths - minimumTenths) + neutralTenths),
                    minimumTenths, maximumTenths);

            // Step 9 — Send to hardware (skipped if unchanged).
            SendSetPosition(leftTargetTenths, rightTargetTenths);
        }

        // -----------------------------------------------------------------------
        // Helpers
        //
        // Math.Clamp is not available in .NET Framework 4.8.
        // -----------------------------------------------------------------------

        private static int ClampInt(int value, int min, int max)
            => value < min ? min : (value > max ? max : value);

        private static float ClampFloat(float value, float min, float max)
            => value < min ? min : (value > max ? max : value);
    }
}

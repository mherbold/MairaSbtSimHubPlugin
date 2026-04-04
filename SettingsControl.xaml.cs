using System.Windows.Controls;

namespace MairaSimHub.SbtPlugin
{
    public partial class SettingsControl : UserControl
    {
        // Reference to the plugin kept for button click handlers that need
        // to call Connect / Disconnect / SendCalibration / SendMaxMovement.
        public MairaSbtPlugin Plugin { get; }

        public SettingsControl()
        {
            InitializeComponent();
        }

        public SettingsControl(MairaSbtPlugin plugin) : this()
        {
            Plugin = plugin;

            // Bind all sliders and toggles to the settings object.
            // TwoWay bindings in the XAML write slider/toggle changes back to
            // Settings properties automatically; no INotifyPropertyChanged needed
            // because we only need UI → model direction on user interaction.
            DataContext = plugin.Settings;

            UpdateConnectionStatus();
        }

        // -----------------------------------------------------------------------
        // Connection buttons
        // -----------------------------------------------------------------------

        private void ConnectButton_Click(object sender, System.Windows.RoutedEventArgs e)
        {
            Plugin.Connect();
            UpdateConnectionStatus();
        }

        private void DisconnectButton_Click(object sender, System.Windows.RoutedEventArgs e)
        {
            Plugin.Disconnect();
            UpdateConnectionStatus();
        }

        // -----------------------------------------------------------------------
        // Calibration / speed apply buttons
        //
        // The user adjusts the angle sliders to the desired values and then
        // clicks Apply to push the NL / AL / BL commands to the firmware.
        // Likewise for the ML (max-movement) command.
        // -----------------------------------------------------------------------

        private void ApplyCalibrationButton_Click(object sender, System.Windows.RoutedEventArgs e)
        {
            Plugin.SendCalibration();
        }

        private void ApplyMotorSpeedButton_Click(object sender, System.Windows.RoutedEventArgs e)
        {
            Plugin.SendMaxSpeed();
        }

        // -----------------------------------------------------------------------
        // Status display
        // -----------------------------------------------------------------------

        private void UpdateConnectionStatus()
        {
            if (ConnectionStatusText == null)
                return;

            if (Plugin.IsConnected)
            {
                ConnectionStatusText.Text = "Status: Connected";
            }
            else if (Plugin.Settings.SbtEnabled)
            {
                ConnectionStatusText.Text = Plugin.Settings.AutoConnect
                    ? "Status: Not connected  (device may not be plugged in)"
                    : "Status: Not connected";
            }
            else
            {
                ConnectionStatusText.Text = "Status: Plugin disabled";
            }
        }
    }
}

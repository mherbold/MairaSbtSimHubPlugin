using System;
using System.IO.Ports;
using System.Management;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace MairaSimHub.SbtPlugin
{
    // Manages USB serial communication with the MAIRA SBT device.
    //
    // Adapted from MAIRA's UsbSerialPortHelper, trimmed to only what the
    // SBT SimHub plugin needs:
    //   - COM port scan using WMI Win32_PnPEntity
    //   - Handshake-based device identification
    //   - Open / Close lifecycle with background port-health monitor
    //   - WriteLine for sending firmware commands
    //
    // Serial parameters: 115200 baud, 8N1, no hardware handshake, ASCII, "\n" line terminator.
    internal sealed class SbtSerialHelper : IDisposable
    {
        // True once FindDevice has successfully located the MAIRA SBT port.
        public bool DeviceFound => _portName != string.Empty;

        // Raised on the thread-pool when the port closes unexpectedly.
        // The plugin subscribes to this to reset its connected state.
        public event EventHandler PortClosed;

        private const string HandshakeQuery    = "WHAT ARE YOU?";
        private const string HandshakeResponse = "MAIRA SBT";
        private const int    BaudRate          = 115200;

        private string _portName = string.Empty;

        private SerialPort                _serialPort = null;
        private CancellationTokenSource   _monitorCts = null;

        // All access to _serialPort is guarded by this lock to keep
        // WriteLine calls on the DataUpdate thread safe against the
        // background monitor calling Close on port loss.
        private readonly object _lock = new object();

        // -----------------------------------------------------------------------
        // FindDevice
        //
        // Scans Win32_PnPEntity COM entries and sends the handshake to each.
        // Stops at the first port that replies with "MAIRA SBT".
        // Safe to call if DeviceFound is already true (returns immediately).
        // -----------------------------------------------------------------------
        public void FindDevice()
        {
            if (DeviceFound)
                return;

            SimHub.Logging.Current.Info("[SbtSerialHelper] Scanning COM ports for MAIRA SBT...");

            try
            {
                using (var searcher = new ManagementObjectSearcher(
                    "SELECT * FROM Win32_PnPEntity WHERE Name LIKE '%(COM%'"))
                {
                    foreach (ManagementObject device in searcher.Get())
                    {
                        var name = device["Name"]?.ToString();
                        if (string.IsNullOrEmpty(name))
                            continue;

                        // Extract the COM port name from e.g. "USB Serial Device (COM4)".
                        int startIndex = name.IndexOf("(COM");
                        if (startIndex < 0)
                            continue;

                        int endIndex = name.IndexOf(')', startIndex);
                        if (endIndex < 0)
                            continue;

                        string portName = name.Substring(startIndex + 1, endIndex - startIndex - 1);

                        if (TryHandshake(portName))
                        {
                            _portName = portName;
                            SimHub.Logging.Current.Info($"[SbtSerialHelper] MAIRA SBT found on {portName}");
                            break;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                SimHub.Logging.Current.Error($"[SbtSerialHelper] COM scan error: {ex.Message}");
            }

            if (!DeviceFound)
                SimHub.Logging.Current.Info("[SbtSerialHelper] MAIRA SBT not found on any COM port.");
        }

        // Opens portName, sends the handshake query, waits 200 ms, checks the response.
        private static bool TryHandshake(string portName)
        {
            try
            {
                using (var port = new SerialPort(portName, BaudRate, Parity.None, 8, StopBits.One)
                {
                    Handshake    = Handshake.None,
                    Encoding     = Encoding.ASCII,
                    ReadTimeout  = 500,
                    WriteTimeout = 500,
                    NewLine      = "\n"
                })
                {
                    port.Open();
                    port.DiscardInBuffer();
                    port.DiscardOutBuffer();
                    port.WriteLine(HandshakeQuery);

                    Thread.Sleep(200);

                    string response = port.ReadExisting()?.Trim() ?? string.Empty;
                    return response.IndexOf(HandshakeResponse, StringComparison.OrdinalIgnoreCase) >= 0;
                }
            }
            catch
            {
                // Port busy, not present, or wrong device — skip silently.
                return false;
            }
        }

        // -----------------------------------------------------------------------
        // Open
        //
        // Opens the discovered port and starts the background health monitor.
        // Returns true on success, false if no device was found or the port
        // could not be opened.
        // -----------------------------------------------------------------------
        public bool Open()
        {
            if (!DeviceFound)
            {
                SimHub.Logging.Current.Warn("[SbtSerialHelper] Open called but no device port is known.");
                return false;
            }

            lock (_lock)
            {
                if (_serialPort != null)
                    return _serialPort.IsOpen;

                _serialPort = new SerialPort(_portName, BaudRate, Parity.None, 8, StopBits.One)
                {
                    Handshake    = Handshake.None,
                    Encoding     = Encoding.ASCII,
                    ReadTimeout  = 3000,
                    WriteTimeout = 3000,
                    NewLine      = "\n"
                };

                try
                {
                    _serialPort.Open();
                    _serialPort.DiscardInBuffer();
                    _serialPort.DiscardOutBuffer();

                    _monitorCts = new CancellationTokenSource();
                    Task.Run(() => MonitorPort(_monitorCts.Token));

                    SimHub.Logging.Current.Info($"[SbtSerialHelper] Port {_portName} opened.");
                    return true;
                }
                catch (Exception ex)
                {
                    SimHub.Logging.Current.Error($"[SbtSerialHelper] Failed to open {_portName}: {ex.Message}");
                    _serialPort.Dispose();
                    _serialPort = null;
                    return false;
                }
            }
        }

        // -----------------------------------------------------------------------
        // Close
        //
        // Cancels the monitor, flushes, and closes the port.
        // Safe to call multiple times or when already closed.
        // -----------------------------------------------------------------------
        public void Close()
        {
            // Cancel the monitor task first so it does not race with our cleanup.
            _monitorCts?.Cancel();
            _monitorCts = null;

            lock (_lock)
            {
                if (_serialPort == null)
                    return;

                if (_serialPort.IsOpen)
                {
                    try { _serialPort.BaseStream.Flush(); } catch { }
                    try { _serialPort.Close();            } catch { }
                }

                _serialPort.Dispose();
                _serialPort = null;

                SimHub.Logging.Current.Info("[SbtSerialHelper] Port closed.");
            }
        }

        public void Dispose()
        {
            GC.SuppressFinalize(this);
            Close();
        }

        // -----------------------------------------------------------------------
        // WriteLine
        //
        // Sends text + "\n" to the firmware.
        // Exceptions are logged and swallowed so a write error does not
        // crash the SimHub DataUpdate loop.
        // -----------------------------------------------------------------------
        public void WriteLine(string line)
        {
            lock (_lock)
            {
                if (_serialPort == null || !_serialPort.IsOpen)
                    return;

                try
                {
                    _serialPort.WriteLine(line);
                }
                catch (Exception ex)
                {
                    SimHub.Logging.Current.Error($"[SbtSerialHelper] Write error: {ex.Message}");
                }
            }
        }

        // -----------------------------------------------------------------------
        // MonitorPort  (background Task)
        //
        // Polls IsOpen every second. If the port has closed unexpectedly
        // (e.g. USB unplugged), calls Close() to clean up and raises PortClosed
        // so the plugin can update its connected state.
        // -----------------------------------------------------------------------
        private async Task MonitorPort(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(1000, token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }

                bool portLost;
                lock (_lock)
                {
                    portLost = _serialPort == null || !_serialPort.IsOpen;
                }

                if (portLost)
                {
                    SimHub.Logging.Current.Warn("[SbtSerialHelper] Port lost — triggering PortClosed.");
                    Close();
                    PortClosed?.Invoke(this, EventArgs.Empty);
                    break;
                }
            }
        }
    }
}

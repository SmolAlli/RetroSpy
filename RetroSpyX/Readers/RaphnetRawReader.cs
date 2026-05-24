using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Avalonia.Threading;
using HidSharp;

namespace RetroSpy.Readers
{
    public class RaphnetRawReader : IControllerReader
    {
        // TODO:
        // Fix issue with everything not closing properly on exit
        // Fix issue with closing raphnet viewer taking a while
        // Test stick max range with firmware range changes, and with gcc etc
        private const int RAPHNET_VID = 0x289B;

        private const double TIMER_MS = 30;
        private static bool _shutdown;
        private readonly HidStream _stream;
        private Thread? _readerThread;
        private DispatcherTimer? _timer;
        private readonly int _id;

        private static readonly string?[] BUTTONS = {
            "a", "b", "z", "start", "l", "r", "cup", "cdown", "cleft", "cright", "up", "down", "left", "right", null, null
        };
        private const int PACKET_SIZE = 15;
        private const int STICK_MAX_RANGE = 16000;
        private const int STICK_MULTIPLIER = 200;
        const int BYTES_FOR_DATA = 2;
        const int STICK_OFFSET = 1;
        private byte[]? buttons;
        private ushort[]? axes;

        private static int ConvertStickToN64Values(ushort input, bool invert = false)
        {
            var re = (input - STICK_MAX_RANGE) / STICK_MULTIPLIER;
            return invert ? re * -1 : re;
        }

        private static float ReadStick(int input)
        {
            return (float)input / 128;
        }

        private static int GetUnsignedValue(int value)
        {
            if (value < 0)
            {
                return (value * -1) + 128;
            }
            return value;
        }

        public RaphnetRawReader(int id)
        {
            _id = id;

            var deviceList = DeviceList.Local.GetHidDevices(RAPHNET_VID);

            HidDevice selectedDevice = deviceList.ToArray()[_id];
            selectedDevice.TryOpen(out _stream);

            _stream.ReadTimeout = Timeout.Infinite;
            _readerThread = new Thread(ReadController);
            _readerThread.Start();

            _timer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(TIMER_MS)
            };
            _timer.Tick += Tick;
            _timer.Start();
        }

        public event EventHandler<ControllerStateEventArgs>? ControllerStateChanged;
        public event EventHandler? ControllerDisconnected;

        public void Finish()
        {
            _stream.ReadTimeout = 1;
            _shutdown = true;
            _readerThread?.Join();
            _readerThread = null;

            _timer?.Stop();
            _timer = null;
            try
            {
                _stream.Close();
            }
            catch (Exception)
            {
                return;
            }
        }

        public void ReadController()
        {
            while (!_shutdown)
            {
                byte[] packet = _stream.Read();

                if (packet == null)
                {
                    throw new ArgumentNullException(nameof(packet));
                }

                if (packet.Length != PACKET_SIZE || packet.Length <= 0)
                {
                    Finish();
                    ControllerDisconnected?.Invoke(this, EventArgs.Empty);
                    _stream.ReadTimeout = 1;
                    return;
                }

                buttons = [.. packet.Skip(packet.Length - BYTES_FOR_DATA)]; // Last 2 bytes for buttons
                ushort _x = BitConverter.ToUInt16([.. packet.Skip(STICK_OFFSET).Take(BYTES_FOR_DATA)]);
                ushort _y = BitConverter.ToUInt16([.. packet.Skip(STICK_OFFSET + BYTES_FOR_DATA).Take(BYTES_FOR_DATA)]);
                axes = [_x, _y];
            }
        }


        private void Tick(object? sender, EventArgs e)
        {
            ControllerStateBuilder state = new();

            if (buttons != null)
            {
                for (int i = 0; i < BUTTONS.Length; ++i)
                {
                    if (string.IsNullOrEmpty(BUTTONS[i]))
                    {
                        continue;
                    }
                    int bitPacket = (buttons[i / 8] >> (i % 8)) & 0x1;

                    state.SetButton(BUTTONS[i], bitPacket != 0x00);
                }
            }

            if (axes != null)
            {
                int x = ConvertStickToN64Values(axes[0]);
                int y = ConvertStickToN64Values(axes[1], invert: true);
                int unsignedX = GetUnsignedValue(x);
                int unsignedY = GetUnsignedValue(y);
                state.SetAnalog("stick_x", ReadStick(x), unsignedX);
                state.SetAnalog("stick_y", ReadStick(y), unsignedY);

                SignalTool.SetMouseProperties(ReadStick(x), ReadStick(y), unsignedX, unsignedY, state);
            }

            ControllerStateChanged?.Invoke(this, state.Build());
        }


        public static IEnumerable<int>? GetDevices()
        {

            var deviceList = DeviceList.Local.GetHidDevices(RAPHNET_VID).ToArray();

            return [.. Enumerable.Range(0, deviceList.Length - 1)];
        }
    }
}
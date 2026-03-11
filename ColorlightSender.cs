using SharpPcap;
using SharpPcap.LibPcap;

namespace ColorlightDemo;

/// <summary>
/// Sends pixel data to a Colorlight 5A-75B receiver card using the
/// reverse-engineered Ethernet protocol (raw Layer-2 frames).
///
/// Uses Npcap's SendQueue to batch all packets and transmit them at
/// wire speed, matching LEDVision's timing behavior.
///
/// Protocol (verified against LEDVision Wireshark capture):
///   Frame layout: [6 dst MAC][6 src MAC][1 packet type][N data bytes]
///   - Byte 12 = packet type (NOT a standard 2-byte EtherType)
///   - Byte 13+ = data payload
///
///   Packet types:
///     0x55 = pixel data: [row_hi, row_lo, off_hi, off_lo, cnt_hi, cnt_lo, 0x08, 0x88, BGR...]
///     0x0A = brightness:  [bright, bright, bright, 0xFF, ...]
///     0x01 = display sync: [0x07, ...zeros..., bright@22, 0x05@23, ...bright@25-27...]
///
///   Fixed MACs: dst=11:22:33:44:55:66, src=22:22:33:44:55:66
///
///   Display cycle (from capture): 3×Sync → 2×Brightness → 256 rows × 2 each
/// </summary>
public sealed class ColorlightSender : IDisposable
{
    private static readonly byte[] DestinationMac = [0x11, 0x22, 0x33, 0x44, 0x55, 0x66];
    private static readonly byte[] SourceMac = [0x22, 0x22, 0x33, 0x44, 0x55, 0x66];

    private const byte PacketTypePixelData = 0x55;
    private const byte PacketTypeBrightness = 0x0A;
    private const byte PacketTypeSync = 0x01;

    // Match LEDVision capture exactly: 256×256 padded, rows sent twice
    private const int HardwareColumns = 256;
    private const int HardwareRows = 256;
    private const int RowDuplicates = 2;
    private const int SyncRepeat = 3;
    private const int BrightnessRepeat = 2;

    private const int MinFrameSize = 60;
    private const int FrameHeaderSize = 13; // 6 dst + 6 src + 1 packet type

    // Frame sizes matching capture: sync=112, brightness=77, pixel=789
    private const int SyncFrameSize = 112;       // 13 + 99
    private const int BrightnessFrameSize = 77;   // 13 + 64
    private const int PixelFrameSize = 789;       // 13 + 8 + 256*3

    private readonly PcapDevice _device;
    private byte _brightness;

    public ColorlightSender(string interfaceName)
    {
        var devices = CaptureDeviceList.Instance;
        if (devices.Count == 0)
            throw new InvalidOperationException(
                "No capture devices found. Make sure Npcap is installed (https://npcap.com).");

        _device = devices
            .OfType<LibPcapLiveDevice>()
            .FirstOrDefault(d =>
                (d.Description?.Contains(interfaceName, StringComparison.OrdinalIgnoreCase) ?? false) ||
                (d.Name?.Contains(interfaceName, StringComparison.OrdinalIgnoreCase) ?? false))
            ?? throw new InvalidOperationException(
                $"Network interface '{interfaceName}' not found.\n" +
                $"Run with --list-nics to see available interfaces.");

        _device.Open();
    }

    /// <summary>
    /// Sends a complete display cycle using SendQueue for batch transmission.
    /// Sync → Brightness → screen rows × 2 each → Sync.
    /// Only actual screen rows are sent (not padded to 256).
    /// </summary>
    /// <param name="sync">Send sync packets to swap the display buffer.
    /// Only set true when pixel data has changed.</param>
    public void SendFrame(byte[] rgbPixels, int screenWidth, int screenHeight, bool sync)
    {
        int rowPackets = screenHeight * RowDuplicates;
        int queueSize = BrightnessRepeat * (BrightnessFrameSize + 16)
                      + rowPackets * (PixelFrameSize + 16)
                      + (sync ? SyncRepeat * (SyncFrameSize + 16) : 0)
                      + 1024;

        using var queue = new SendQueue(queueSize, timeResolution: TimestampResolution.Nanosecond);

        // 1) Brightness frames
        var brightnessFrame = BuildBrightnessFrame();
        for (int i = 0; i < BrightnessRepeat; i++)
            queue.Add(brightnessFrame);

        // 2) Pixel data: only actual screen rows, each sent twice
        var rowFrame = new byte[PixelFrameSize];
        Buffer.BlockCopy(DestinationMac, 0, rowFrame, 0, 6);
        Buffer.BlockCopy(SourceMac, 0, rowFrame, 6, 6);
        rowFrame[12] = PacketTypePixelData;
        rowFrame[15] = 0; // offset_hi
        rowFrame[16] = 0; // offset_lo
        rowFrame[17] = (byte)(HardwareColumns >> 8);   // count_hi
        rowFrame[18] = (byte)(HardwareColumns & 0xFF); // count_lo
        rowFrame[19] = 0x08; // flag1
        rowFrame[20] = 0x88; // flag2
        int pixelDataOffset = FrameHeaderSize + 8; // byte 21

        for (int row = 0; row < screenHeight; row++)
        {
            rowFrame[13] = (byte)(row >> 8);
            rowFrame[14] = (byte)(row & 0xFF);

            Array.Clear(rowFrame, pixelDataOffset, HardwareColumns * 3);

            int bytesToCopy = Math.Min(screenWidth, HardwareColumns) * 3;
            int srcOffset = row * screenWidth * 3;
            Buffer.BlockCopy(rgbPixels, srcOffset, rowFrame, pixelDataOffset, bytesToCopy);

            queue.Add(rowFrame);
            queue.Add(rowFrame);
        }

        // 3) Sync only when data changed (swap display to show new frame)
        if (sync)
        {
            var syncFrame = BuildSyncFrame();
            for (int i = 0; i < SyncRepeat; i++)
                queue.Add(syncFrame);
        }

        queue.Transmit(_device, sync ? SendQueueTransmitModes.Synchronized : SendQueueTransmitModes.Normal);
    }

    public void SetBrightness(byte brightness)
    {
        _brightness = brightness;
    }

    private byte[] BuildBrightnessFrame()
    {
        var frame = new byte[BrightnessFrameSize];
        Buffer.BlockCopy(DestinationMac, 0, frame, 0, 6);
        Buffer.BlockCopy(SourceMac, 0, frame, 6, 6);
        frame[12] = PacketTypeBrightness;
        frame[13] = _brightness;
        frame[14] = _brightness;
        frame[15] = _brightness;
        frame[16] = 0xFF;
        return frame;
    }

    private byte[] BuildSyncFrame()
    {
        var frame = new byte[SyncFrameSize];
        Buffer.BlockCopy(DestinationMac, 0, frame, 0, 6);
        Buffer.BlockCopy(SourceMac, 0, frame, 6, 6);
        frame[12] = PacketTypeSync;
        frame[13] = 0x07;
        frame[13 + 22] = _brightness; // byte 35
        frame[13 + 23] = 0x05;        // byte 36
        frame[13 + 25] = _brightness; // byte 38
        frame[13 + 26] = _brightness; // byte 39
        frame[13 + 27] = _brightness; // byte 40
        return frame;
    }

    public static void ListInterfaces()
    {
        Console.WriteLine("Available capture devices:");
        Console.WriteLine();

        var devices = CaptureDeviceList.Instance;
        for (int i = 0; i < devices.Count; i++)
        {
            var dev = devices[i];
            var mac = (dev as ILiveDevice)?.MacAddress;
            Console.WriteLine($"  [{i}] {dev.Name}");
            Console.WriteLine($"       {dev.Description}");
            if (mac != null)
                Console.WriteLine($"       MAC: {mac}");
            Console.WriteLine();
        }

        if (devices.Count == 0)
            Console.WriteLine("  (none found - is Npcap installed?)");
    }

    public void Dispose()
    {
        _device?.Close();
    }
}

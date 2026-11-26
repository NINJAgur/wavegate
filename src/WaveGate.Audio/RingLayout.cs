namespace WaveGate.Audio;

/// <summary>Byte-for-byte mirror of src/WaveGate.Apo/SharedRing.h. Change both together.</summary>
internal static class RingLayout
{
    public const string Name = @"Global\WaveGate.MicRing.v1";
    public const uint Magic = 0x42524757; // "WGRB"
    public const uint Version = 1;
    public const int Capacity = 1 << 15;  // mono float samples
    public const int Mask = Capacity - 1;
    public const int HeaderSize = 256;
    public const int TotalSize = HeaderSize + Capacity * sizeof(float);

    public const int MagicOffset = 0;
    public const int VersionOffset = 4;
    public const int CapacityOffset = 8;
    public const int SampleRateOffset = 12;
    public const int WritePosOffset = 64;
    public const int ReadPosOffset = 128;
    public const int OwnerIdOffset = 192;
    public const int OwnerBeatOffset = 200;

    public const long OwnerTimeoutMs = 500;
}

using System.IO.MemoryMappedFiles;

namespace WaveGate.Audio;

/// <summary>
/// Producer side of the shared ring. The APO creates the mapping (it lives in
/// session 0 and has SeCreateGlobalPrivilege); we only ever open it.
/// TryOpen is called from a timer; Write is called from the audio thread.
/// </summary>
public sealed unsafe class SharedRingWriter : IDisposable
{
    private sealed class Session(MemoryMappedFile file, MemoryMappedViewAccessor view, byte* basePtr)
    {
        public readonly MemoryMappedFile File = file;
        public readonly MemoryMappedViewAccessor View = view;
        public readonly byte* Base = basePtr;
    }

    private volatile Session? _session;

    public bool IsOpen => _session is not null;

    /// <summary>Sample rate published by the consuming APO, or 0 if none has claimed the ring yet.</summary>
    public int SampleRate
    {
        get
        {
            var s = _session;
            return s is null ? 0 : (int)Volatile.Read(ref *(uint*)(s.Base + RingLayout.SampleRateOffset));
        }
    }

    /// <summary>True while some APO instance is actively draining the ring.</summary>
    public bool IsConsumerActive
    {
        get
        {
            var s = _session;
            if (s is null) return false;
            var owner = Volatile.Read(ref *(ulong*)(s.Base + RingLayout.OwnerIdOffset));
            var beat = Volatile.Read(ref *(long*)(s.Base + RingLayout.OwnerBeatOffset));
            return owner != 0 && Environment.TickCount64 - beat < RingLayout.OwnerTimeoutMs;
        }
    }

    public bool TryOpen()
    {
        if (_session is not null) return true;

        MemoryMappedFile? file = null;
        MemoryMappedViewAccessor? view = null;
        try
        {
            file = MemoryMappedFile.OpenExisting(RingLayout.Name, MemoryMappedFileRights.ReadWrite);
            view = file.CreateViewAccessor(0, RingLayout.TotalSize, MemoryMappedFileAccess.ReadWrite);

            byte* ptr = null;
            view.SafeMemoryMappedViewHandle.AcquirePointer(ref ptr);
            ptr += view.PointerOffset;

            if (Volatile.Read(ref *(uint*)(ptr + RingLayout.MagicOffset)) != RingLayout.Magic ||
                *(uint*)(ptr + RingLayout.VersionOffset) != RingLayout.Version ||
                *(uint*)(ptr + RingLayout.CapacityOffset) != RingLayout.Capacity)
            {
                view.SafeMemoryMappedViewHandle.ReleasePointer();
                view.Dispose();
                file.Dispose();
                return false;
            }

            _session = new Session(file, view, ptr);
            return true;
        }
        catch (Exception e) when (e is FileNotFoundException or UnauthorizedAccessException or IOException)
        {
            // No APO loaded yet (nobody is capturing from the mic), or an ACL problem. Retry later.
            view?.Dispose();
            file?.Dispose();
            return false;
        }
    }

    /// <summary>Appends mono samples. Never blocks; overwrites oldest data if the consumer is absent.</summary>
    public void Write(ReadOnlySpan<float> samples)
    {
        var s = _session;
        if (s is null || samples.IsEmpty) return;

        if (samples.Length > RingLayout.Capacity)
            samples = samples[^RingLayout.Capacity..];

        ref long writePos = ref *(long*)(s.Base + RingLayout.WritePosOffset);
        var data = (float*)(s.Base + RingLayout.HeaderSize);

        long w = Volatile.Read(ref writePos);
        int start = (int)(w & RingLayout.Mask);
        int first = Math.Min(samples.Length, RingLayout.Capacity - start);

        samples[..first].CopyTo(new Span<float>(data + start, first));
        samples[first..].CopyTo(new Span<float>(data, samples.Length - first));

        // Release: the APO must see the samples before it sees the new write position.
        Volatile.Write(ref writePos, w + samples.Length);
    }

    /// <summary>Call only after the audio thread has stopped calling Write.</summary>
    public void Dispose()
    {
        var s = _session;
        _session = null;
        if (s is null) return;
        s.View.SafeMemoryMappedViewHandle.ReleasePointer();
        s.View.Dispose();
        s.File.Dispose();
    }
}

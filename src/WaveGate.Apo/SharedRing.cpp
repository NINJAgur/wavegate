#include "SharedRing.h"
#include <sddl.h>

namespace wg {

namespace {

std::atomic<uint32_t> g_instanceCounter{ 0 };

inline float SanitizeUnit(float s) noexcept
{
    // NaN fails both comparisons and becomes 0; the producer is untrusted.
    if (s >= -1.0f && s <= 1.0f) return s;
    return (s > 1.0f) ? 1.0f : (s < -1.0f ? -1.0f : 0.0f);
}

inline float Clamp(float s) noexcept
{
    return (s > 1.0f) ? 1.0f : (s < -1.0f ? -1.0f : s);
}

} // namespace

bool RingReader::Open(uint32_t sampleRate, uint32_t channels) noexcept
{
    Close();
    if (sampleRate == 0 || channels == 0) return false;

    PSECURITY_DESCRIPTOR sd = nullptr;
    if (!ConvertStringSecurityDescriptorToSecurityDescriptorW(kRingSddl, SDDL_REVISION_1, &sd, nullptr))
        return false;

    SECURITY_ATTRIBUTES sa{ sizeof(sa), sd, FALSE };
    HANDLE mapping = CreateFileMappingW(INVALID_HANDLE_VALUE, &sa, PAGE_READWRITE, 0, kRingTotalSize, kRingName);
    const DWORD createError = GetLastError();
    LocalFree(sd);
    if (!mapping)
    {
        OutputDebugStringW(L"[WaveGate] CreateFileMapping failed; running as passthrough\n");
        return false;
    }

    void* view = MapViewOfFile(mapping, FILE_MAP_READ | FILE_MAP_WRITE, 0, 0, kRingTotalSize);
    if (!view)
    {
        CloseHandle(mapping);
        return false;
    }

    auto* header = static_cast<RingHeader*>(view);
    if (createError != ERROR_ALREADY_EXISTS)
    {
        // Fresh pages are zeroed by the OS; publish magic last so readers never see a half-written header.
        header->version  = kRingVersion;
        header->capacity = kRingCapacity;
        header->magic.store(kRingMagic, std::memory_order_release);
    }
    else if (header->magic.load(std::memory_order_acquire) != kRingMagic ||
             header->version != kRingVersion || header->capacity != kRingCapacity)
    {
        // Stale mapping from an incompatible build: stay out of the way.
        UnmapViewOfFile(view);
        CloseHandle(mapping);
        return false;
    }

    // Best effort: keep the ring resident so APOProcess never takes a hard page fault.
    VirtualLock(view, kRingTotalSize);

    m_mapping    = mapping;
    m_view       = view;
    m_header     = header;
    m_samples    = reinterpret_cast<float*>(static_cast<uint8_t*>(view) + kRingHeaderSize);
    m_instanceId = (static_cast<uint64_t>(GetCurrentProcessId()) << 32) | (++g_instanceCounter);
    m_sampleRate = sampleRate;
    m_channels   = channels;
    m_prebuffer  = sampleRate / 50;   // 20 ms jitter cushion
    m_targetLead = sampleRate / 25;   // 40 ms
    m_maxLead    = sampleRate / 5;    // 200 ms
    m_primed     = false;
    return true;
}

void RingReader::Close() noexcept
{
    if (m_header)
    {
        uint64_t expected = m_instanceId;
        m_header->ownerId.compare_exchange_strong(expected, 0, std::memory_order_acq_rel);
    }
    if (m_view)
    {
        VirtualUnlock(m_view, kRingTotalSize);
        UnmapViewOfFile(m_view);
    }
    if (m_mapping) CloseHandle(m_mapping);

    m_mapping = nullptr;
    m_view    = nullptr;
    m_header  = nullptr;
    m_samples = nullptr;
}

bool RingReader::HoldOwnership() noexcept
{
    const uint64_t now = GetTickCount64();
    uint64_t owner = m_header->ownerId.load(std::memory_order_acquire);

    if (owner == m_instanceId)
    {
        m_header->ownerBeatMs.store(now, std::memory_order_release);
        return true;
    }

    const uint64_t beat = m_header->ownerBeatMs.load(std::memory_order_acquire);
    if (owner != 0 && now - beat < kOwnerTimeoutMs)
        return false;

    if (!m_header->ownerId.compare_exchange_strong(owner, m_instanceId, std::memory_order_acq_rel))
        return false;

    m_header->ownerBeatMs.store(now, std::memory_order_release);
    m_header->sampleRate.store(m_sampleRate, std::memory_order_release);
    m_header->channels.store(m_channels, std::memory_order_release);
    // Start at "now": never play audio that was queued while nobody was consuming.
    m_header->readPos.store(m_header->writePos.load(std::memory_order_acquire), std::memory_order_release);
    m_primed = false;
    return true;
}

bool RingReader::MixInto(float* frames, uint32_t frameCount, uint32_t channels) noexcept
{
    if (!m_header || !frames || frameCount == 0 || channels == 0) return false;
    if (!HoldOwnership()) return false;

    const uint64_t write = m_header->writePos.load(std::memory_order_acquire);
    uint64_t read = m_header->readPos.load(std::memory_order_relaxed);

    // Producer reset, or producer far ahead (clock drift / consumer was stalled):
    // resync close to the write head instead of playing stale audio.
    if (write < read || write - read > m_maxLead)
        read = (write > m_targetLead) ? write - m_targetLead : write;

    const uint64_t available = write - read;
    if (!m_primed)
    {
        if (available < m_prebuffer)
        {
            m_header->readPos.store(read, std::memory_order_release);
            return false;
        }
        m_primed = true;
    }

    const uint32_t count = (available < frameCount) ? static_cast<uint32_t>(available) : frameCount;
    for (uint32_t i = 0; i < count; ++i)
    {
        const float clip = SanitizeUnit(m_samples[(read + i) & kRingMask]);
        float* frame = frames + static_cast<size_t>(i) * channels;
        for (uint32_t c = 0; c < channels; ++c)
            frame[c] = Clamp(frame[c] + clip);
    }

    if (count < frameCount) m_primed = false; // underrun: rebuffer before resuming
    m_header->readPos.store(read + count, std::memory_order_release);
    return count > 0;
}

} // namespace wg

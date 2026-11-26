#pragma once
// Shared-memory ring buffer between WaveGate.Audio (producer, user session)
// and the APO inside audiodg.exe (consumer, session 0).
//
// Layout is mirrored byte-for-byte in src/WaveGate.Audio/RingLayout.cs.
// Samples are MONO float32 at the rate the owning APO publishes; the APO
// fans them out to every channel. Mono keeps the two sides from ever
// disagreeing about frame alignment.

#include <windows.h>
#include <atomic>
#include <cstddef>
#include <cstdint>

namespace wg {

inline constexpr wchar_t  kRingName[]     = L"Global\\WaveGate.MicRing.v1";
inline constexpr uint32_t kRingMagic      = 0x42524757; // "WGRB"
inline constexpr uint32_t kRingVersion    = 1;
inline constexpr uint32_t kRingCapacity   = 1u << 15;   // samples (~680 ms @ 48 kHz)
inline constexpr uint32_t kRingMask       = kRingCapacity - 1;
inline constexpr uint32_t kRingHeaderSize = 256;
inline constexpr uint32_t kRingTotalSize  = kRingHeaderSize + kRingCapacity * sizeof(float);

// audiodg runs as LOCAL SERVICE; the producer runs as the interactive user.
// SY/LS/BA: full access. IU (interactive users): read + write.
inline constexpr wchar_t kRingSddl[] = L"D:P(A;;GA;;;SY)(A;;GA;;;LS)(A;;GA;;;BA)(A;;GRGW;;;IU)";

static_assert((kRingCapacity & kRingMask) == 0, "capacity must be a power of two");
static_assert(std::atomic<uint64_t>::is_always_lock_free, "need lock-free 64-bit atomics");

struct RingHeader
{
    std::atomic<uint32_t> magic;       // 0   written last by the creator
    uint32_t              version;     // 4
    uint32_t              capacity;    // 8
    std::atomic<uint32_t> sampleRate;  // 12  published by the owning APO
    std::atomic<uint32_t> channels;    // 16  informational
    uint32_t              reserved0[11];

    std::atomic<uint64_t> writePos;    // 64  producer-owned, monotonic sample index
    uint8_t               pad1[56];

    std::atomic<uint64_t> readPos;     // 128 consumer-owned, monotonic sample index
    uint8_t               pad2[56];

    std::atomic<uint64_t> ownerId;     // 192 APO instance currently consuming
    std::atomic<uint64_t> ownerBeatMs; // 200 GetTickCount64() of owner's last APOProcess
    uint8_t               pad3[48];
};

static_assert(sizeof(RingHeader) == kRingHeaderSize, "header layout drift");
static_assert(offsetof(RingHeader, writePos) == 64, "header layout drift");
static_assert(offsetof(RingHeader, readPos) == 128, "header layout drift");
static_assert(offsetof(RingHeader, ownerId) == 192, "header layout drift");

// Consumer side. Open/Close are NOT real-time safe; MixInto is.
//
// Several APO instances can exist for one endpoint (e.g. one per processing
// mode). Only one may consume, so instances compete for a heartbeat lease:
// the holder refreshes ownerBeatMs every period; anyone else takes over once
// the beat is older than kOwnerTimeoutMs.
class RingReader
{
public:
    RingReader() = default;
    ~RingReader() { Close(); }
    RingReader(const RingReader&) = delete;
    RingReader& operator=(const RingReader&) = delete;

    bool Open(uint32_t sampleRate, uint32_t channels) noexcept;
    void Close() noexcept;

    // Adds pending clip samples into `frames` (interleaved, `channels` wide)
    // and clamps to [-1, 1]. Returns true if anything was mixed.
    bool MixInto(float* frames, uint32_t frameCount, uint32_t channels) noexcept;

private:
    bool HoldOwnership() noexcept;

    static constexpr uint64_t kOwnerTimeoutMs = 500;

    HANDLE      m_mapping    = nullptr;
    void*       m_view       = nullptr;
    RingHeader* m_header     = nullptr;
    float*      m_samples    = nullptr;
    uint64_t    m_instanceId = 0;
    uint32_t    m_sampleRate = 0;
    uint32_t    m_channels   = 0;
    uint64_t    m_prebuffer  = 0;  // samples to accumulate before (re)starting
    uint64_t    m_targetLead = 0;  // where to resync to after an overrun
    uint64_t    m_maxLead    = 0;  // beyond this the producer is too far ahead
    bool        m_primed     = false;
};

} // namespace wg

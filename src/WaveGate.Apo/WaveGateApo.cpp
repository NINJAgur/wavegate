#include "WaveGateApo.h"
#include <mmreg.h>
#include <ks.h>
#include <ksmedia.h>
#include <cstring>

std::atomic<LONG> g_objectCount{ 0 };

// AVRT_DATA keeps this in a section that is safe to touch from the RT thread.
const AVRT_DATA CRegAPOProperties<1> CWaveGateApo::sm_RegProperties(
    CLSID_WaveGateApo,
    L"WaveGate Mic Injector",
    L"Copyright (c) WaveGate",
    1,
    0,
    __uuidof(IAudioProcessingObject),
    static_cast<APO_FLAG>(APO_FLAG_INPLACE |
                          APO_FLAG_FRAMESPERSECOND_MUST_MATCH |
                          APO_FLAG_BITSPERSAMPLE_MUST_MATCH |
                          APO_FLAG_SAMPLESPERFRAME_MUST_MATCH));

CWaveGateApo::CWaveGateApo(IUnknown* outer)
    : CBaseAudioProcessingObject(sm_RegProperties)
    , m_outer(outer ? outer : reinterpret_cast<IUnknown*>(static_cast<INonDelegatingUnknown*>(this)))
{
    ++g_objectCount;
}

CWaveGateApo::~CWaveGateApo()
{
    m_ring.Close();
    --g_objectCount;
}

// ---- IUnknown --------------------------------------------------------------

STDMETHODIMP CWaveGateApo::QueryInterface(REFIID riid, void** ppv) { return m_outer->QueryInterface(riid, ppv); }
STDMETHODIMP_(ULONG) CWaveGateApo::AddRef() { return m_outer->AddRef(); }
STDMETHODIMP_(ULONG) CWaveGateApo::Release() { return m_outer->Release(); }

HRESULT STDMETHODCALLTYPE CWaveGateApo::NonDelegatingQueryInterface(REFIID riid, void** ppv)
{
    if (!ppv) return E_POINTER;
    *ppv = nullptr;

    if (riid == __uuidof(IUnknown))
    {
        // Aggregation rule: IUnknown must resolve to the non-delegating vtable.
        *ppv = static_cast<INonDelegatingUnknown*>(this);
        NonDelegatingAddRef();
        return S_OK;
    }

    if (riid == __uuidof(IAudioProcessingObject))                  *ppv = static_cast<IAudioProcessingObject*>(this);
    else if (riid == __uuidof(IAudioProcessingObjectRT))           *ppv = static_cast<IAudioProcessingObjectRT*>(this);
    else if (riid == __uuidof(IAudioProcessingObjectConfiguration)) *ppv = static_cast<IAudioProcessingObjectConfiguration*>(this);
    else if (riid == __uuidof(IAudioSystemEffects))                *ppv = static_cast<IAudioSystemEffects*>(this);
    else if (riid == __uuidof(IAudioSystemEffects2))               *ppv = static_cast<IAudioSystemEffects2*>(this);
    else return E_NOINTERFACE;

    AddRef();
    return S_OK;
}

ULONG STDMETHODCALLTYPE CWaveGateApo::NonDelegatingAddRef()
{
    return static_cast<ULONG>(InterlockedIncrement(&m_refCount));
}

ULONG STDMETHODCALLTYPE CWaveGateApo::NonDelegatingRelease()
{
    const LONG count = InterlockedDecrement(&m_refCount);
    if (count == 0) delete this;
    return static_cast<ULONG>(count);
}

// ---- IAudioProcessingObject ------------------------------------------------

STDMETHODIMP CWaveGateApo::Initialize(UINT32 cbDataSize, BYTE* pbyData)
{
    if ((pbyData == nullptr) != (cbDataSize == 0)) return E_INVALIDARG;

    // We need nothing from the init structs; just accept every known shape.
    if (cbDataSize == sizeof(APOInitSystemEffects3) ||
        cbDataSize == sizeof(APOInitSystemEffects2) ||
        cbDataSize == sizeof(APOInitSystemEffects))
        return S_OK;

    return E_INVALIDARG;
}

// ---- IAudioProcessingObjectConfiguration -----------------------------------

STDMETHODIMP CWaveGateApo::LockForProcess(UINT32 u32NumInputConnections, APO_CONNECTION_DESCRIPTOR** ppInputConnections,
                                          UINT32 u32NumOutputConnections, APO_CONNECTION_DESCRIPTOR** ppOutputConnections)
{
    HRESULT hr = CBaseAudioProcessingObject::LockForProcess(
        u32NumInputConnections, ppInputConnections, u32NumOutputConnections, ppOutputConnections);
    if (FAILED(hr)) return hr;

    m_mixEnabled = false;
    m_channels   = 0;

    UNCOMPRESSEDAUDIOFORMAT fmt{};
    if (u32NumInputConnections < 1 || !ppInputConnections || !ppInputConnections[0] ||
        !ppInputConnections[0]->pFormat ||
        FAILED(ppInputConnections[0]->pFormat->GetUncompressedAudioFormat(&fmt)))
        return S_OK; // passthrough; never fail the graph because of us

    const bool isFloat32 = IsEqualGUID(fmt.guidFormatType, KSDATAFORMAT_SUBTYPE_IEEE_FLOAT) &&
                           fmt.dwBytesPerSampleContainer == sizeof(float);
    const UINT32 rate = static_cast<UINT32>(fmt.fFramesPerSecond + 0.5f);

    if (isFloat32 && fmt.dwSamplesPerFrame > 0 && rate > 0)
    {
        m_channels   = fmt.dwSamplesPerFrame;
        m_mixEnabled = m_ring.Open(rate, m_channels);
    }
    return S_OK;
}

STDMETHODIMP CWaveGateApo::UnlockForProcess()
{
    m_mixEnabled = false;
    m_ring.Close();
    return CBaseAudioProcessingObject::UnlockForProcess();
}

// ---- IAudioProcessingObjectRT ----------------------------------------------
// Runs on the MMCSS audio thread: no locks, no allocation, no exceptions,
// no blocking calls. Every path must leave the output connection valid.

STDMETHODIMP_(void) CWaveGateApo::APOProcess(UINT32 u32NumInputConnections, APO_CONNECTION_PROPERTY** ppInputConnections,
                                             UINT32 u32NumOutputConnections, APO_CONNECTION_PROPERTY** ppOutputConnections)
{
    if (u32NumInputConnections < 1 || u32NumOutputConnections < 1 ||
        !ppInputConnections || !ppOutputConnections ||
        !ppInputConnections[0] || !ppOutputConnections[0])
        return;

    APO_CONNECTION_PROPERTY* in  = ppInputConnections[0];
    APO_CONNECTION_PROPERTY* out = ppOutputConnections[0];
    const UINT32 frames = in->u32ValidFrameCount;

    out->u32ValidFrameCount = frames;
    out->u32BufferFlags     = in->u32BufferFlags;

    if (in->u32BufferFlags == BUFFER_INVALID || frames == 0 || !in->pBuffer || !out->pBuffer)
        return;

    auto* samples = reinterpret_cast<float*>(out->pBuffer);
    const size_t sampleCount = static_cast<size_t>(frames) * (m_channels ? m_channels : 1);

    // Declared in-place, but stay correct if the engine ever hands us two buffers.
    if (in->pBuffer != out->pBuffer && m_channels)
        memcpy(samples, reinterpret_cast<const float*>(in->pBuffer), sampleCount * sizeof(float));

    if (!m_mixEnabled) return;

    // A silent buffer's contents are undefined; zero it before mixing on top.
    if (in->u32BufferFlags == BUFFER_SILENT)
        memset(samples, 0, sampleCount * sizeof(float));

    if (m_ring.MixInto(samples, frames, m_channels))
        out->u32BufferFlags = BUFFER_VALID;
}

// ---- IAudioSystemEffects2 --------------------------------------------------

STDMETHODIMP CWaveGateApo::GetEffectsList(LPGUID* ppEffectsIds, UINT* pcEffects, HANDLE /*Event*/)
{
    if (!ppEffectsIds || !pcEffects) return E_POINTER;
    *ppEffectsIds = nullptr;
    *pcEffects    = 0;
    return S_OK;
}

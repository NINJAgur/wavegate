#pragma once
#include <windows.h>
#include <audioenginebaseapo.h>
#include <BaseAudioProcessingObject.h>
#include "Guids.h"
#include "SharedRing.h"

// The audio engine may create APOs aggregated, so the object exposes a
// non-delegating IUnknown alongside the delegating one (classic COM pattern).
struct INonDelegatingUnknown
{
    virtual HRESULT STDMETHODCALLTYPE NonDelegatingQueryInterface(REFIID riid, void** ppv) = 0;
    virtual ULONG   STDMETHODCALLTYPE NonDelegatingAddRef() = 0;
    virtual ULONG   STDMETHODCALLTYPE NonDelegatingRelease() = 0;
};

class CWaveGateApo final
    : public CBaseAudioProcessingObject
    , public IAudioSystemEffects2
    , public INonDelegatingUnknown
{
public:
    explicit CWaveGateApo(IUnknown* outer);

    // IUnknown (delegates to the outer object when aggregated)
    STDMETHOD(QueryInterface)(REFIID riid, void** ppv) override;
    STDMETHOD_(ULONG, AddRef)() override;
    STDMETHOD_(ULONG, Release)() override;

    // INonDelegatingUnknown
    HRESULT STDMETHODCALLTYPE NonDelegatingQueryInterface(REFIID riid, void** ppv) override;
    ULONG   STDMETHODCALLTYPE NonDelegatingAddRef() override;
    ULONG   STDMETHODCALLTYPE NonDelegatingRelease() override;

    // IAudioProcessingObject
    STDMETHOD(Initialize)(UINT32 cbDataSize, BYTE* pbyData) override;

    // IAudioProcessingObjectConfiguration
    STDMETHOD(LockForProcess)(UINT32 u32NumInputConnections, APO_CONNECTION_DESCRIPTOR** ppInputConnections,
                              UINT32 u32NumOutputConnections, APO_CONNECTION_DESCRIPTOR** ppOutputConnections) override;
    STDMETHOD(UnlockForProcess)() override;

    // IAudioProcessingObjectRT
    STDMETHOD_(void, APOProcess)(UINT32 u32NumInputConnections, APO_CONNECTION_PROPERTY** ppInputConnections,
                                 UINT32 u32NumOutputConnections, APO_CONNECTION_PROPERTY** ppOutputConnections) override;

    // IAudioSystemEffects2
    STDMETHOD(GetEffectsList)(LPGUID* ppEffectsIds, UINT* pcEffects, HANDLE Event) override;

    static const CRegAPOProperties<1> sm_RegProperties;

private:
    ~CWaveGateApo();

    LONG           m_refCount = 1;
    IUnknown*      m_outer;
    wg::RingReader m_ring;
    UINT32         m_channels   = 0;
    bool           m_mixEnabled = false; // only touched outside APOProcess while unlocked
};

extern std::atomic<LONG> g_objectCount;

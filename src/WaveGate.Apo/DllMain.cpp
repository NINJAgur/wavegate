// COM plumbing: class factory, DllGetClassObject, and self-registration.
// Registration writes two keys (HKLM, so regsvr32 must run elevated):
//   SOFTWARE\Classes\CLSID\{clsid}\InprocServer32                 -> COM
//   SOFTWARE\Classes\AudioEngine\AudioProcessingObjects\{clsid}   -> audio engine APO catalog
// Attaching the CLSID to a specific microphone is done by WaveGate.Installer.

#include "WaveGateApo.h"
#include <olectl.h>
#include <new>
#include <string>

namespace {

HMODULE g_module = nullptr;
std::atomic<LONG> g_lockCount{ 0 };

const std::wstring kClsidKey = std::wstring(L"SOFTWARE\\Classes\\CLSID\\") + CLSID_WaveGateApo_String;
const std::wstring kApoKey   = std::wstring(L"SOFTWARE\\Classes\\AudioEngine\\AudioProcessingObjects\\") + CLSID_WaveGateApo_String;

class CWaveGateClassFactory final : public IClassFactory
{
public:
    STDMETHODIMP QueryInterface(REFIID riid, void** ppv) override
    {
        if (!ppv) return E_POINTER;
        if (riid == __uuidof(IUnknown) || riid == __uuidof(IClassFactory))
        {
            *ppv = static_cast<IClassFactory*>(this);
            return S_OK;
        }
        *ppv = nullptr;
        return E_NOINTERFACE;
    }

    // Static lifetime; the DLL lock count keeps the module alive instead.
    STDMETHODIMP_(ULONG) AddRef() override { return 2; }
    STDMETHODIMP_(ULONG) Release() override { return 1; }

    STDMETHODIMP CreateInstance(IUnknown* outer, REFIID riid, void** ppv) override
    {
        if (!ppv) return E_POINTER;
        *ppv = nullptr;
        if (outer && riid != __uuidof(IUnknown)) return CLASS_E_NOAGGREGATION;

        auto* apo = new (std::nothrow) CWaveGateApo(outer);
        if (!apo) return E_OUTOFMEMORY;

        const HRESULT hr = apo->NonDelegatingQueryInterface(riid, ppv);
        apo->NonDelegatingRelease();
        return hr;
    }

    STDMETHODIMP LockServer(BOOL lock) override
    {
        if (lock) ++g_lockCount; else --g_lockCount;
        return S_OK;
    }
};

CWaveGateClassFactory g_factory;

LSTATUS SetString(const std::wstring& key, const wchar_t* name, const wchar_t* value)
{
    const DWORD bytes = static_cast<DWORD>((wcslen(value) + 1) * sizeof(wchar_t));
    return RegSetKeyValueW(HKEY_LOCAL_MACHINE, key.c_str(), name, REG_SZ, value, bytes);
}

LSTATUS SetDword(const std::wstring& key, const wchar_t* name, DWORD value)
{
    return RegSetKeyValueW(HKEY_LOCAL_MACHINE, key.c_str(), name, REG_DWORD, &value, sizeof(value));
}

} // namespace

BOOL APIENTRY DllMain(HMODULE module, DWORD reason, LPVOID)
{
    if (reason == DLL_PROCESS_ATTACH)
    {
        g_module = module;
        DisableThreadLibraryCalls(module);
    }
    return TRUE;
}

STDAPI DllCanUnloadNow()
{
    return (g_objectCount.load() == 0 && g_lockCount.load() == 0) ? S_OK : S_FALSE;
}

STDAPI DllGetClassObject(REFCLSID rclsid, REFIID riid, LPVOID* ppv)
{
    if (!ppv) return E_POINTER;
    *ppv = nullptr;
    if (rclsid != CLSID_WaveGateApo) return CLASS_E_CLASSNOTAVAILABLE;
    return g_factory.QueryInterface(riid, ppv);
}

STDAPI DllRegisterServer()
{
    wchar_t path[MAX_PATH];
    const DWORD len = GetModuleFileNameW(g_module, path, MAX_PATH);
    if (len == 0 || len == MAX_PATH) return SELFREG_E_CLASS;

    const APO_REG_PROPERTIES& p = CWaveGateApo::sm_RegProperties;

    wchar_t iid[64];
    if (!StringFromGUID2(p.iidAPOInterfaceList[0], iid, ARRAYSIZE(iid))) return SELFREG_E_CLASS;

    const std::wstring inproc = kClsidKey + L"\\InprocServer32";
    LSTATUS s = ERROR_SUCCESS;
    if (!s) s = SetString(kClsidKey, nullptr, p.szFriendlyName);
    if (!s) s = SetString(inproc, nullptr, path);
    if (!s) s = SetString(inproc, L"ThreadingModel", L"Both");

    if (!s) s = SetString(kApoKey, L"FriendlyName", p.szFriendlyName);
    if (!s) s = SetString(kApoKey, L"Copyright", p.szCopyrightInfo);
    if (!s) s = SetDword(kApoKey, L"MajorVersion", p.u32MajorVersion);
    if (!s) s = SetDword(kApoKey, L"MinorVersion", p.u32MinorVersion);
    if (!s) s = SetDword(kApoKey, L"Flags", p.Flags);
    if (!s) s = SetDword(kApoKey, L"MinInputConnections", p.u32MinInputConnections);
    if (!s) s = SetDword(kApoKey, L"MaxInputConnections", p.u32MaxInputConnections);
    if (!s) s = SetDword(kApoKey, L"MinOutputConnections", p.u32MinOutputConnections);
    if (!s) s = SetDword(kApoKey, L"MaxOutputConnections", p.u32MaxOutputConnections);
    if (!s) s = SetDword(kApoKey, L"MaxInstances", p.u32MaxInstances);
    if (!s) s = SetDword(kApoKey, L"NumAPOInterfaces", p.u32NumAPOInterfaces);
    if (!s) s = SetString(kApoKey, L"APOInterface0", iid);

    if (s != ERROR_SUCCESS)
    {
        DllUnregisterServer();
        return SELFREG_E_CLASS;
    }
    return S_OK;
}

STDAPI DllUnregisterServer()
{
    // Missing keys are fine: unregister must be idempotent.
    RegDeleteTreeW(HKEY_LOCAL_MACHINE, kApoKey.c_str());
    RegDeleteTreeW(HKEY_LOCAL_MACHINE, kClsidKey.c_str());
    return S_OK;
}

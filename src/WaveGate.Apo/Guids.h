#pragma once
#include <guiddef.h>

// Must match ApoAttachment.ApoClsid in WaveGate.Installer.
inline constexpr GUID CLSID_WaveGateApo =
    { 0x6e56fa97, 0x0cb5, 0x4a10, { 0xb2, 0xa8, 0x5b, 0x83, 0x99, 0x9a, 0x1a, 0x67 } };

inline constexpr wchar_t CLSID_WaveGateApo_String[] = L"{6E56FA97-0CB5-4A10-B2A8-5B83999A1A67}";

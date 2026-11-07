#include <windows.h>
#include <d3d11_4.h>
#include <d3d12.h>
#include <dxgi1_6.h>
#include <wrl/client.h>
#include <atomic>
#include <cstdint>
#include <cstdio>
#include <mutex>

using Microsoft::WRL::ComPtr;

void BridgeLog(const char* fmt, ...);
ID3D12CommandQueue* BridgeQueue();
typedef HRESULT(STDMETHODCALLTYPE* PresentFn)(IDXGISwapChain*, UINT, UINT);
bool FgPresent(IDXGISwapChain* sc, UINT sync, UINT flags, PresentFn original, HRESULT* hr);

namespace
{
	typedef HRESULT(STDMETHODCALLTYPE* Present1Fn)(IDXGISwapChain1*, UINT, UINT, const DXGI_PRESENT_PARAMETERS*);

	PresentFn g_present = nullptr;
	Present1Fn g_present1 = nullptr;
	std::atomic<void*> g_anchor{ nullptr };
	bool g_hooked = false, g_tried = false;
	DWORD g_renderThread = 0;
	std::atomic<uint64_t> g_presents{ 0 };
	bool g_inHook = false;

	const char* EffectName(DXGI_SWAP_EFFECT e)
	{
		switch (e)
		{
		case DXGI_SWAP_EFFECT_DISCARD: return "DISCARD (bitblt)";
		case DXGI_SWAP_EFFECT_SEQUENTIAL: return "SEQUENTIAL (bitblt)";
		case DXGI_SWAP_EFFECT_FLIP_SEQUENTIAL: return "FLIP_SEQUENTIAL";
		case DXGI_SWAP_EFFECT_FLIP_DISCARD: return "FLIP_DISCARD";
		default: return "?";
		}
	}

	HRESULT STDMETHODCALLTYPE PresentHook(IDXGISwapChain* sc, UINT sync, UINT flags)
	{
		HRESULT hr;
		bool ours = false;
		if (!g_inHook && GetCurrentThreadId() == g_renderThread)
		{
			g_inHook = true;
			ours = FgPresent(sc, sync, flags, g_present, &hr);
			g_inHook = false;
			g_presents++;
		}
		if (!ours) hr = g_present(sc, sync, flags);
		return hr;
	}

	HRESULT STDMETHODCALLTYPE Present1Hook(IDXGISwapChain1* sc, UINT sync, UINT flags, const DXGI_PRESENT_PARAMETERS* p)
	{
		HRESULT hr = g_present1(sc, sync, flags, p);
		if (GetCurrentThreadId() == g_renderThread) g_presents++;
		return hr;
	}

	bool Patch(void** slot, void* hook, void** original)
	{
		DWORD was = 0;
		if (!VirtualProtect(slot, sizeof(void*), PAGE_EXECUTE_READWRITE, &was)) { BridgeLog("Present hook: VirtualProtect failed (error %lu).", GetLastError()); return false; }
		*original = *slot;
		*slot = hook;
		VirtualProtect(slot, sizeof(void*), was, &was);
		return true;
	}

	void Hook()
	{
		if (g_tried) return;
		g_tried = true;
		g_renderThread = GetCurrentThreadId();
		ID3D11Resource* any = (ID3D11Resource*)g_anchor.load();
		if (!any) { BridgeLog("Present hook: no anchor texture."); return; }
		ComPtr<ID3D11Device> dev;
		any->GetDevice(&dev);
		ComPtr<IDXGIDevice> xd;
		ComPtr<IDXGIAdapter> ad;
		ComPtr<IDXGIFactory2> factory;
		if (FAILED(dev.As(&xd)) || FAILED(xd->GetAdapter(&ad)) || FAILED(ad->GetParent(IID_PPV_ARGS(&factory)))) { BridgeLog("Present hook: no IDXGIFactory2 from the game's device."); return; }
		ComPtr<IDXGIFactory5> f5;
		BOOL tearing = FALSE;
		if (SUCCEEDED(factory.As(&f5))) f5->CheckFeatureSupport(DXGI_FEATURE_PRESENT_ALLOW_TEARING, &tearing, sizeof tearing);
		WNDCLASSW wc = {};
		wc.lpfnWndProc = DefWindowProcW;
		wc.hInstance = GetModuleHandleW(nullptr);
		wc.lpszClassName = L"FSR4PresentHook";
		RegisterClassW(&wc);
		HWND hwnd = CreateWindowExW(0, wc.lpszClassName, L"FSR4 present hook", WS_OVERLAPPEDWINDOW, 0, 0, 64, 64, nullptr, nullptr, wc.hInstance, nullptr);
		if (!hwnd) { BridgeLog("Present hook: CreateWindowExW failed (error %lu).", GetLastError()); return; }
		DXGI_SWAP_CHAIN_DESC1 d = {};
		d.Width = 64; d.Height = 64;
		d.Format = DXGI_FORMAT_R8G8B8A8_UNORM;
		d.SampleDesc.Count = 1;
		d.BufferUsage = DXGI_USAGE_RENDER_TARGET_OUTPUT;
		d.BufferCount = 2;
		d.SwapEffect = DXGI_SWAP_EFFECT_FLIP_DISCARD;
		ComPtr<IDXGISwapChain1> dummy;
		HRESULT hr = factory->CreateSwapChainForHwnd(dev.Get(), hwnd, &d, nullptr, nullptr, &dummy);
		if (FAILED(hr))
		{
			d.SwapEffect = DXGI_SWAP_EFFECT_DISCARD;
			d.BufferCount = 1;
			hr = factory->CreateSwapChainForHwnd(dev.Get(), hwnd, &d, nullptr, nullptr, &dummy);
		}
		if (FAILED(hr)) { BridgeLog("Present hook: dummy swap chain failed: 0x%08X.", (unsigned)hr); DestroyWindow(hwnd); return; }
		void** vt = *(void***)dummy.Get();
		bool ok = Patch(&vt[8], (void*)&PresentHook, (void**)&g_present) && Patch(&vt[22], (void*)&Present1Hook, (void**)&g_present1);
		g_hooked = ok;
		BridgeLog("Present hook: %s (dummy chain %s, vtable %p, Present %p, Present1 %p; tearing support %d; render thread %lu).",
			ok ? "Present and Present1 hooked through the vtable" : "hooking FAILED", EffectName(d.SwapEffect), (void*)vt, g_present, g_present1, tearing, g_renderThread);
		dummy.Reset();
		DestroyWindow(hwnd);
	}
}

void PresentEvent(int eventId)
{
	if (eventId == 6) Hook();
}

bool PresentHooked() { return g_hooked; }
uint64_t PresentsSeen() { return g_presents.load(); }

extern "C" __declspec(dllexport) void FSR4_HookPresent(void* texture) { g_anchor = texture; }

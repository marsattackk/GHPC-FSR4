#include <windows.h>
#include <d3d11_4.h>
#include <d3d12.h>
#include <dxgi1_6.h>
#include <wrl/client.h>

#include <atomic>
#include <cstdarg>
#include <cstdint>
#include <cstdio>
#include <deque>
#include <map>
#include <mutex>
#include <string>
#include <vector>

#include "ffx_api.h"
#include "ffx_api_loader.h"
#include "dx12/ffx_api_dx12.h"
#include "ffx_upscale.h"

using Microsoft::WRL::ComPtr;

#define EXPORT extern "C" __declspec(dllexport)

struct FrameData
{
	void* color;
	void* depth;
	void* motion;
	void* output;
	uint32_t renderW, renderH, displayW, displayH;
	float jitterX, jitterY, mvScaleX, mvScaleY;
	float frameTimeMs, cameraNear, cameraFar, fovV;
	float sharpness;
	int32_t reset;
	uint32_t createFlags, dispatchFlags;
	float exposure;
	uint32_t direct;
	uint32_t maxRenderW, maxRenderH;
};

struct Stats
{
	uint64_t frames;
	uint64_t providerId;
	int32_t lastResult;
	int32_t failed;
	char provider[64];
	char adapter[128];
	uint64_t presented;
	float cpuMs;
	float gpuMs;
	uint64_t fsrMemory;
	uint64_t logDropped;
	uint32_t bridgeStarts;
	uint32_t contextStarts;
	int32_t removedReason;
	uint32_t failures;
	float gpuPeriodMs, gpuPeriodMin, gpuPeriodMax;
	float gpuBusyMs, gpuBusyMin, gpuBusyMax;
	uint64_t gpuFrames;
	uint64_t gpuDisjoint;
	uint64_t gpuDropped;
	float markMs[48];
	float flushMs;
	uint32_t exposureWanted;
	uint64_t directFrames;
};

namespace
{
	std::mutex g_logLock;
	std::deque<std::string> g_log;
	uint64_t g_logDropped = 0, g_logDroppedReported = 0;

	void Log(const char* fmt, ...)
	{
		char buf[1024];
		va_list a;
		va_start(a, fmt);
		vsnprintf(buf, sizeof buf, fmt, a);
		va_end(a);
		std::lock_guard<std::mutex> l(g_logLock);
		if (g_log.size() < 512) g_log.emplace_back(buf);
		else g_logDropped++;
	}

	std::mutex g_ffxMsgLock;
	std::map<std::string, uint64_t> g_ffxMsgCount;

	void OnFfxMessage(uint32_t type, const wchar_t* message)
	{
		char buf[1024];
		WideCharToMultiByte(CP_UTF8, 0, message, -1, buf, sizeof buf, nullptr, nullptr);
		const char* kind = type == FFX_API_MESSAGE_TYPE_ERROR ? "error" : "warning";
		uint64_t n;
		{
			std::lock_guard<std::mutex> l(g_ffxMsgLock);
			n = ++g_ffxMsgCount[std::string(kind) + buf];
		}
		if (n == 1) Log("FSR %s: %s", kind, buf);
		else
		{
			uint64_t p = 10;
			while (p < n) p *= 10;
			if (p == n) Log("FSR %s, %llu times so far: %s", kind, (unsigned long long)n, buf);
		}
	}

	std::mutex g_viewLock;
	uint64_t g_viewGen = 0;
	struct Retired { ComPtr<ID3D11Texture2D> t11; ComPtr<ID3D12Resource> t12; ComPtr<ID3D11ShaderResourceView> srv; uint64_t gen; uint64_t unusedSince; };
	std::vector<Retired> g_retired;
	uint64_t g_viewTicks = 0;

	struct Shared
	{
		ComPtr<ID3D11Texture2D> t11;
		ComPtr<ID3D12Resource> t12;
		ComPtr<ID3D11ShaderResourceView> srv;
		UINT w = 0, h = 0;
		DXGI_FORMAT fmt = DXGI_FORMAT_UNKNOWN;
		void Release()
		{
			if (srv)
			{
				std::lock_guard<std::mutex> l(g_viewLock);
				g_retired.push_back({ t11, t12, srv, g_viewGen, 0 });
				g_viewGen++;
				srv.Reset();
			}
			t12.Reset(); t11.Reset(); w = h = 0; fmt = DXGI_FORMAT_UNKNOWN;
		}
	};

	const int Slots = 3;
	const DWORD FrameWaitMs = 2000, IdleWaitMs = 5000;

	struct State
	{
		HMODULE upscalerDll = nullptr, loaderDll = nullptr;
		ffxFunctions fx = {};
		bool loaded = false;
		std::atomic<bool> failed{ false };
		bool teardownDue = false;

		ID3D11Device* d11Base = nullptr;
		ComPtr<ID3D11Device5> d11;
		ComPtr<ID3D11DeviceContext4> ctx;
		ComPtr<ID3D12Device> d12;
		ComPtr<ID3D12CommandQueue> queue;
		ComPtr<ID3D12CommandAllocator> alloc[Slots];
		ComPtr<ID3D12GraphicsCommandList> list[Slots];
		uint64_t allocFence[Slots] = {};
		bool slotTimed[Slots] = {};
		ComPtr<ID3D11Fence> in11;
		ComPtr<ID3D12Fence> in12;
		ComPtr<ID3D12Fence> out12;
		ComPtr<ID3D11Fence> out11;
		uint64_t inValue = 0, outValue = 0;
		HANDLE event = nullptr;
		uint64_t submits = 0;
		ComPtr<ID3D12QueryHeap> timestamps;
		ComPtr<ID3D12Resource> readback;
		double tickMs = 0;

		Shared color, depth, motion, output;

		ffxContext fsr = nullptr;
		ComPtr<ID3D12Resource> expTex;
		ComPtr<ID3D12Resource> expUpload;
		float expValue = 0;
		bool exposureRequired = false;
		uint64_t providerGen = 0;
		std::string wantProvider;
		uint64_t overrideId = 0;
		bool providersListed = false;
		uint32_t ctxDisplayW = 0, ctxDisplayH = 0, ctxRenderW = 0, ctxRenderH = 0, ctxFlags = 0;
		int errors = 0;
		Stats stats = {};
	} g;

	std::mutex g_statsLock;
	Stats g_published = {};

	void Publish()
	{
		std::lock_guard<std::mutex> l(g_statsLock);
		g_published = g.stats;
	}

	void Average(float& avg, double sample)
	{
		avg = avg == 0.0f ? (float)sample : avg + ((float)sample - avg) / 32.0f;
	}

	DXGI_FORMAT Typed(DXGI_FORMAT f)
	{
		switch (f)
		{
		case DXGI_FORMAT_R16G16B16A16_TYPELESS: return DXGI_FORMAT_R16G16B16A16_FLOAT;
		case DXGI_FORMAT_R32G32B32A32_TYPELESS: return DXGI_FORMAT_R32G32B32A32_FLOAT;
		case DXGI_FORMAT_R8G8B8A8_TYPELESS:
		case DXGI_FORMAT_R8G8B8A8_UNORM_SRGB: return DXGI_FORMAT_R8G8B8A8_UNORM;
		case DXGI_FORMAT_B8G8R8A8_TYPELESS:
		case DXGI_FORMAT_B8G8R8A8_UNORM_SRGB: return DXGI_FORMAT_B8G8R8A8_UNORM;
		case DXGI_FORMAT_R10G10B10A2_TYPELESS: return DXGI_FORMAT_R10G10B10A2_UNORM;
		case DXGI_FORMAT_R32_TYPELESS: return DXGI_FORMAT_R32_FLOAT;
		case DXGI_FORMAT_R16G16_TYPELESS: return DXGI_FORMAT_R16G16_FLOAT;
		case DXGI_FORMAT_R32G32_TYPELESS: return DXGI_FORMAT_R32G32_FLOAT;
		case DXGI_FORMAT_R16_TYPELESS: return DXGI_FORMAT_R16_FLOAT;
		default: return f;
		}
	}

	bool Typeless(DXGI_FORMAT f)
	{
		switch (f)
		{
		case DXGI_FORMAT_R32G32B32_TYPELESS: case DXGI_FORMAT_R32G8X24_TYPELESS: case DXGI_FORMAT_R24G8_TYPELESS:
		case DXGI_FORMAT_R8G8_TYPELESS: case DXGI_FORMAT_R8_TYPELESS: case DXGI_FORMAT_B8G8R8X8_TYPELESS:
			return true;
		default: return false;
		}
	}

	bool made = false;
	bool deviceLost = false;

	void CheckRemoved()
	{
		HRESULT r12 = g.d12 ? g.d12->GetDeviceRemovedReason() : S_OK;
		HRESULT r11 = g.d11 ? g.d11->GetDeviceRemovedReason() : S_OK;
		HRESULT r = FAILED(r12) ? r12 : r11;
		if (SUCCEEDED(r)) return;
		deviceLost = true;
		if (g.stats.removedReason == (int32_t)r) return;
		g.stats.removedReason = (int32_t)r;
		Log("Device removed (%s): 0x%08X.", FAILED(r12) ? "our D3D12 device" : "the game's D3D11 device", (unsigned)r);
	}

	bool Check(HRESULT hr, const char* what)
	{
		if (SUCCEEDED(hr)) return true;
		Log("%s failed: 0x%08X", what, (unsigned)hr);
		CheckRemoved();
		return false;
	}

	bool WaitOut(uint64_t value, DWORD ms, const char* what)
	{
		if (!value || g.out12->GetCompletedValue() >= value) return true;
		if (!Check(g.out12->SetEventOnCompletion(value, g.event), "SetEventOnCompletion")) return false;
		DWORD w = WaitForSingleObject(g.event, ms);
		if (w == WAIT_OBJECT_0) return true;
		Log("%s: the GPU did not finish within %lu ms (wait result %lu, fence at %llu of %llu).", what, ms, w,
			(unsigned long long)g.out12->GetCompletedValue(), (unsigned long long)value);
		CheckRemoved();
		deviceLost = true;
		return false;
	}

	bool WaitIdle(const char* what, bool evenIfLost = false)
	{
		if (!g.queue || !g.out12) return true;
		if (deviceLost && !evenIfLost) return false;
		uint64_t v = ++g.outValue;
		if (!Check(g.queue->Signal(g.out12.Get(), v), "queue Signal")) return false;
		return WaitOut(v, IdleWaitMs, what);
	}

	void ReleaseShared()
	{
		g.color.Release();
		g.depth.Release();
		g.motion.Release();
		g.output.Release();
	}

	void DestroyContext()
	{
		if (g.fsr && g.fx.DestroyContext) g.fx.DestroyContext(&g.fsr, nullptr);
		g.fsr = nullptr;
		g.ctxDisplayW = g.ctxDisplayH = g.ctxRenderW = g.ctxRenderH = g.ctxFlags = 0;
		g.stats.providerId = 0;
		g.stats.provider[0] = 0;
		g.stats.fsrMemory = 0;
	}

	void Teardown(const char* why)
	{
		bool idle = (g.d12 && FAILED(g.d12->GetDeviceRemovedReason())) || WaitIdle("teardown", true);
		if (idle)
		{
			DestroyContext();
			ReleaseShared();
			for (int i = 0; i < Slots; i++) { g.list[i].Reset(); g.alloc[i].Reset(); }
			g.timestamps.Reset();
			g.readback.Reset();
			g.expTex.Reset();
			g.expUpload.Reset();
			g.in12.Reset();
			g.out12.Reset();
			g.queue.Reset();
			g.d12.Reset();
		}
		else
		{
			g.fsr = nullptr;
			g.ctxDisplayW = g.ctxDisplayH = g.ctxRenderW = g.ctxRenderH = g.ctxFlags = 0;
			g.stats.providerId = 0;
			g.stats.provider[0] = 0;
			g.stats.fsrMemory = 0;
			for (Shared* s : { &g.color, &g.depth, &g.motion, &g.output }) { s->t12.Detach(); s->Release(); }
			for (int i = 0; i < Slots; i++) { g.list[i].Detach(); g.alloc[i].Detach(); }
			g.timestamps.Detach();
			g.readback.Detach();
			g.expTex.Detach();
			g.expUpload.Detach();
			g.in12.Detach();
			g.out12.Detach();
			g.queue.Detach();
			g.d12.Detach();
		}
		for (int i = 0; i < Slots; i++) { g.allocFence[i] = 0; g.slotTimed[i] = false; }
		g.in11.Reset();
		g.out11.Reset();
		g.ctx.Reset();
		g.d11.Reset();
		g.d11Base = nullptr;
		g.inValue = g.outValue = 0;
		g.expValue = 0;
		g.providersListed = false;
		g.overrideId = 0;
		g.exposureRequired = false;
		deviceLost = false;
		Log("Bridge shut down (%s)%s.", why, idle ? "" : "; its GPU objects abandoned, the queue was not idle");
	}

	bool InitDevices(ID3D11Resource* any)
	{
		ComPtr<ID3D11Device> dev;
		any->GetDevice(&dev);
		g.d11Base = dev.Get();
		if (!Check(dev.As(&g.d11), "ID3D11Device5 (Windows 10 1703 or later)")) return false;
		ComPtr<ID3D11DeviceContext> ic;
		dev->GetImmediateContext(&ic);
		if (!Check(ic.As(&g.ctx), "ID3D11DeviceContext4")) return false;

		ComPtr<IDXGIDevice> xd;
		ComPtr<IDXGIAdapter> ad;
		DXGI_ADAPTER_DESC ds = {};
		if (!Check(dev.As(&xd), "IDXGIDevice") || !Check(xd->GetAdapter(&ad), "GetAdapter") || !Check(ad->GetDesc(&ds), "GetDesc")) return false;
		WideCharToMultiByte(CP_UTF8, 0, ds.Description, -1, g.stats.adapter, sizeof g.stats.adapter, nullptr, nullptr);

		ComPtr<IDXGIFactory4> factory;
		ComPtr<IDXGIAdapter1> ad12;
		if (!Check(CreateDXGIFactory2(0, IID_PPV_ARGS(&factory)), "CreateDXGIFactory2")) return false;
		if (!Check(factory->EnumAdapterByLuid(ds.AdapterLuid, IID_PPV_ARGS(&ad12)), "EnumAdapterByLuid")) return false;
		if (!Check(D3D12CreateDevice(ad12.Get(), D3D_FEATURE_LEVEL_12_0, IID_PPV_ARGS(&g.d12)), "D3D12CreateDevice")) return false;

		D3D12_COMMAND_QUEUE_DESC qd = {};
		qd.Type = D3D12_COMMAND_LIST_TYPE_DIRECT;
		if (!Check(g.d12->CreateCommandQueue(&qd, IID_PPV_ARGS(&g.queue)), "CreateCommandQueue")) return false;
		for (int i = 0; i < Slots; i++)
		{
			if (!Check(g.d12->CreateCommandAllocator(D3D12_COMMAND_LIST_TYPE_DIRECT, IID_PPV_ARGS(&g.alloc[i])), "CreateCommandAllocator")) return false;
			if (!Check(g.d12->CreateCommandList(0, D3D12_COMMAND_LIST_TYPE_DIRECT, g.alloc[i].Get(), nullptr, IID_PPV_ARGS(&g.list[i])), "CreateCommandList")) return false;
			if (!Check(g.list[i]->Close(), "command list Close")) return false;
		}

		D3D12_QUERY_HEAP_DESC qh = {};
		qh.Type = D3D12_QUERY_HEAP_TYPE_TIMESTAMP;
		qh.Count = 2 * Slots;
		if (!Check(g.d12->CreateQueryHeap(&qh, IID_PPV_ARGS(&g.timestamps)), "CreateQueryHeap")) return false;
		D3D12_HEAP_PROPERTIES hp = {};
		hp.Type = D3D12_HEAP_TYPE_READBACK;
		D3D12_RESOURCE_DESC bd = {};
		bd.Dimension = D3D12_RESOURCE_DIMENSION_BUFFER;
		bd.Width = 2 * Slots * sizeof(uint64_t);
		bd.Height = bd.DepthOrArraySize = bd.MipLevels = 1;
		bd.SampleDesc.Count = 1;
		bd.Layout = D3D12_TEXTURE_LAYOUT_ROW_MAJOR;
		if (!Check(g.d12->CreateCommittedResource(&hp, D3D12_HEAP_FLAG_NONE, &bd, D3D12_RESOURCE_STATE_COPY_DEST, nullptr, IID_PPV_ARGS(&g.readback)), "timestamp readback buffer")) return false;
		UINT64 freq = 0;
		if (!Check(g.queue->GetTimestampFrequency(&freq), "GetTimestampFrequency") || !freq) return false;
		g.tickMs = 1000.0 / (double)freq;

		HANDLE h = nullptr;
		if (!Check(g.d11->CreateFence(0, D3D11_FENCE_FLAG_SHARED, IID_PPV_ARGS(&g.in11)), "ID3D11Device5::CreateFence")) return false;
		if (!Check(g.in11->CreateSharedHandle(nullptr, GENERIC_ALL, nullptr, &h), "in fence CreateSharedHandle")) return false;
		HRESULT hr = g.d12->OpenSharedHandle(h, IID_PPV_ARGS(&g.in12));
		CloseHandle(h);
		if (!Check(hr, "in fence OpenSharedHandle")) return false;
		if (!Check(g.d12->CreateFence(0, D3D12_FENCE_FLAG_SHARED, IID_PPV_ARGS(&g.out12)), "ID3D12Device::CreateFence")) return false;
		h = nullptr;
		if (!Check(g.d12->CreateSharedHandle(g.out12.Get(), nullptr, GENERIC_ALL, nullptr, &h), "out fence CreateSharedHandle")) return false;
		hr = g.d11->OpenSharedFence(h, IID_PPV_ARGS(&g.out11));
		CloseHandle(h);
		if (!Check(hr, "out fence OpenSharedFence")) return false;
		if (!g.event && !(g.event = CreateEventW(nullptr, FALSE, FALSE, nullptr))) { Log("CreateEventW failed: error %lu.", GetLastError()); return false; }

		g.stats.bridgeStarts++;
		Log("Bridge ready on %s: D3D12 device and queue, shared fences (start %u).", g.stats.adapter, g.stats.bridgeStarts);
		return true;
	}

	bool EnsureShared(Shared& s, ID3D11Texture2D* src, bool uav, const char* name, bool view)
	{
		D3D11_TEXTURE2D_DESC sd;
		src->GetDesc(&sd);
		DXGI_FORMAT fmt = Typed(sd.Format);
		if (s.t11 && s.w == sd.Width && s.h == sd.Height && s.fmt == fmt) return true;
		if (sd.SampleDesc.Count != 1) { Log("%s: multisampled textures are not supported.", name); return false; }
		if (sd.MipLevels != 1 || sd.ArraySize != 1) { Log("%s: %u mips, %u slices: CopyResource needs exactly one of each.", name, sd.MipLevels, sd.ArraySize); return false; }
		if (Typeless(fmt)) { Log("%s: no typed format for game texture format %d.", name, (int)sd.Format); return false; }

		if (!WaitIdle("new shared texture")) return false;
		s.Release();
		D3D11_TEXTURE2D_DESC d = {};
		d.Width = sd.Width;
		d.Height = sd.Height;
		d.MipLevels = 1;
		d.ArraySize = 1;
		d.Format = fmt;
		d.SampleDesc.Count = 1;
		d.Usage = D3D11_USAGE_DEFAULT;
		d.BindFlags = D3D11_BIND_SHADER_RESOURCE | (uav ? D3D11_BIND_UNORDERED_ACCESS : 0);
		d.MiscFlags = D3D11_RESOURCE_MISC_SHARED | D3D11_RESOURCE_MISC_SHARED_NTHANDLE;
		if (!Check(g.d11->CreateTexture2D(&d, nullptr, &s.t11), name)) return false;

		ComPtr<IDXGIResource1> r;
		HANDLE h = nullptr;
		if (!Check(s.t11.As(&r), "IDXGIResource1") || !Check(r->CreateSharedHandle(nullptr, DXGI_SHARED_RESOURCE_READ | DXGI_SHARED_RESOURCE_WRITE, nullptr, &h), "texture CreateSharedHandle"))
			{ s.Release(); return false; }
		HRESULT hr = g.d12->OpenSharedHandle(h, IID_PPV_ARGS(&s.t12));
		CloseHandle(h);
		if (!Check(hr, "texture OpenSharedHandle")) { s.Release(); return false; }
		s.w = sd.Width;
		s.h = sd.Height;
		s.fmt = fmt;
		made = true;
		if (view)
		{
			ComPtr<ID3D11ShaderResourceView> srv;
			if (!Check(g.d11->CreateShaderResourceView(s.t11.Get(), nullptr, &srv), "shared texture view")) { s.Release(); return false; }
			std::lock_guard<std::mutex> l(g_viewLock);
			s.srv = srv;
			g_viewGen++;
		}
		Log("Shared %s %ux%u, format %d (game texture format %d)%s.", name, s.w, s.h, (int)fmt, (int)sd.Format, view ? ", viewed by Unity" : "");
		return true;
	}

	bool PickProvider()
	{
		if (g.providersListed) return g.wantProvider.empty() || g.overrideId != 0;
		g.providersListed = true;
		uint64_t count = 0;
		ffxQueryDescGetVersions vq = {};
		vq.header.type = FFX_API_QUERY_DESC_TYPE_GET_VERSIONS;
		vq.createDescType = FFX_API_CREATE_CONTEXT_DESC_TYPE_UPSCALE;
		vq.device = g.d12.Get();
		vq.outputCount = &count;
		ffxReturnCode_t rc = g.fx.Query(nullptr, &vq.header);
		uint64_t ids[16] = {};
		const char* names[16] = {};
		if (rc == FFX_API_RETURN_OK && count)
		{
			if (count > 16) count = 16;
			vq.versionIds = ids;
			vq.versionNames = names;
			rc = g.fx.Query(nullptr, &vq.header);
		}
		if (rc != FFX_API_RETURN_OK) { Log("FSR providers: the version query failed (%u).", rc); return g.wantProvider.empty(); }
		std::string list;
		for (uint64_t i = 0; i < count; i++)
		{
			char item[96];
			snprintf(item, sizeof item, "%s'%s' (id 0x%llX)", i ? ", " : "", names[i] ? names[i] : "?", (unsigned long long)ids[i]);
			list += item;
			if (!g.overrideId && !g.wantProvider.empty() && names[i] && strncmp(names[i], g.wantProvider.c_str(), g.wantProvider.size()) == 0)
				g.overrideId = ids[i];
		}
		Log("FSR providers on this GPU (%llu): %s.", (unsigned long long)count, list.c_str());
		if (g.wantProvider.empty()) return true;
		if (!g.overrideId) { Log("FSR provider '%s' asked for: not offered here.", g.wantProvider.c_str()); return false; }
		Log("FSR provider forced: '%s' (id 0x%llX).", g.wantProvider.c_str(), (unsigned long long)g.overrideId);
		return true;
	}

	bool EnsureContext(const FrameData& fd)
	{
		bool dynamic = (fd.createFlags & FFX_UPSCALE_ENABLE_DYNAMIC_RESOLUTION) != 0;
		uint32_t keyW = dynamic ? (fd.maxRenderW > fd.renderW ? fd.maxRenderW : fd.renderW) : fd.renderW;
		uint32_t keyH = dynamic ? (fd.maxRenderH > fd.renderH ? fd.maxRenderH : fd.renderH) : fd.renderH;
		if (g.fsr && g.ctxDisplayW == fd.displayW && g.ctxDisplayH == fd.displayH && g.ctxRenderW == keyW && g.ctxRenderH == keyH
			&& g.ctxFlags == fd.createFlags) return true;
		LARGE_INTEGER t0, t1, qf;
		QueryPerformanceCounter(&t0);
		if (!WaitIdle("new FSR context")) return false;
		DestroyContext();
		if (!PickProvider()) return false;

		ffxCreateBackendDX12Desc backend = {};
		backend.header.type = FFX_API_CREATE_CONTEXT_DESC_TYPE_BACKEND_DX12;
		backend.device = g.d12.Get();
		ffxCreateContextDescUpscaleVersion version = {};
		version.header.type = FFX_API_CREATE_CONTEXT_DESC_TYPE_UPSCALE_VERSION;
		version.version = FFX_UPSCALER_VERSION;
		ffxCreateContextDescUpscale desc = {};
		desc.header.type = FFX_API_CREATE_CONTEXT_DESC_TYPE_UPSCALE;
		desc.flags = fd.createFlags;
		desc.maxRenderSize = { keyW, keyH };
		desc.maxUpscaleSize = { fd.displayW, fd.displayH };
		desc.fpMessage = OnFfxMessage;
		desc.header.pNext = &backend.header;
		backend.header.pNext = &version.header;
		ffxOverrideVersion force = {};
		force.header.type = FFX_API_DESC_TYPE_OVERRIDE_VERSION;
		force.versionId = g.overrideId;
		if (g.overrideId) version.header.pNext = &force.header;

		ffxReturnCode_t rc = g.fx.CreateContext(&g.fsr, &desc.header, nullptr);
		if (rc != FFX_API_RETURN_OK) { Log("ffxCreateContext failed: %u.", rc); g.fsr = nullptr; CheckRemoved(); return false; }
		QueryPerformanceCounter(&t1);
		QueryPerformanceFrequency(&qf);
		double madeMs = (t1.QuadPart - t0.QuadPart) * 1000.0 / qf.QuadPart;
		g.ctxDisplayW = fd.displayW;
		g.ctxDisplayH = fd.displayH;
		g.ctxRenderW = keyW;
		g.ctxRenderH = keyH;
		g.ctxFlags = fd.createFlags;
		g.stats.contextStarts++;
		made = true;

		ffxQueryGetProviderVersion pv = {};
		pv.header.type = FFX_API_QUERY_DESC_TYPE_GET_PROVIDER_VERSION;
		if (g.fx.Query(&g.fsr, &pv.header) == FFX_API_RETURN_OK && pv.versionName)
		{
			g.stats.providerId = pv.versionId;
			snprintf(g.stats.provider, sizeof g.stats.provider, "%s", pv.versionName);
		}
		FfxApiEffectMemoryUsage mem = {};
		ffxQueryDescUpscaleGetGPUMemoryUsage mq = {};
		mq.header.type = FFX_API_QUERY_DESC_TYPE_UPSCALE_GPU_MEMORY_USAGE;
		mq.gpuMemoryUsageUpscaler = &mem;
		ffxReturnCode_t mrc = g.fx.Query(&g.fsr, &mq.header);
		g.stats.fsrMemory = mrc == FFX_API_RETURN_OK ? mem.totalUsageInBytes : 0;
		Log("FSR context %ux%u -> %ux%u, flags 0x%X: provider '%s' (id 0x%llX); GPU memory %.1f MB (%.1f MB aliasable)%s; context %u, made in %.1f ms (queue idle, old one destroyed, new one created).",
			fd.renderW, fd.renderH, fd.displayW, fd.displayH, fd.createFlags, g.stats.provider, (unsigned long long)g.stats.providerId,
			mem.totalUsageInBytes / 1048576.0, mem.aliasableUsageInBytes / 1048576.0, mrc == FFX_API_RETURN_OK ? "" : " (query failed)",
			g.stats.contextStarts, madeMs);
		ffxQueryDescUpscaleGetResourceRequirements rq = {};
		rq.header.type = FFX_API_QUERY_DESC_TYPE_UPSCALE_GET_RESOURCE_REQUIREMENTS;
		ffxReturnCode_t rrc = g.fx.Query(&g.fsr, &rq.header);
		if (rrc == FFX_API_RETURN_OK)
			Log("FSR inputs: required 0x%llX, optional 0x%llX (reactive mask %s, transparency-and-composition mask %s).",
				(unsigned long long)rq.required_resources, (unsigned long long)rq.optional_resources,
				(rq.required_resources & FFX_API_QUERY_RESOURCE_INPUT_REACTIVEMASK) ? "required" : (rq.optional_resources & FFX_API_QUERY_RESOURCE_INPUT_REACTIVEMASK) ? "optional" : "not used",
				(rq.required_resources & FFX_API_QUERY_RESOURCE_INPUT_TRANSPARENCYCOMPOSITION) ? "required" : (rq.optional_resources & FFX_API_QUERY_RESOURCE_INPUT_TRANSPARENCYCOMPOSITION) ? "optional" : "not used");
		else Log("FSR inputs: the resource-requirements query failed (%u).", rrc);
		if (rrc == FFX_API_RETURN_OK)
		{
			bool req = (rq.required_resources & FFX_API_QUERY_RESOURCE_INPUT_EXPOSURE) != 0;
			if (req != g.exposureRequired) Log("FSR exposure input %s by this provider: %s.", req ? "required" : "not required", req ? "the game's exposure is handed to it" : "its own auto exposure");
			g.exposureRequired = req;
			g.stats.exposureWanted = req ? 1 : 0;
		}
		return true;
	}

	void Barrier(ID3D12Resource* r, D3D12_RESOURCE_STATES from, D3D12_RESOURCE_STATES to, D3D12_RESOURCE_BARRIER& b)
	{
		b = {};
		b.Type = D3D12_RESOURCE_BARRIER_TYPE_TRANSITION;
		b.Transition.pResource = r;
		b.Transition.StateBefore = from;
		b.Transition.StateAfter = to;
		b.Transition.Subresource = D3D12_RESOURCE_BARRIER_ALL_SUBRESOURCES;
	}

	void Fail(const char* why)
	{
		Log("FSR stopped: %s", why);
		CheckRemoved();
		if (deviceLost || !WaitIdle("stopping after an error")) Teardown("after an error");
		else { DestroyContext(); ReleaseShared(); }
		g.errors = 0;
		g.stats.failures++;
		g.failed = true;
	}

	void ReadTimestamps(int slot)
	{
		if (!g.slotTimed[slot]) return;
		g.slotTimed[slot] = false;
		D3D12_RANGE r = { slot * 2 * sizeof(uint64_t), (slot * 2 + 2) * sizeof(uint64_t) };
		void* p = nullptr;
		if (FAILED(g.readback->Map(0, &r, &p))) return;
		const uint64_t* t = (const uint64_t*)((const char*)p + r.Begin);
		if (t[1] > t[0]) Average(g.stats.gpuMs, (t[1] - t[0]) * g.tickMs);
		D3D12_RANGE none = { 0, 0 };
		g.readback->Unmap(0, &none);
	}

	bool EnsureExposure(ID3D12GraphicsCommandList* l, int slot, float value)
	{
		if (!g.expTex)
		{
			D3D12_HEAP_PROPERTIES hp = {};
			hp.Type = D3D12_HEAP_TYPE_DEFAULT;
			D3D12_RESOURCE_DESC td = {};
			td.Dimension = D3D12_RESOURCE_DIMENSION_TEXTURE2D;
			td.Width = td.Height = 1;
			td.DepthOrArraySize = td.MipLevels = 1;
			td.Format = DXGI_FORMAT_R32_FLOAT;
			td.SampleDesc.Count = 1;
			if (!Check(g.d12->CreateCommittedResource(&hp, D3D12_HEAP_FLAG_NONE, &td, D3D12_RESOURCE_STATE_COPY_DEST, nullptr, IID_PPV_ARGS(&g.expTex)), "exposure texture")) return false;
			hp.Type = D3D12_HEAP_TYPE_UPLOAD;
			D3D12_RESOURCE_DESC bd = {};
			bd.Dimension = D3D12_RESOURCE_DIMENSION_BUFFER;
			bd.Width = Slots * D3D12_TEXTURE_DATA_PLACEMENT_ALIGNMENT;
			bd.Height = bd.DepthOrArraySize = bd.MipLevels = 1;
			bd.SampleDesc.Count = 1;
			bd.Layout = D3D12_TEXTURE_LAYOUT_ROW_MAJOR;
			if (!Check(g.d12->CreateCommittedResource(&hp, D3D12_HEAP_FLAG_NONE, &bd, D3D12_RESOURCE_STATE_GENERIC_READ, nullptr, IID_PPV_ARGS(&g.expUpload)), "exposure upload buffer")) return false;
			g.expValue = 0;
		}
		if (g.expValue == value) return true;
		bool first = g.expValue == 0;
		const UINT64 at = (UINT64)slot * D3D12_TEXTURE_DATA_PLACEMENT_ALIGNMENT;
		void* p = nullptr;
		D3D12_RANGE none = { 0, 0 };
		if (!Check(g.expUpload->Map(0, &none, &p), "exposure upload Map")) return false;
		*(float*)((char*)p + at) = value;
		D3D12_RANGE written = { (SIZE_T)at, (SIZE_T)at + sizeof(float) };
		g.expUpload->Unmap(0, &written);
		D3D12_RESOURCE_BARRIER b;
		if (!first) { Barrier(g.expTex.Get(), D3D12_RESOURCE_STATE_NON_PIXEL_SHADER_RESOURCE, D3D12_RESOURCE_STATE_COPY_DEST, b); l->ResourceBarrier(1, &b); }
		D3D12_TEXTURE_COPY_LOCATION dst = {}, src = {};
		dst.pResource = g.expTex.Get();
		dst.Type = D3D12_TEXTURE_COPY_TYPE_SUBRESOURCE_INDEX;
		src.pResource = g.expUpload.Get();
		src.Type = D3D12_TEXTURE_COPY_TYPE_PLACED_FOOTPRINT;
		src.PlacedFootprint.Offset = at;
		src.PlacedFootprint.Footprint = { DXGI_FORMAT_R32_FLOAT, 1, 1, 1, D3D12_TEXTURE_DATA_PITCH_ALIGNMENT };
		l->CopyTextureRegion(&dst, 0, 0, 0, &src, nullptr);
		Barrier(g.expTex.Get(), D3D12_RESOURCE_STATE_COPY_DEST, D3D12_RESOURCE_STATE_NON_PIXEL_SHADER_RESOURCE, b);
		l->ResourceBarrier(1, &b);
		g.expValue = value;
		return true;
	}

	struct Handed { float nearM, farM, sharpness, preExposure, exposure; uint32_t createFlags, dispatchFlags; };
	Handed g_handed = { -1 };

	void TimingMark(int code);
	const uint32_t DirectColor = 1, DirectOutput = 2, DirectMotion = 4;

	void Upscale(const FrameData& handed)
	{
		if (!g.loaded || g.failed || !handed.color || !handed.depth || !handed.motion || !handed.output) return;
		FrameData fd = handed;
		float exposure = !g.exposureRequired || !(handed.exposure > 0) ? 0.0f : handed.exposure;
		if (exposure > 0) fd.createFlags &= ~(uint32_t)FFX_UPSCALE_ENABLE_AUTO_EXPOSURE;
		float preExposure = 1.0f;
		float expStep = exposure > 0 ? floorf(log2f(exposure) * 4.0f + 0.5f) : -1000.0f;
		(void)expStep;

		ComPtr<ID3D11Device> dev;
		((ID3D11Resource*)fd.color)->GetDevice(&dev);
		if (g.d12 && dev.Get() != g.d11Base) Teardown("the game's D3D11 device changed");
		if (!g.d12 && !InitDevices((ID3D11Resource*)fd.color)) { Teardown("could not start"); Fail("the D3D12 side could not be made (above)."); return; }
		if (!EnsureShared(g.color, (ID3D11Texture2D*)fd.color, false, "color", true)
			|| !EnsureShared(g.depth, (ID3D11Texture2D*)fd.depth, false, "depth", false)
			|| !EnsureShared(g.motion, (ID3D11Texture2D*)fd.motion, false, "motion vectors", true)
			|| !EnsureShared(g.output, (ID3D11Texture2D*)fd.output, true, "output", true)) { Fail("shared textures (above)."); return; }
		if (!EnsureContext(fd)) { Fail("no FSR context (above)."); return; }

		int slot = (int)(g.submits % Slots);
		if (!WaitOut(g.allocFence[slot], FrameWaitMs, "command allocator reuse")) { Fail("the GPU stopped making progress (above)."); return; }
		ReadTimestamps(slot);
		ID3D12GraphicsCommandList* l = g.list[slot].Get();
		if (!Check(g.alloc[slot]->Reset(), "command allocator Reset") || !Check(l->Reset(g.alloc[slot].Get(), nullptr), "command list Reset"))
			{ Fail("command list (above)."); return; }

		if (!(fd.direct & DirectColor)) g.ctx->CopyResource(g.color.t11.Get(), (ID3D11Resource*)fd.color);
		g.ctx->CopyResource(g.depth.t11.Get(), (ID3D11Resource*)fd.depth);
		if (!(fd.direct & DirectMotion)) g.ctx->CopyResource(g.motion.t11.Get(), (ID3D11Resource*)fd.motion);
		if (fd.direct) g.stats.directFrames++;
		TimingMark(3);
		uint64_t ready = ++g.inValue;
		if (!Check(g.ctx->Signal(g.in11.Get(), ready), "D3D11 Signal")) { l->Close(); Fail("fence (above)."); return; }
		LARGE_INTEGER f0, f1, ff;
		QueryPerformanceCounter(&f0);
		g.ctx->Flush();
		QueryPerformanceCounter(&f1);
		QueryPerformanceFrequency(&ff);
		Average(g.stats.flushMs, (f1.QuadPart - f0.QuadPart) * 1000.0 / ff.QuadPart);

		D3D12_RESOURCE_BARRIER in[4], out[4];
		const auto read = D3D12_RESOURCE_STATE_NON_PIXEL_SHADER_RESOURCE;
		Barrier(g.color.t12.Get(), D3D12_RESOURCE_STATE_COMMON, read, in[0]);
		Barrier(g.depth.t12.Get(), D3D12_RESOURCE_STATE_COMMON, read, in[1]);
		Barrier(g.motion.t12.Get(), D3D12_RESOURCE_STATE_COMMON, read, in[2]);
		Barrier(g.output.t12.Get(), D3D12_RESOURCE_STATE_COMMON, D3D12_RESOURCE_STATE_UNORDERED_ACCESS, in[3]);
		l->ResourceBarrier(4, in);

		ffxDispatchDescUpscale d = {};
		d.header.type = FFX_API_DISPATCH_DESC_TYPE_UPSCALE;
		d.commandList = l;
		d.color = ffxApiGetResourceDX12(g.color.t12.Get(), FFX_API_RESOURCE_STATE_COMPUTE_READ);
		d.depth = ffxApiGetResourceDX12(g.depth.t12.Get(), FFX_API_RESOURCE_STATE_COMPUTE_READ);
		d.motionVectors = ffxApiGetResourceDX12(g.motion.t12.Get(), FFX_API_RESOURCE_STATE_COMPUTE_READ);
		d.output = ffxApiGetResourceDX12(g.output.t12.Get(), FFX_API_RESOURCE_STATE_UNORDERED_ACCESS);
		d.jitterOffset = { fd.jitterX, fd.jitterY };
		d.motionVectorScale = { fd.mvScaleX, fd.mvScaleY };
		d.renderSize = { fd.renderW, fd.renderH };
		d.upscaleSize = { fd.displayW, fd.displayH };
		d.enableSharpening = fd.sharpness > 0.0f;
		d.sharpness = fd.sharpness;
		d.frameTimeDelta = fd.frameTimeMs;
		d.preExposure = preExposure;
		if (exposure > 0)
		{
			if (!EnsureExposure(l, slot, exposure)) { l->Close(); Fail("exposure input (above)."); return; }
			d.exposure = ffxApiGetResourceDX12(g.expTex.Get(), FFX_API_RESOURCE_STATE_COMPUTE_READ);
		}
		d.reset = fd.reset != 0;
		d.cameraNear = fd.cameraNear;
		d.cameraFar = fd.cameraFar;
		d.cameraFovAngleVertical = fd.fovV;
		d.viewSpaceToMetersFactor = 1.0f;
		d.flags = fd.dispatchFlags;
		l->EndQuery(g.timestamps.Get(), D3D12_QUERY_TYPE_TIMESTAMP, slot * 2);
		ffxReturnCode_t rc = g.fx.Dispatch(&g.fsr, &d.header);
		l->EndQuery(g.timestamps.Get(), D3D12_QUERY_TYPE_TIMESTAMP, slot * 2 + 1);
		g.stats.lastResult = (int32_t)rc;

		for (int i = 0; i < 4; i++)
		{
			out[i] = in[i];
			std::swap(out[i].Transition.StateBefore, out[i].Transition.StateAfter);
		}
		l->ResourceBarrier(4, out);
		l->ResolveQueryData(g.timestamps.Get(), D3D12_QUERY_TYPE_TIMESTAMP, slot * 2, 2, g.readback.Get(), slot * 2 * sizeof(uint64_t));
		if (!Check(l->Close(), "command list Close")) { Fail("command list (above)."); return; }

		if (!Check(g.queue->Wait(g.in12.Get(), ready), "queue Wait")) { Fail("fence (above)."); return; }
		ID3D12CommandList* lists[] = { l };
		g.queue->ExecuteCommandLists(1, lists);
		uint64_t done = ++g.outValue;
		if (!Check(g.queue->Signal(g.out12.Get(), done), "queue Signal")) { Fail("fence (above)."); return; }
		g.allocFence[slot] = done;
		g.slotTimed[slot] = true;
		g.submits++;

		if (!Check(g.ctx->Wait(g.out11.Get(), done), "D3D11 Wait")) { Fail("fence (above)."); return; }
		TimingMark(4);
		if (rc != FFX_API_RETURN_OK)
		{
			Log("ffxDispatch failed: %u (frame %llu).", rc, (unsigned long long)g.stats.frames);
			CheckRemoved();
			if (deviceLost || ++g.errors >= 10) Fail(deviceLost ? "device lost." : "ten failed dispatches.");
			return;
		}
		if (!(fd.direct & DirectOutput)) g.ctx->CopyResource((ID3D11Resource*)fd.output, g.output.t11.Get());
		g.stats.frames++;
	}

	void Release()
	{
		if (!g.d12) return;
		if (!WaitIdle("release")) { Teardown("the queue was not idle at release"); return; }
		ReleaseShared();
		Log("Bridge released its shared textures (device and FSR context kept).");
	}

	const int TCodes = 48;
	void TimingMark(int) {}

	void __stdcall OnRenderEvent(int eventId, void* data)
	{
		__try
		{
			if (g.teardownDue) { g.teardownDue = false; Teardown("after an exception"); }
			if (eventId == 1 && data)
			{
				LARGE_INTEGER t0, t1, f;
				QueryPerformanceCounter(&t0);
				uint64_t before = g.stats.frames;
				made = false;
				TimingMark(1);
				Upscale(*(const FrameData*)data);
				TimingMark(2);
				QueryPerformanceCounter(&t1);
				QueryPerformanceFrequency(&f);
				if (g.stats.frames != before && !made) Average(g.stats.cpuMs, (t1.QuadPart - t0.QuadPart) * 1000.0 / f.QuadPart);
			}
			else if (eventId == 2) Release();
			else if (eventId == 3) g.stats.presented++;
			else if (eventId >= 100 && eventId < 100 + TCodes) TimingMark(eventId - 100);
		}
		__except (EXCEPTION_EXECUTE_HANDLER)
		{
			g.teardownDue = true;
			g.errors = 0;
			g.stats.failures++;
			g.failed = true;
			Log("FSR stopped: exception 0x%08X on the render thread.", (unsigned)GetExceptionCode());
		}
		Publish();
	}
}

EXPORT int FSR4_Load(const wchar_t* dir)
{
	if (g.loaded) return 1;
	std::wstring d(dir);
	if (!d.empty() && d.back() != L'\\') d += L'\\';
	g.upscalerDll = LoadLibraryExW((d + L"amd_fidelityfx_upscaler_dx12.dll").c_str(), nullptr, LOAD_WITH_ALTERED_SEARCH_PATH);
	if (!g.upscalerDll) { Log("Could not load amd_fidelityfx_upscaler_dx12.dll: error %lu.", GetLastError()); return 0; }
	g.loaderDll = LoadLibraryExW((d + L"amd_fidelityfx_loader_dx12.dll").c_str(), nullptr, LOAD_WITH_ALTERED_SEARCH_PATH);
	if (!g.loaderDll) { Log("Could not load amd_fidelityfx_loader_dx12.dll: error %lu.", GetLastError()); return 0; }
	ffxLoadFunctions(&g.fx, g.loaderDll);
	if (!g.fx.CreateContext || !g.fx.Dispatch || !g.fx.Query || !g.fx.DestroyContext) { Log("The FSR loader lacks its entry points."); return 0; }
	g.loaded = true;
	return 1;
}

EXPORT void* FSR4_GetRenderEventFunc() { return (void*)&OnRenderEvent; }

EXPORT int FSR4_PollLog(char* buf, int cap)
{
	std::lock_guard<std::mutex> l(g_logLock);
	if (cap <= 0) return 0;
	if (g_log.empty())
	{
		if (g_logDropped == g_logDroppedReported) return 0;
		int n = snprintf(buf, cap, "(%llu bridge log lines lost: the log was not read in time.)", (unsigned long long)(g_logDropped - g_logDroppedReported));
		g_logDroppedReported = g_logDropped;
		return n < 0 ? 0 : min(n, cap - 1);
	}
	int n = (int)min((size_t)(cap - 1), g_log.front().size());
	memcpy(buf, g_log.front().data(), n);
	buf[n] = 0;
	g_log.pop_front();
	return n;
}

EXPORT void FSR4_GetStats(Stats* out)
{
	if (!out) return;
	{
		std::lock_guard<std::mutex> l(g_statsLock);
		*out = g_published;
	}
	out->failed = g.failed ? 1 : 0;
	std::lock_guard<std::mutex> l(g_logLock);
	out->logDropped = g_logDropped;
}

EXPORT int FSR4_Failed() { return g.failed ? 1 : 0; }

EXPORT int FSR4_ExposureWanted() { return g.exposureRequired ? 1 : 0; }

EXPORT uint64_t FSR4_GetViews(void** color, void** motion, void** output)
{
	std::lock_guard<std::mutex> l(g_viewLock);
	*color = g.color.srv.Get(); *motion = g.motion.srv.Get(); *output = g.output.srv.Get();
	return *color && *motion && *output ? g_viewGen : 0;
}

EXPORT void FSR4_ViewsInUse(uint64_t gen)
{
	std::lock_guard<std::mutex> l(g_viewLock);
	g_viewTicks++;
	for (size_t i = 0; i < g_retired.size();)
	{
		Retired& r = g_retired[i];
		if (gen != 0 && r.gen >= gen) { r.unusedSince = 0; i++; continue; }
		if (!r.unusedSince) r.unusedSince = g_viewTicks;
		if (g_viewTicks - r.unusedSince >= 3) g_retired.erase(g_retired.begin() + i);
		else i++;
	}
}

EXPORT void FSR4_ClearFailure() { g.failed = false; }

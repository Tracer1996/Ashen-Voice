#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <d3d9.h>
#include <atomic>
#include <fstream>
#include <mutex>
#include <string>
#include <thread>
#include <vector>

#pragma comment(lib, "d3d9.lib")

using EndSceneFn = HRESULT(__stdcall*)(IDirect3DDevice9*);
static EndSceneFn g_originalEndScene = nullptr;
static BYTE g_savedBytes[5]{};
static void* g_endSceneAddress = nullptr;
static std::atomic<bool> g_running{true};
static std::mutex g_speakerMutex;
static std::vector<std::string> g_speakers;
static std::string g_status = "Waiting for Discord companion";

struct Vertex { float x, y, z, rhw; D3DCOLOR color; };
static constexpr DWORD FVF = D3DFVF_XYZRHW | D3DFVF_DIFFUSE;

static const unsigned char FONT[37][7] = {
  {0x0E,0x11,0x13,0x15,0x19,0x11,0x0E}, // 0
  {0x04,0x0C,0x04,0x04,0x04,0x04,0x0E}, // 1
  {0x0E,0x11,0x01,0x02,0x04,0x08,0x1F}, // 2
  {0x1E,0x01,0x01,0x0E,0x01,0x01,0x1E}, // 3
  {0x02,0x06,0x0A,0x12,0x1F,0x02,0x02}, // 4
  {0x1F,0x10,0x10,0x1E,0x01,0x01,0x1E}, // 5
  {0x0E,0x10,0x10,0x1E,0x11,0x11,0x0E}, // 6
  {0x1F,0x01,0x02,0x04,0x08,0x08,0x08}, // 7
  {0x0E,0x11,0x11,0x0E,0x11,0x11,0x0E}, // 8
  {0x0E,0x11,0x11,0x0F,0x01,0x01,0x0E}, // 9
  {0x0E,0x11,0x11,0x1F,0x11,0x11,0x11}, // A
  {0x1E,0x11,0x11,0x1E,0x11,0x11,0x1E}, // B
  {0x0E,0x11,0x10,0x10,0x10,0x11,0x0E}, // C
  {0x1E,0x11,0x11,0x11,0x11,0x11,0x1E}, // D
  {0x1F,0x10,0x10,0x1E,0x10,0x10,0x1F}, // E
  {0x1F,0x10,0x10,0x1E,0x10,0x10,0x10}, // F
  {0x0E,0x11,0x10,0x17,0x11,0x11,0x0F}, // G
  {0x11,0x11,0x11,0x1F,0x11,0x11,0x11}, // H
  {0x0E,0x04,0x04,0x04,0x04,0x04,0x0E}, // I
  {0x07,0x02,0x02,0x02,0x12,0x12,0x0C}, // J
  {0x11,0x12,0x14,0x18,0x14,0x12,0x11}, // K
  {0x10,0x10,0x10,0x10,0x10,0x10,0x1F}, // L
  {0x11,0x1B,0x15,0x15,0x11,0x11,0x11}, // M
  {0x11,0x19,0x15,0x13,0x11,0x11,0x11}, // N
  {0x0E,0x11,0x11,0x11,0x11,0x11,0x0E}, // O
  {0x1E,0x11,0x11,0x1E,0x10,0x10,0x10}, // P
  {0x0E,0x11,0x11,0x11,0x15,0x12,0x0D}, // Q
  {0x1E,0x11,0x11,0x1E,0x14,0x12,0x11}, // R
  {0x0F,0x10,0x10,0x0E,0x01,0x01,0x1E}, // S
  {0x1F,0x04,0x04,0x04,0x04,0x04,0x04}, // T
  {0x11,0x11,0x11,0x11,0x11,0x11,0x0E}, // U
  {0x11,0x11,0x11,0x11,0x11,0x0A,0x04}, // V
  {0x11,0x11,0x11,0x15,0x15,0x15,0x0A}, // W
  {0x11,0x11,0x0A,0x04,0x0A,0x11,0x11}, // X
  {0x11,0x11,0x0A,0x04,0x04,0x04,0x04}, // Y
  {0x1F,0x01,0x02,0x04,0x08,0x10,0x1F}, // Z
  {0,0,0,0,0,0,0} // space/unknown
};

static int GlyphIndex(char c) {
  if (c >= '0' && c <= '9') return c - '0';
  if (c >= 'a' && c <= 'z') c = char(c - 32);
  if (c >= 'A' && c <= 'Z') return 10 + (c - 'A');
  return 36;
}

static void Rect(IDirect3DDevice9* d, float x, float y, float w, float h, D3DCOLOR c) {
  Vertex v[4] = {{x,y,0,1,c},{x+w,y,0,1,c},{x,y+h,0,1,c},{x+w,y+h,0,1,c}};
  d->DrawPrimitiveUP(D3DPT_TRIANGLESTRIP, 2, v, sizeof(Vertex));
}

static void Text(IDirect3DDevice9* d, float x, float y, const std::string& s, float scale, D3DCOLOR c) {
  float cursor = x;
  for (char ch : s) {
    if (ch == ' ') { cursor += 4.0f * scale; continue; }
    const auto& rows = FONT[GlyphIndex(ch)];
    for (int row=0; row<7; ++row) for (int col=0; col<5; ++col)
      if (rows[row] & (1 << (4-col))) Rect(d, cursor + col*scale, y + row*scale, scale, scale, c);
    cursor += 6.0f * scale;
  }
}

static std::string SpeakerPath() {
  char path[MAX_PATH]{};
  DWORD n = GetEnvironmentVariableA("LOCALAPPDATA", path, MAX_PATH);
  std::string base = n ? std::string(path, n) : ".";
  return base + "\\AshenVoice\\speakers.txt";
}

static void ReaderThread() {
  while (g_running) {
    std::ifstream in(SpeakerPath());
    std::vector<std::string> names;
    std::string line;
    while (std::getline(in, line)) {
      if (!line.empty() && names.size() < 10) names.push_back(line.substr(0, 24));
    }
    {
      std::lock_guard<std::mutex> lock(g_speakerMutex);
      g_speakers = std::move(names);
      g_status = in.good() || in.eof() ? "" : "START DISCORD COMPANION";
    }
    Sleep(100);
  }
}

static void Render(IDirect3DDevice9* d) {
  std::vector<std::string> speakers;
  std::string status;
  {
    std::lock_guard<std::mutex> lock(g_speakerMutex);
    speakers = g_speakers;
    status = g_status;
  }
  if (speakers.empty() && status.empty()) return;

  D3DVIEWPORT9 vp{}; if (FAILED(d->GetViewport(&vp))) return;
  IDirect3DStateBlock9* state = nullptr;
  if (SUCCEEDED(d->CreateStateBlock(D3DSBT_ALL, &state))) state->Capture();
  d->SetTexture(0, nullptr);
  d->SetFVF(FVF);
  d->SetRenderState(D3DRS_ALPHABLENDENABLE, TRUE);
  d->SetRenderState(D3DRS_SRCBLEND, D3DBLEND_SRCALPHA);
  d->SetRenderState(D3DRS_DESTBLEND, D3DBLEND_INVSRCALPHA);
  d->SetRenderState(D3DRS_ZENABLE, FALSE);
  d->SetRenderState(D3DRS_LIGHTING, FALSE);

  const float width = 250.0f;
  const float x = float(vp.Width) - width - 18.0f;
  const float y = 22.0f;
  size_t rows = speakers.empty() ? 1 : speakers.size();
  Rect(d, x, y, width, 34.0f + rows*24.0f, D3DCOLOR_ARGB(165, 15, 18, 24));
  Text(d, x+12, y+9, "DISCORD VOICE", 2.0f, D3DCOLOR_ARGB(255, 210, 214, 255));
  float cy = y + 34.0f;
  if (!status.empty()) {
    Text(d, x+12, cy+5, status, 1.0f, D3DCOLOR_ARGB(255, 255, 190, 90));
  } else {
    for (const auto& name : speakers) {
      Rect(d, x+12, cy+7, 8, 8, D3DCOLOR_ARGB(255, 88, 235, 140));
      Text(d, x+28, cy+4, name, 1.5f, D3DCOLOR_ARGB(255, 255, 255, 255));
      cy += 24.0f;
    }
  }
  if (state) { state->Apply(); state->Release(); }
}

static HRESULT __stdcall HookEndScene(IDirect3DDevice9* device) {
  Render(device);
  return g_originalEndScene(device);
}

static bool InstallHook(void* target, void* detour) {
  g_endSceneAddress = target;
  DWORD old{};
  if (!VirtualProtect(target, 5, PAGE_EXECUTE_READWRITE, &old)) return false;
  memcpy(g_savedBytes, target, 5);
  BYTE* trampoline = (BYTE*)VirtualAlloc(nullptr, 16, MEM_COMMIT|MEM_RESERVE, PAGE_EXECUTE_READWRITE);
  if (!trampoline) return false;
  memcpy(trampoline, target, 5);
  trampoline[5] = 0xE9;
  *(DWORD*)(trampoline+6) = (DWORD)((BYTE*)target + 5 - (trampoline + 10));
  g_originalEndScene = (EndSceneFn)trampoline;
  BYTE patch[5] = {0xE9,0,0,0,0};
  *(DWORD*)(patch+1) = (DWORD)((BYTE*)detour - ((BYTE*)target + 5));
  memcpy(target, patch, 5);
  FlushInstructionCache(GetCurrentProcess(), target, 5);
  VirtualProtect(target, 5, old, &old);
  return true;
}

static LRESULT CALLBACK DummyWndProc(HWND h, UINT m, WPARAM w, LPARAM l) { return DefWindowProcA(h,m,w,l); }

static DWORD WINAPI InitThread(void*) {
  while (!GetModuleHandleA("d3d9.dll") && g_running) Sleep(100);
  WNDCLASSEXA wc{sizeof(wc), CS_CLASSDC, DummyWndProc, 0,0,GetModuleHandleA(nullptr),nullptr,nullptr,nullptr,nullptr,"AshenVoiceDummy",nullptr};
  RegisterClassExA(&wc);
  HWND hwnd = CreateWindowA(wc.lpszClassName, "", WS_OVERLAPPEDWINDOW, 0,0,100,100,nullptr,nullptr,wc.hInstance,nullptr);
  IDirect3D9* d3d = Direct3DCreate9(D3D_SDK_VERSION);
  if (!d3d) return 1;
  D3DPRESENT_PARAMETERS pp{}; pp.Windowed=TRUE; pp.SwapEffect=D3DSWAPEFFECT_DISCARD; pp.hDeviceWindow=hwnd;
  IDirect3DDevice9* dev = nullptr;
  HRESULT hr = d3d->CreateDevice(D3DADAPTER_DEFAULT,D3DDEVTYPE_HAL,hwnd,D3DCREATE_SOFTWARE_VERTEXPROCESSING,&pp,&dev);
  if (FAILED(hr)) hr = d3d->CreateDevice(D3DADAPTER_DEFAULT,D3DDEVTYPE_REF,hwnd,D3DCREATE_SOFTWARE_VERTEXPROCESSING,&pp,&dev);
  if (SUCCEEDED(hr)) {
    void** vtable = *(void***)dev;
    InstallHook(vtable[42], (void*)&HookEndScene);
    dev->Release();
  }
  d3d->Release(); DestroyWindow(hwnd); UnregisterClassA(wc.lpszClassName,wc.hInstance);
  std::thread(ReaderThread).detach();
  return 0;
}

BOOL APIENTRY DllMain(HMODULE module, DWORD reason, LPVOID) {
  if (reason == DLL_PROCESS_ATTACH) {
    DisableThreadLibraryCalls(module);
    CreateThread(nullptr,0,InitThread,nullptr,0,nullptr);
  } else if (reason == DLL_PROCESS_DETACH) {
    g_running = false;
    if (g_endSceneAddress) {
      DWORD old{}; VirtualProtect(g_endSceneAddress,5,PAGE_EXECUTE_READWRITE,&old);
      memcpy(g_endSceneAddress,g_savedBytes,5); FlushInstructionCache(GetCurrentProcess(),g_endSceneAddress,5);
      VirtualProtect(g_endSceneAddress,5,old,&old);
    }
  }
  return TRUE;
}

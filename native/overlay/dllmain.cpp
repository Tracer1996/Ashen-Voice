#include <windows.h>
#include <d3d9.h>
#include <MinHook.h>

#include <array>
#include <cstdint>
#include <cstdio>
#include <string>

namespace
{
    using EndSceneFunction = HRESULT(APIENTRY*)(IDirect3DDevice9*);

    HMODULE g_module = nullptr;
    EndSceneFunction g_originalEndScene = nullptr;
    void* g_endSceneAddress = nullptr;
    HANDLE g_stopEvent = nullptr;
    HANDLE g_readyEvent = nullptr;

    struct Vertex
    {
        float x;
        float y;
        float z;
        float rhw;
        D3DCOLOR color;
    };

    constexpr DWORD VertexFormat = D3DFVF_XYZRHW | D3DFVF_DIFFUSE;

    std::wstring EventName(const wchar_t* prefix)
    {
        wchar_t buffer[96]{};
        swprintf_s(buffer, _countof(buffer), L"Local\\AshenVoice_%ls_%lu", prefix, GetCurrentProcessId());
        return buffer;
    }

    void WriteNativeLog(const wchar_t* message)
    {
        wchar_t localAppData[MAX_PATH]{};
        const DWORD length = GetEnvironmentVariableW(L"LOCALAPPDATA", localAppData, MAX_PATH);
        if (length == 0 || length >= MAX_PATH)
        {
            return;
        }

        std::wstring directory = std::wstring(localAppData) + L"\\AshenVoice";
        CreateDirectoryW(directory.c_str(), nullptr);
        const std::wstring filePath = directory + L"\\overlay-native.log";

        HANDLE file = CreateFileW(
            filePath.c_str(),
            FILE_APPEND_DATA,
            FILE_SHARE_READ | FILE_SHARE_WRITE,
            nullptr,
            OPEN_ALWAYS,
            FILE_ATTRIBUTE_NORMAL,
            nullptr);

        if (file == INVALID_HANDLE_VALUE)
        {
            return;
        }

        SYSTEMTIME time{};
        GetLocalTime(&time);

        wchar_t wideLine[768]{};
        swprintf_s(
            wideLine,
            _countof(wideLine),
            L"[%02u:%02u:%02u] %ls\r\n",
            time.wHour,
            time.wMinute,
            time.wSecond,
            message);

        const int utf8Length = WideCharToMultiByte(
            CP_UTF8,
            0,
            wideLine,
            -1,
            nullptr,
            0,
            nullptr,
            nullptr);

        if (utf8Length > 1)
        {
            std::string utf8(static_cast<size_t>(utf8Length), '\0');
            WideCharToMultiByte(
                CP_UTF8,
                0,
                wideLine,
                -1,
                utf8.data(),
                utf8Length,
                nullptr,
                nullptr);

            DWORD written = 0;
            WriteFile(file, utf8.data(), static_cast<DWORD>(utf8.size() - 1), &written, nullptr);
        }

        CloseHandle(file);
    }

    void DrawRectangle(IDirect3DDevice9* device, float left, float top, float right, float bottom, D3DCOLOR color)
    {
        const std::array<Vertex, 6> vertices = {
            Vertex{left,  top,    0.0f, 1.0f, color},
            Vertex{right, top,    0.0f, 1.0f, color},
            Vertex{right, bottom, 0.0f, 1.0f, color},
            Vertex{left,  top,    0.0f, 1.0f, color},
            Vertex{right, bottom, 0.0f, 1.0f, color},
            Vertex{left,  bottom, 0.0f, 1.0f, color}
        };

        device->DrawPrimitiveUP(
            D3DPT_TRIANGLELIST,
            2,
            vertices.data(),
            sizeof(Vertex));
    }

    std::array<std::uint8_t, 7> Glyph(char character)
    {
        switch (character)
        {
        case 'A': return {0x0E, 0x11, 0x11, 0x1F, 0x11, 0x11, 0x11};
        case 'B': return {0x1E, 0x11, 0x11, 0x1E, 0x11, 0x11, 0x1E};
        case 'C': return {0x0E, 0x11, 0x10, 0x10, 0x10, 0x11, 0x0E};
        case 'D': return {0x1E, 0x11, 0x11, 0x11, 0x11, 0x11, 0x1E};
        case 'E': return {0x1F, 0x10, 0x10, 0x1E, 0x10, 0x10, 0x1F};
        case 'F': return {0x1F, 0x10, 0x10, 0x1E, 0x10, 0x10, 0x10};
        case 'G': return {0x0E, 0x11, 0x10, 0x17, 0x11, 0x11, 0x0F};
        case 'H': return {0x11, 0x11, 0x11, 0x1F, 0x11, 0x11, 0x11};
        case 'I': return {0x1F, 0x04, 0x04, 0x04, 0x04, 0x04, 0x1F};
        case 'J': return {0x07, 0x02, 0x02, 0x02, 0x12, 0x12, 0x0C};
        case 'K': return {0x11, 0x12, 0x14, 0x18, 0x14, 0x12, 0x11};
        case 'L': return {0x10, 0x10, 0x10, 0x10, 0x10, 0x10, 0x1F};
        case 'M': return {0x11, 0x1B, 0x15, 0x15, 0x11, 0x11, 0x11};
        case 'N': return {0x11, 0x19, 0x15, 0x13, 0x11, 0x11, 0x11};
        case 'O': return {0x0E, 0x11, 0x11, 0x11, 0x11, 0x11, 0x0E};
        case 'P': return {0x1E, 0x11, 0x11, 0x1E, 0x10, 0x10, 0x10};
        case 'Q': return {0x0E, 0x11, 0x11, 0x11, 0x15, 0x12, 0x0D};
        case 'R': return {0x1E, 0x11, 0x11, 0x1E, 0x14, 0x12, 0x11};
        case 'S': return {0x0F, 0x10, 0x10, 0x0E, 0x01, 0x01, 0x1E};
        case 'T': return {0x1F, 0x04, 0x04, 0x04, 0x04, 0x04, 0x04};
        case 'U': return {0x11, 0x11, 0x11, 0x11, 0x11, 0x11, 0x0E};
        case 'V': return {0x11, 0x11, 0x11, 0x11, 0x11, 0x0A, 0x04};
        case 'W': return {0x11, 0x11, 0x11, 0x15, 0x15, 0x15, 0x0A};
        case 'X': return {0x11, 0x11, 0x0A, 0x04, 0x0A, 0x11, 0x11};
        case 'Y': return {0x11, 0x11, 0x0A, 0x04, 0x04, 0x04, 0x04};
        case 'Z': return {0x1F, 0x01, 0x02, 0x04, 0x08, 0x10, 0x1F};
        case '0': return {0x0E, 0x11, 0x13, 0x15, 0x19, 0x11, 0x0E};
        case '1': return {0x04, 0x0C, 0x14, 0x04, 0x04, 0x04, 0x1F};
        case '2': return {0x0E, 0x11, 0x01, 0x02, 0x04, 0x08, 0x1F};
        case '3': return {0x1E, 0x01, 0x01, 0x0E, 0x01, 0x01, 0x1E};
        case '4': return {0x02, 0x06, 0x0A, 0x12, 0x1F, 0x02, 0x02};
        case '5': return {0x1F, 0x10, 0x10, 0x1E, 0x01, 0x01, 0x1E};
        case '6': return {0x0E, 0x10, 0x10, 0x1E, 0x11, 0x11, 0x0E};
        case '7': return {0x1F, 0x01, 0x02, 0x04, 0x08, 0x08, 0x08};
        case '8': return {0x0E, 0x11, 0x11, 0x0E, 0x11, 0x11, 0x0E};
        case '9': return {0x0E, 0x11, 0x11, 0x0F, 0x01, 0x01, 0x0E};
        case '-': return {0x00, 0x00, 0x00, 0x1F, 0x00, 0x00, 0x00};
        case ':': return {0x00, 0x04, 0x04, 0x00, 0x04, 0x04, 0x00};
        case '.': return {0x00, 0x00, 0x00, 0x00, 0x00, 0x0C, 0x0C};
        default:  return {0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00};
        }
    }

    void DrawText(
        IDirect3DDevice9* device,
        const std::string& text,
        float x,
        float y,
        float scale,
        D3DCOLOR color)
    {
        float cursorX = x;
        const float advance = 6.0f * scale;

        for (char character : text)
        {
            if (character >= 'a' && character <= 'z')
            {
                character = static_cast<char>(character - 'a' + 'A');
            }

            if (character == ' ')
            {
                cursorX += advance;
                continue;
            }

            const auto glyph = Glyph(character);
            for (int row = 0; row < 7; ++row)
            {
                for (int column = 0; column < 5; ++column)
                {
                    const std::uint8_t mask = static_cast<std::uint8_t>(1u << (4 - column));
                    if ((glyph[static_cast<size_t>(row)] & mask) != 0)
                    {
                        const float left = cursorX + static_cast<float>(column) * scale;
                        const float top = y + static_cast<float>(row) * scale;
                        DrawRectangle(device, left, top, left + scale, top + scale, color);
                    }
                }
            }

            cursorX += advance;
        }
    }

    void DrawOverlay(IDirect3DDevice9* device)
    {
        if (device == nullptr)
        {
            return;
        }

        D3DVIEWPORT9 viewport{};
        if (FAILED(device->GetViewport(&viewport)))
        {
            return;
        }

        IDirect3DStateBlock9* stateBlock = nullptr;
        if (SUCCEEDED(device->CreateStateBlock(D3DSBT_ALL, &stateBlock)) && stateBlock != nullptr)
        {
            stateBlock->Capture();
        }

        device->SetTexture(0, nullptr);
        device->SetPixelShader(nullptr);
        device->SetVertexShader(nullptr);
        device->SetFVF(VertexFormat);
        device->SetRenderState(D3DRS_ZENABLE, FALSE);
        device->SetRenderState(D3DRS_ZWRITEENABLE, FALSE);
        device->SetRenderState(D3DRS_CULLMODE, D3DCULL_NONE);
        device->SetRenderState(D3DRS_ALPHABLENDENABLE, TRUE);
        device->SetRenderState(D3DRS_SRCBLEND, D3DBLEND_SRCALPHA);
        device->SetRenderState(D3DRS_DESTBLEND, D3DBLEND_INVSRCALPHA);
        device->SetRenderState(D3DRS_LIGHTING, FALSE);
        device->SetRenderState(D3DRS_SCISSORTESTENABLE, FALSE);

        constexpr float panelWidth = 330.0f;
        constexpr float panelHeight = 220.0f;
        const float panelX = static_cast<float>(viewport.X + viewport.Width) - panelWidth - 28.0f;
        const float panelY = static_cast<float>(viewport.Y) + 36.0f;

        const D3DCOLOR panel = D3DCOLOR_ARGB(220, 18, 20, 24);
        const D3DCOLOR panelEdge = D3DCOLOR_ARGB(235, 64, 68, 78);
        const D3DCOLOR ember = D3DCOLOR_ARGB(255, 240, 113, 50);
        const D3DCOLOR white = D3DCOLOR_ARGB(255, 242, 242, 242);
        const D3DCOLOR muted = D3DCOLOR_ARGB(255, 170, 174, 182);
        const D3DCOLOR green = D3DCOLOR_ARGB(255, 84, 224, 123);

        DrawRectangle(device, panelX - 2.0f, panelY - 2.0f, panelX + panelWidth + 2.0f, panelY + panelHeight + 2.0f, panelEdge);
        DrawRectangle(device, panelX, panelY, panelX + panelWidth, panelY + panelHeight, panel);
        DrawRectangle(device, panelX, panelY, panelX + panelWidth, panelY + 5.0f, ember);

        DrawText(device, "ASHEN VOICE", panelX + 18.0f, panelY + 20.0f, 2.0f, ember);
        DrawText(device, "OVERLAY CONNECTED", panelX + 18.0f, panelY + 45.0f, 1.0f, muted);

        const std::array<const char*, 3> speakers = {
            "METHL - SPEAKING",
            "DANARI - SPEAKING",
            "POETRY - SPEAKING"
        };

        float speakerY = panelY + 78.0f;
        for (const char* speaker : speakers)
        {
            DrawRectangle(device, panelX + 18.0f, speakerY + 2.0f, panelX + 26.0f, speakerY + 10.0f, green);
            DrawText(device, speaker, panelX + 38.0f, speakerY, 1.4f, white);
            speakerY += 34.0f;
        }

        DrawText(device, "PHASE 2 TEST PANEL", panelX + 18.0f, panelY + 196.0f, 1.0f, muted);

        if (stateBlock != nullptr)
        {
            stateBlock->Apply();
            stateBlock->Release();
        }
    }

    HRESULT APIENTRY HookedEndScene(IDirect3DDevice9* device)
    {
        DrawOverlay(device);
        return g_originalEndScene(device);
    }

    LRESULT CALLBACK DummyWindowProcedure(HWND window, UINT message, WPARAM wParam, LPARAM lParam)
    {
        return DefWindowProcW(window, message, wParam, lParam);
    }

    void* LocateEndScene()
    {
        const wchar_t* className = L"AshenVoiceD3D9Probe";

        WNDCLASSEXW windowClass{};
        windowClass.cbSize = sizeof(windowClass);
        windowClass.lpfnWndProc = DummyWindowProcedure;
        windowClass.hInstance = g_module;
        windowClass.lpszClassName = className;

        const ATOM atom = RegisterClassExW(&windowClass);
        if (atom == 0 && GetLastError() != ERROR_CLASS_ALREADY_EXISTS)
        {
            WriteNativeLog(L"Failed to register the DirectX probe window class.");
            return nullptr;
        }

        HWND window = CreateWindowExW(
            0,
            className,
            L"Ashen Voice Probe",
            WS_OVERLAPPEDWINDOW,
            0,
            0,
            100,
            100,
            nullptr,
            nullptr,
            g_module,
            nullptr);

        if (window == nullptr)
        {
            WriteNativeLog(L"Failed to create the DirectX probe window.");
            UnregisterClassW(className, g_module);
            return nullptr;
        }

        IDirect3D9* direct3D = Direct3DCreate9(D3D_SDK_VERSION);
        if (direct3D == nullptr)
        {
            WriteNativeLog(L"Direct3DCreate9 failed.");
            DestroyWindow(window);
            UnregisterClassW(className, g_module);
            return nullptr;
        }

        D3DPRESENT_PARAMETERS parameters{};
        parameters.Windowed = TRUE;
        parameters.SwapEffect = D3DSWAPEFFECT_DISCARD;
        parameters.hDeviceWindow = window;
        parameters.BackBufferFormat = D3DFMT_UNKNOWN;

        IDirect3DDevice9* device = nullptr;
        HRESULT result = direct3D->CreateDevice(
            D3DADAPTER_DEFAULT,
            D3DDEVTYPE_HAL,
            window,
            D3DCREATE_SOFTWARE_VERTEXPROCESSING,
            &parameters,
            &device);

        if (FAILED(result))
        {
            result = direct3D->CreateDevice(
                D3DADAPTER_DEFAULT,
                D3DDEVTYPE_REF,
                window,
                D3DCREATE_SOFTWARE_VERTEXPROCESSING,
                &parameters,
                &device);
        }

        void* endScene = nullptr;
        if (SUCCEEDED(result) && device != nullptr)
        {
            void** virtualTable = *reinterpret_cast<void***>(device);
            endScene = virtualTable[42];
            device->Release();
        }
        else
        {
            WriteNativeLog(L"Failed to create the DirectX 9 probe device.");
        }

        direct3D->Release();
        DestroyWindow(window);
        UnregisterClassW(className, g_module);
        return endScene;
    }

    bool InstallHook()
    {
        g_endSceneAddress = LocateEndScene();
        if (g_endSceneAddress == nullptr)
        {
            return false;
        }

        if (MH_Initialize() != MH_OK)
        {
            WriteNativeLog(L"MinHook initialization failed.");
            return false;
        }

        if (MH_CreateHook(
                g_endSceneAddress,
                reinterpret_cast<void*>(&HookedEndScene),
                reinterpret_cast<void**>(&g_originalEndScene)) != MH_OK)
        {
            WriteNativeLog(L"Could not create the DirectX 9 EndScene hook.");
            MH_Uninitialize();
            return false;
        }

        if (MH_EnableHook(g_endSceneAddress) != MH_OK)
        {
            WriteNativeLog(L"Could not enable the DirectX 9 EndScene hook.");
            MH_RemoveHook(g_endSceneAddress);
            MH_Uninitialize();
            return false;
        }

        WriteNativeLog(L"DirectX 9 EndScene hook installed.");
        return true;
    }

    void RemoveHook()
    {
        if (g_endSceneAddress != nullptr)
        {
            MH_DisableHook(g_endSceneAddress);
            MH_RemoveHook(g_endSceneAddress);
            g_endSceneAddress = nullptr;
        }

        MH_Uninitialize();
        WriteNativeLog(L"DirectX 9 hook removed.");
    }

    DWORD WINAPI OverlayThread(void* parameter)
    {
        g_module = static_cast<HMODULE>(parameter);

        const std::wstring stopEventName = EventName(L"Stop");
        const std::wstring readyEventName = EventName(L"Ready");
        g_stopEvent = CreateEventW(nullptr, TRUE, FALSE, stopEventName.c_str());
        g_readyEvent = CreateEventW(nullptr, TRUE, FALSE, readyEventName.c_str());

        if (g_stopEvent == nullptr || g_readyEvent == nullptr)
        {
            WriteNativeLog(L"Could not create overlay control events.");
            if (g_stopEvent != nullptr) CloseHandle(g_stopEvent);
            if (g_readyEvent != nullptr) CloseHandle(g_readyEvent);
            FreeLibraryAndExitThread(g_module, 1);
        }

        if (!InstallHook())
        {
            WriteNativeLog(L"Overlay initialization failed.");
            CloseHandle(g_stopEvent);
            CloseHandle(g_readyEvent);
            FreeLibraryAndExitThread(g_module, 2);
        }

        SetEvent(g_readyEvent);
        WaitForSingleObject(g_stopEvent, INFINITE);

        RemoveHook();
        CloseHandle(g_stopEvent);
        CloseHandle(g_readyEvent);
        g_stopEvent = nullptr;
        g_readyEvent = nullptr;

        Sleep(100);
        FreeLibraryAndExitThread(g_module, 0);
    }
}

BOOL APIENTRY DllMain(HMODULE module, DWORD reason, LPVOID)
{
    if (reason == DLL_PROCESS_ATTACH)
    {
        DisableThreadLibraryCalls(module);
        HANDLE thread = CreateThread(nullptr, 0, OverlayThread, module, 0, nullptr);
        if (thread != nullptr)
        {
            CloseHandle(thread);
        }
    }

    return TRUE;
}

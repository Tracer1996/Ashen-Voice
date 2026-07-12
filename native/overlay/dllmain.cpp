#include <windows.h>
#include <d3d9.h>
#include <wincodec.h>
#include <MinHook.h>

#include <algorithm>
#include <array>
#include <cmath>
#include <cstdint>
#include <cstdio>
#include <cstring>
#include <filesystem>
#include <fstream>
#include <sstream>
#include <string>
#include <unordered_map>
#include <utility>
#include <vector>

namespace
{
    using EndSceneFunction = HRESULT(APIENTRY*)(IDirect3DDevice9*);

    HMODULE g_module = nullptr;
    EndSceneFunction g_originalEndScene = nullptr;
    void* g_endSceneAddress = nullptr;
    HANDLE g_stopEvent = nullptr;
    HANDLE g_readyEvent = nullptr;

    struct ColorVertex
    {
        float x;
        float y;
        float z;
        float rhw;
        D3DCOLOR color;
    };

    struct TextureVertex
    {
        float x;
        float y;
        float z;
        float rhw;
        D3DCOLOR color;
        float u;
        float v;
    };

    struct Speaker
    {
        std::wstring name;
        std::wstring avatarPath;
    };

    constexpr DWORD ColorVertexFormat = D3DFVF_XYZRHW | D3DFVF_DIFFUSE;
    constexpr DWORD TextureVertexFormat = D3DFVF_XYZRHW | D3DFVF_DIFFUSE | D3DFVF_TEX1;
    constexpr std::size_t MaximumSpeakers = 5;

    std::wstring g_statePath;
    ULONGLONG g_lastStateRead = 0;
    std::vector<Speaker> g_speakers;
    IDirect3DDevice9* g_textureDevice = nullptr;
    IWICImagingFactory* g_wicFactory = nullptr;
    bool g_wicAttempted = false;
    std::unordered_map<std::wstring, IDirect3DTexture9*> g_avatarTextures;
    std::unordered_map<std::wstring, IDirect3DTexture9*> g_textTextures;

    std::wstring EventName(const wchar_t* prefix)
    {
        wchar_t buffer[96]{};
        swprintf_s(buffer, _countof(buffer), L"Local\\AshenVoice_%ls_%lu", prefix, GetCurrentProcessId());
        return buffer;
    }

    std::wstring LocalAppDataDirectory()
    {
        wchar_t localAppData[MAX_PATH]{};
        const DWORD length = GetEnvironmentVariableW(L"LOCALAPPDATA", localAppData, MAX_PATH);
        if (length == 0 || length >= MAX_PATH)
        {
            return {};
        }

        return std::wstring(localAppData) + L"\\AshenVoice";
    }

    void WriteNativeLog(const wchar_t* message)
    {
        const std::wstring directory = LocalAppDataDirectory();
        if (directory.empty())
        {
            return;
        }

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

        const int utf8Length = WideCharToMultiByte(CP_UTF8, 0, wideLine, -1, nullptr, 0, nullptr, nullptr);
        if (utf8Length > 1)
        {
            std::string utf8(static_cast<std::size_t>(utf8Length), '\0');
            WideCharToMultiByte(CP_UTF8, 0, wideLine, -1, utf8.data(), utf8Length, nullptr, nullptr);
            DWORD written = 0;
            WriteFile(file, utf8.data(), static_cast<DWORD>(utf8.size() - 1), &written, nullptr);
        }

        CloseHandle(file);
    }

    std::wstring Utf8ToWide(const std::string& value)
    {
        if (value.empty())
        {
            return {};
        }

        const int required = MultiByteToWideChar(CP_UTF8, MB_ERR_INVALID_CHARS, value.data(), static_cast<int>(value.size()), nullptr, 0);
        if (required <= 0)
        {
            return {};
        }

        std::wstring result(static_cast<std::size_t>(required), L'\0');
        MultiByteToWideChar(CP_UTF8, MB_ERR_INVALID_CHARS, value.data(), static_cast<int>(value.size()), result.data(), required);
        return result;
    }

    void RefreshSpeakers()
    {
        const ULONGLONG now = GetTickCount64();
        if (now - g_lastStateRead < 100)
        {
            return;
        }
        g_lastStateRead = now;

        if (g_statePath.empty())
        {
            const std::wstring directory = LocalAppDataDirectory();
            if (directory.empty())
            {
                return;
            }
            g_statePath = directory + L"\\speakers.tsv";
        }

        std::ifstream input(std::filesystem::path(g_statePath), std::ios::binary);
        if (!input)
        {
            g_speakers.clear();
            return;
        }

        std::ostringstream buffer;
        buffer << input.rdbuf();
        std::istringstream lines(buffer.str());

        std::vector<Speaker> speakers;
        std::string line;
        while (speakers.size() < MaximumSpeakers && std::getline(lines, line))
        {
            if (!line.empty() && line.back() == '\r')
            {
                line.pop_back();
            }

            const std::size_t separator = line.find('\t');
            const std::string nameBytes = separator == std::string::npos ? line : line.substr(0, separator);
            const std::string pathBytes = separator == std::string::npos ? std::string() : line.substr(separator + 1);

            std::wstring name = Utf8ToWide(nameBytes);
            if (name.empty())
            {
                continue;
            }

            if (name.size() > 48)
            {
                name.resize(48);
            }

            speakers.push_back(Speaker{std::move(name), Utf8ToWide(pathBytes)});
        }

        g_speakers = std::move(speakers);
    }

    void ReleaseTextures()
    {
        for (auto& entry : g_avatarTextures)
        {
            if (entry.second != nullptr)
            {
                entry.second->Release();
            }
        }
        g_avatarTextures.clear();

        for (auto& entry : g_textTextures)
        {
            if (entry.second != nullptr)
            {
                entry.second->Release();
            }
        }
        g_textTextures.clear();
        g_textureDevice = nullptr;
    }

    void EnsureDevice(IDirect3DDevice9* device)
    {
        if (g_textureDevice != device)
        {
            ReleaseTextures();
            g_textureDevice = device;
        }
    }

    bool EnsureWicFactory()
    {
        if (g_wicFactory != nullptr)
        {
            return true;
        }

        if (g_wicAttempted)
        {
            return false;
        }

        g_wicAttempted = true;
        const HRESULT initializeResult = CoInitializeEx(nullptr, COINIT_MULTITHREADED);
        if (FAILED(initializeResult) && initializeResult != RPC_E_CHANGED_MODE)
        {
            WriteNativeLog(L"COM initialization failed while preparing avatar support.");
            return false;
        }

        const HRESULT factoryResult = CoCreateInstance(
            CLSID_WICImagingFactory,
            nullptr,
            CLSCTX_INPROC_SERVER,
            IID_PPV_ARGS(&g_wicFactory));

        if (FAILED(factoryResult))
        {
            WriteNativeLog(L"Windows Imaging Component could not be initialized.");
            g_wicFactory = nullptr;
            return false;
        }

        return true;
    }

    IDirect3DTexture9* CreateTextureFromPixels(
        IDirect3DDevice9* device,
        UINT width,
        UINT height,
        const std::vector<std::uint32_t>& pixels)
    {
        if (device == nullptr || pixels.size() != static_cast<std::size_t>(width) * height)
        {
            return nullptr;
        }

        IDirect3DTexture9* texture = nullptr;
        if (FAILED(device->CreateTexture(
                width,
                height,
                1,
                0,
                D3DFMT_A8R8G8B8,
                D3DPOOL_MANAGED,
                &texture,
                nullptr)))
        {
            return nullptr;
        }

        D3DLOCKED_RECT locked{};
        if (FAILED(texture->LockRect(0, &locked, nullptr, 0)))
        {
            texture->Release();
            return nullptr;
        }

        for (UINT y = 0; y < height; ++y)
        {
            auto* destination = reinterpret_cast<std::uint8_t*>(locked.pBits) + static_cast<std::size_t>(y) * locked.Pitch;
            const auto* source = pixels.data() + static_cast<std::size_t>(y) * width;
            memcpy(destination, source, static_cast<std::size_t>(width) * sizeof(std::uint32_t));
        }

        texture->UnlockRect(0);
        return texture;
    }

    std::uint32_t HashColor(const std::wstring& value)
    {
        std::uint32_t hash = 2166136261u;
        for (wchar_t character : value)
        {
            hash ^= static_cast<std::uint32_t>(character);
            hash *= 16777619u;
        }

        const std::uint8_t red = static_cast<std::uint8_t>(90u + (hash & 0x5Fu));
        const std::uint8_t green = static_cast<std::uint8_t>(75u + ((hash >> 8u) & 0x6Fu));
        const std::uint8_t blue = static_cast<std::uint8_t>(95u + ((hash >> 16u) & 0x5Fu));
        return D3DCOLOR_ARGB(255, red, green, blue);
    }

    IDirect3DTexture9* CreateAvatarTexture(
        IDirect3DDevice9* device,
        const std::wstring& name,
        const std::wstring& avatarPath)
    {
        constexpr UINT textureSize = 36;
        constexpr UINT imageSize = 30;
        constexpr int imageOffset = 3;
        constexpr float center = 17.5f;
        constexpr float outerRadius = 17.5f;
        constexpr float innerRadius = 15.0f;
        const std::uint32_t ringColor = D3DCOLOR_ARGB(255, 35, 224, 117);
        const std::uint32_t fallbackColor = HashColor(name);

        std::vector<std::uint32_t> sourcePixels(static_cast<std::size_t>(imageSize) * imageSize, fallbackColor);
        bool loadedImage = false;

        if (!avatarPath.empty() && std::filesystem::exists(std::filesystem::path(avatarPath)) && EnsureWicFactory())
        {
            IWICBitmapDecoder* decoder = nullptr;
            IWICBitmapFrameDecode* frame = nullptr;
            IWICBitmapScaler* scaler = nullptr;
            IWICFormatConverter* converter = nullptr;

            HRESULT result = g_wicFactory->CreateDecoderFromFilename(
                avatarPath.c_str(),
                nullptr,
                GENERIC_READ,
                WICDecodeMetadataCacheOnLoad,
                &decoder);

            if (SUCCEEDED(result))
            {
                result = decoder->GetFrame(0, &frame);
            }
            if (SUCCEEDED(result))
            {
                result = g_wicFactory->CreateBitmapScaler(&scaler);
            }
            if (SUCCEEDED(result))
            {
                result = scaler->Initialize(frame, imageSize, imageSize, WICBitmapInterpolationModeFant);
            }
            if (SUCCEEDED(result))
            {
                result = g_wicFactory->CreateFormatConverter(&converter);
            }
            if (SUCCEEDED(result))
            {
                result = converter->Initialize(
                    scaler,
                    GUID_WICPixelFormat32bppBGRA,
                    WICBitmapDitherTypeNone,
                    nullptr,
                    0.0,
                    WICBitmapPaletteTypeCustom);
            }

            if (SUCCEEDED(result))
            {
                std::vector<std::uint8_t> bytes(static_cast<std::size_t>(imageSize) * imageSize * 4u);
                result = converter->CopyPixels(
                    nullptr,
                    imageSize * 4u,
                    static_cast<UINT>(bytes.size()),
                    bytes.data());

                if (SUCCEEDED(result))
                {
                    for (std::size_t index = 0; index < sourcePixels.size(); ++index)
                    {
                        const std::uint8_t blue = bytes[index * 4u + 0u];
                        const std::uint8_t green = bytes[index * 4u + 1u];
                        const std::uint8_t red = bytes[index * 4u + 2u];
                        const std::uint8_t alpha = bytes[index * 4u + 3u];
                        sourcePixels[index] = D3DCOLOR_ARGB(alpha, red, green, blue);
                    }
                    loadedImage = true;
                }
            }

            if (converter != nullptr) converter->Release();
            if (scaler != nullptr) scaler->Release();
            if (frame != nullptr) frame->Release();
            if (decoder != nullptr) decoder->Release();
        }

        std::vector<std::uint32_t> outputPixels(static_cast<std::size_t>(textureSize) * textureSize, 0u);
        for (UINT y = 0; y < textureSize; ++y)
        {
            for (UINT x = 0; x < textureSize; ++x)
            {
                const float dx = static_cast<float>(x) - center;
                const float dy = static_cast<float>(y) - center;
                const float distance = std::sqrt(dx * dx + dy * dy);
                const std::size_t outputIndex = static_cast<std::size_t>(y) * textureSize + x;

                if (distance <= outerRadius && distance > innerRadius)
                {
                    outputPixels[outputIndex] = ringColor;
                }
                else if (distance <= innerRadius)
                {
                    const int sourceX = std::clamp(static_cast<int>(x) - imageOffset, 0, static_cast<int>(imageSize) - 1);
                    const int sourceY = std::clamp(static_cast<int>(y) - imageOffset, 0, static_cast<int>(imageSize) - 1);
                    const std::size_t sourceIndex = static_cast<std::size_t>(sourceY) * imageSize + static_cast<std::size_t>(sourceX);
                    outputPixels[outputIndex] = sourcePixels[sourceIndex];
                }
            }
        }

        if (!loadedImage)
        {
            // The fallback remains a clean colored circle. The name beside it still identifies the speaker.
        }

        return CreateTextureFromPixels(device, textureSize, textureSize, outputPixels);
    }

    IDirect3DTexture9* CreateTextTexture(IDirect3DDevice9* device, const std::wstring& text)
    {
        HDC measurementDc = CreateCompatibleDC(nullptr);
        if (measurementDc == nullptr)
        {
            return nullptr;
        }

        HFONT font = CreateFontW(
            -17,
            0,
            0,
            0,
            FW_SEMIBOLD,
            FALSE,
            FALSE,
            FALSE,
            DEFAULT_CHARSET,
            OUT_DEFAULT_PRECIS,
            CLIP_DEFAULT_PRECIS,
            ANTIALIASED_QUALITY,
            DEFAULT_PITCH | FF_DONTCARE,
            L"Segoe UI");

        if (font == nullptr)
        {
            DeleteDC(measurementDc);
            return nullptr;
        }

        HGDIOBJ previousFont = SelectObject(measurementDc, font);
        SIZE measured{};
        GetTextExtentPoint32W(measurementDc, text.c_str(), static_cast<int>(text.size()), &measured);
        SelectObject(measurementDc, previousFont);
        DeleteDC(measurementDc);

        const int width = std::clamp(static_cast<int>(measured.cx) + 8, 24, 190);
        constexpr int height = 28;

        BITMAPINFO bitmapInfo{};
        bitmapInfo.bmiHeader.biSize = sizeof(BITMAPINFOHEADER);
        bitmapInfo.bmiHeader.biWidth = width;
        bitmapInfo.bmiHeader.biHeight = -height;
        bitmapInfo.bmiHeader.biPlanes = 1;
        bitmapInfo.bmiHeader.biBitCount = 32;
        bitmapInfo.bmiHeader.biCompression = BI_RGB;

        void* bits = nullptr;
        HBITMAP bitmap = CreateDIBSection(nullptr, &bitmapInfo, DIB_RGB_COLORS, &bits, nullptr, 0);
        HDC dc = CreateCompatibleDC(nullptr);
        if (bitmap == nullptr || dc == nullptr || bits == nullptr)
        {
            if (bitmap != nullptr) DeleteObject(bitmap);
            if (dc != nullptr) DeleteDC(dc);
            DeleteObject(font);
            return nullptr;
        }

        HGDIOBJ previousBitmap = SelectObject(dc, bitmap);
        previousFont = SelectObject(dc, font);
        memset(bits, 0, static_cast<std::size_t>(width) * height * 4u);
        SetBkMode(dc, TRANSPARENT);
        SetTextColor(dc, RGB(255, 255, 255));
        RECT rectangle{2, 0, width - 2, height};
        DrawTextW(dc, text.c_str(), -1, &rectangle, DT_LEFT | DT_VCENTER | DT_SINGLELINE | DT_NOPREFIX | DT_END_ELLIPSIS);
        GdiFlush();

        std::vector<std::uint32_t> pixels(static_cast<std::size_t>(width) * height, 0u);
        const auto* raw = static_cast<const std::uint8_t*>(bits);
        for (std::size_t index = 0; index < pixels.size(); ++index)
        {
            const std::uint8_t blue = raw[index * 4u + 0u];
            const std::uint8_t green = raw[index * 4u + 1u];
            const std::uint8_t red = raw[index * 4u + 2u];
            const std::uint8_t alpha = std::max({red, green, blue});
            pixels[index] = D3DCOLOR_ARGB(alpha, 242, 243, 245);
        }

        SelectObject(dc, previousFont);
        SelectObject(dc, previousBitmap);
        DeleteObject(font);
        DeleteObject(bitmap);
        DeleteDC(dc);

        return CreateTextureFromPixels(device, static_cast<UINT>(width), height, pixels);
    }

    IDirect3DTexture9* AvatarTexture(IDirect3DDevice9* device, const Speaker& speaker)
    {
        const std::wstring key = speaker.avatarPath.empty()
            ? L"fallback:" + speaker.name
            : speaker.avatarPath;

        const auto existing = g_avatarTextures.find(key);
        if (existing != g_avatarTextures.end())
        {
            return existing->second;
        }

        IDirect3DTexture9* texture = CreateAvatarTexture(device, speaker.name, speaker.avatarPath);
        g_avatarTextures.emplace(key, texture);
        return texture;
    }

    IDirect3DTexture9* NameTexture(IDirect3DDevice9* device, const std::wstring& name)
    {
        const auto existing = g_textTextures.find(name);
        if (existing != g_textTextures.end())
        {
            return existing->second;
        }

        IDirect3DTexture9* texture = CreateTextTexture(device, name);
        g_textTextures.emplace(name, texture);
        return texture;
    }

    void PrepareColorDrawing(IDirect3DDevice9* device)
    {
        device->SetTexture(0, nullptr);
        device->SetFVF(ColorVertexFormat);
    }

    void DrawRectangle(IDirect3DDevice9* device, float left, float top, float right, float bottom, D3DCOLOR color)
    {
        PrepareColorDrawing(device);
        const std::array<ColorVertex, 6> vertices = {
            ColorVertex{left,  top,    0.0f, 1.0f, color},
            ColorVertex{right, top,    0.0f, 1.0f, color},
            ColorVertex{right, bottom, 0.0f, 1.0f, color},
            ColorVertex{left,  top,    0.0f, 1.0f, color},
            ColorVertex{right, bottom, 0.0f, 1.0f, color},
            ColorVertex{left,  bottom, 0.0f, 1.0f, color}
        };

        device->DrawPrimitiveUP(D3DPT_TRIANGLELIST, 2, vertices.data(), sizeof(ColorVertex));
    }

    void DrawCircle(IDirect3DDevice9* device, float centerX, float centerY, float radius, D3DCOLOR color)
    {
        constexpr int segments = 24;
        std::array<ColorVertex, segments + 2> vertices{};
        vertices[0] = ColorVertex{centerX, centerY, 0.0f, 1.0f, color};

        for (int index = 0; index <= segments; ++index)
        {
            const float angle = static_cast<float>(index) / static_cast<float>(segments) * 6.28318530718f;
            vertices[static_cast<std::size_t>(index) + 1u] = ColorVertex{
                centerX + std::cos(angle) * radius,
                centerY + std::sin(angle) * radius,
                0.0f,
                1.0f,
                color};
        }

        PrepareColorDrawing(device);
        device->DrawPrimitiveUP(D3DPT_TRIANGLEFAN, segments, vertices.data(), sizeof(ColorVertex));
    }

    void DrawRoundedRectangle(
        IDirect3DDevice9* device,
        float left,
        float top,
        float right,
        float bottom,
        float radius,
        D3DCOLOR color)
    {
        DrawRectangle(device, left + radius, top, right - radius, bottom, color);
        DrawRectangle(device, left, top + radius, right, bottom - radius, color);
        DrawCircle(device, left + radius, top + radius, radius, color);
        DrawCircle(device, right - radius, top + radius, radius, color);
        DrawCircle(device, left + radius, bottom - radius, radius, color);
        DrawCircle(device, right - radius, bottom - radius, radius, color);
    }

    void DrawTexture(
        IDirect3DDevice9* device,
        IDirect3DTexture9* texture,
        float left,
        float top,
        float width,
        float height,
        std::uint8_t opacity = 255)
    {
        if (texture == nullptr)
        {
            return;
        }

        const D3DCOLOR tint = D3DCOLOR_ARGB(opacity, 255, 255, 255);
        const std::array<TextureVertex, 4> vertices = {
            TextureVertex{left,         top,          0.0f, 1.0f, tint, 0.0f, 0.0f},
            TextureVertex{left + width, top,          0.0f, 1.0f, tint, 1.0f, 0.0f},
            TextureVertex{left,         top + height, 0.0f, 1.0f, tint, 0.0f, 1.0f},
            TextureVertex{left + width, top + height, 0.0f, 1.0f, tint, 1.0f, 1.0f}
        };

        device->SetTexture(0, texture);
        device->SetFVF(TextureVertexFormat);
        device->SetTextureStageState(0, D3DTSS_COLOROP, D3DTOP_MODULATE);
        device->SetTextureStageState(0, D3DTSS_COLORARG1, D3DTA_TEXTURE);
        device->SetTextureStageState(0, D3DTSS_COLORARG2, D3DTA_DIFFUSE);
        device->SetTextureStageState(0, D3DTSS_ALPHAOP, D3DTOP_MODULATE);
        device->SetTextureStageState(0, D3DTSS_ALPHAARG1, D3DTA_TEXTURE);
        device->SetTextureStageState(0, D3DTSS_ALPHAARG2, D3DTA_DIFFUSE);
        device->SetSamplerState(0, D3DSAMP_MINFILTER, D3DTEXF_LINEAR);
        device->SetSamplerState(0, D3DSAMP_MAGFILTER, D3DTEXF_LINEAR);
        device->SetSamplerState(0, D3DSAMP_MIPFILTER, D3DTEXF_NONE);
        device->DrawPrimitiveUP(D3DPT_TRIANGLESTRIP, 2, vertices.data(), sizeof(TextureVertex));
    }

    void DrawOverlay(IDirect3DDevice9* device)
    {
        if (device == nullptr)
        {
            return;
        }

        RefreshSpeakers();
        if (g_speakers.empty())
        {
            return;
        }

        D3DVIEWPORT9 viewport{};
        if (FAILED(device->GetViewport(&viewport)))
        {
            return;
        }

        EnsureDevice(device);

        IDirect3DStateBlock9* stateBlock = nullptr;
        if (SUCCEEDED(device->CreateStateBlock(D3DSBT_ALL, &stateBlock)) && stateBlock != nullptr)
        {
            stateBlock->Capture();
        }

        device->SetPixelShader(nullptr);
        device->SetVertexShader(nullptr);
        device->SetRenderState(D3DRS_ZENABLE, FALSE);
        device->SetRenderState(D3DRS_ZWRITEENABLE, FALSE);
        device->SetRenderState(D3DRS_CULLMODE, D3DCULL_NONE);
        device->SetRenderState(D3DRS_ALPHABLENDENABLE, TRUE);
        device->SetRenderState(D3DRS_SRCBLEND, D3DBLEND_SRCALPHA);
        device->SetRenderState(D3DRS_DESTBLEND, D3DBLEND_INVSRCALPHA);
        device->SetRenderState(D3DRS_LIGHTING, FALSE);
        device->SetRenderState(D3DRS_SCISSORTESTENABLE, FALSE);

        constexpr float cardWidth = 218.0f;
        constexpr float cardHeight = 44.0f;
        constexpr float cardGap = 6.0f;
        constexpr float rightMargin = 18.0f;
        constexpr float topMargin = 34.0f;
        constexpr float avatarSize = 36.0f;
        const D3DCOLOR shadow = D3DCOLOR_ARGB(70, 0, 0, 0);
        const D3DCOLOR background = D3DCOLOR_ARGB(178, 25, 27, 32);
        const D3DCOLOR edge = D3DCOLOR_ARGB(100, 77, 80, 88);

        const float cardX = static_cast<float>(viewport.X + viewport.Width) - cardWidth - rightMargin;
        float cardY = static_cast<float>(viewport.Y) + topMargin;

        for (const Speaker& speaker : g_speakers)
        {
            DrawRoundedRectangle(device, cardX + 2.0f, cardY + 3.0f, cardX + cardWidth + 2.0f, cardY + cardHeight + 3.0f, 8.0f, shadow);
            DrawRoundedRectangle(device, cardX, cardY, cardX + cardWidth, cardY + cardHeight, 8.0f, edge);
            DrawRoundedRectangle(device, cardX + 1.0f, cardY + 1.0f, cardX + cardWidth - 1.0f, cardY + cardHeight - 1.0f, 7.0f, background);

            IDirect3DTexture9* avatar = AvatarTexture(device, speaker);
            DrawTexture(device, avatar, cardX + 5.0f, cardY + 4.0f, avatarSize, avatarSize);

            IDirect3DTexture9* name = NameTexture(device, speaker.name);
            if (name != nullptr)
            {
                D3DSURFACE_DESC description{};
                if (SUCCEEDED(name->GetLevelDesc(0, &description)))
                {
                    const float textWidth = std::min(static_cast<float>(description.Width), cardWidth - 53.0f);
                    DrawTexture(device, name, cardX + 48.0f, cardY + 8.0f, textWidth, static_cast<float>(description.Height));
                }
            }

            cardY += cardHeight + cardGap;
        }

        device->SetTexture(0, nullptr);
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

        WriteNativeLog(L"DirectX 9 compact speaker overlay installed.");
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
        ReleaseTextures();
        if (g_wicFactory != nullptr)
        {
            g_wicFactory->Release();
            g_wicFactory = nullptr;
        }
        WriteNativeLog(L"DirectX 9 compact speaker overlay removed.");
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

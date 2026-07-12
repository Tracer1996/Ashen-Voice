#include <windows.h>

#include <filesystem>
#include <iostream>
#include <string>
#include <vector>

namespace
{
    void PrintUsage()
    {
        std::wcerr << L"Usage: AshenVoiceInjector.exe --pid <process id> --dll <full dll path>\n";
    }

    bool ParseArguments(int argc, wchar_t** argv, DWORD& pid, std::filesystem::path& dllPath)
    {
        for (int i = 1; i < argc; ++i)
        {
            const std::wstring argument = argv[i];
            if (argument == L"--pid" && i + 1 < argc)
            {
                try
                {
                    pid = static_cast<DWORD>(std::stoul(argv[++i]));
                }
                catch (...)
                {
                    return false;
                }
            }
            else if (argument == L"--dll" && i + 1 < argc)
            {
                dllPath = argv[++i];
            }
        }

        return pid != 0 && !dllPath.empty();
    }

    bool IsTargetCompatible(HANDLE process)
    {
        BOOL selfWow64 = FALSE;
        BOOL targetWow64 = FALSE;

        if (!IsWow64Process(GetCurrentProcess(), &selfWow64) || !IsWow64Process(process, &targetWow64))
        {
            return true;
        }

        // A 32-bit injector running under WOW64 cannot inject its 32-bit DLL into a native 64-bit target.
        return !selfWow64 || targetWow64;
    }
}

int wmain(int argc, wchar_t** argv)
{
    DWORD pid = 0;
    std::filesystem::path dllPath;

    if (!ParseArguments(argc, argv, pid, dllPath))
    {
        PrintUsage();
        return 2;
    }

    std::error_code pathError;
    dllPath = std::filesystem::absolute(dllPath, pathError);
    if (pathError || !std::filesystem::exists(dllPath))
    {
        std::wcerr << L"Overlay DLL was not found: " << dllPath.wstring() << L"\n";
        return 3;
    }

    const std::wstring dllPathString = dllPath.wstring();
    const SIZE_T allocationSize = (dllPathString.size() + 1) * sizeof(wchar_t);

    HANDLE process = OpenProcess(
        PROCESS_CREATE_THREAD |
        PROCESS_QUERY_INFORMATION |
        PROCESS_VM_OPERATION |
        PROCESS_VM_WRITE |
        PROCESS_VM_READ,
        FALSE,
        pid);

    if (process == nullptr)
    {
        std::wcerr << L"Could not open the WoW process. Windows error: " << GetLastError() << L"\n";
        std::wcerr << L"Make sure Ashen Voice and WoW are running at the same permission level.\n";
        return 4;
    }

    if (!IsTargetCompatible(process))
    {
        std::wcerr << L"The selected process is 64-bit. Ashen Voice supports the 32-bit classic WoW client.\n";
        CloseHandle(process);
        return 5;
    }

    void* remoteMemory = VirtualAllocEx(
        process,
        nullptr,
        allocationSize,
        MEM_COMMIT | MEM_RESERVE,
        PAGE_READWRITE);

    if (remoteMemory == nullptr)
    {
        std::wcerr << L"Could not allocate memory in the WoW process. Windows error: " << GetLastError() << L"\n";
        CloseHandle(process);
        return 6;
    }

    SIZE_T bytesWritten = 0;
    if (!WriteProcessMemory(
            process,
            remoteMemory,
            dllPathString.c_str(),
            allocationSize,
            &bytesWritten) ||
        bytesWritten != allocationSize)
    {
        std::wcerr << L"Could not write the overlay path into the WoW process. Windows error: " << GetLastError() << L"\n";
        VirtualFreeEx(process, remoteMemory, 0, MEM_RELEASE);
        CloseHandle(process);
        return 7;
    }

    HMODULE kernel32 = GetModuleHandleW(L"kernel32.dll");
    if (kernel32 == nullptr)
    {
        std::wcerr << L"Could not locate kernel32.dll.\n";
        VirtualFreeEx(process, remoteMemory, 0, MEM_RELEASE);
        CloseHandle(process);
        return 8;
    }

    const auto loadLibraryAddress = reinterpret_cast<LPTHREAD_START_ROUTINE>(
        GetProcAddress(kernel32, "LoadLibraryW"));

    if (loadLibraryAddress == nullptr)
    {
        std::wcerr << L"Could not locate LoadLibraryW.\n";
        VirtualFreeEx(process, remoteMemory, 0, MEM_RELEASE);
        CloseHandle(process);
        return 9;
    }

    HANDLE remoteThread = CreateRemoteThread(
        process,
        nullptr,
        0,
        loadLibraryAddress,
        remoteMemory,
        0,
        nullptr);

    if (remoteThread == nullptr)
    {
        std::wcerr << L"Could not start the overlay inside WoW. Windows error: " << GetLastError() << L"\n";
        VirtualFreeEx(process, remoteMemory, 0, MEM_RELEASE);
        CloseHandle(process);
        return 10;
    }

    const DWORD waitResult = WaitForSingleObject(remoteThread, 15000);
    if (waitResult != WAIT_OBJECT_0)
    {
        std::wcerr << L"Timed out while loading the overlay DLL.\n";
        CloseHandle(remoteThread);
        VirtualFreeEx(process, remoteMemory, 0, MEM_RELEASE);
        CloseHandle(process);
        return 11;
    }

    DWORD remoteModule = 0;
    if (!GetExitCodeThread(remoteThread, &remoteModule) || remoteModule == 0)
    {
        std::wcerr << L"WoW rejected the overlay DLL. Windows error: " << GetLastError() << L"\n";
        CloseHandle(remoteThread);
        VirtualFreeEx(process, remoteMemory, 0, MEM_RELEASE);
        CloseHandle(process);
        return 12;
    }

    CloseHandle(remoteThread);
    VirtualFreeEx(process, remoteMemory, 0, MEM_RELEASE);
    CloseHandle(process);

    std::wcout << L"Ashen Voice overlay DLL loaded into process " << pid << L".\n";
    return 0;
}

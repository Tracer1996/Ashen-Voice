#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <tlhelp32.h>
#include <filesystem>
#include <iostream>
#include <string>
#include <vector>

static DWORD FindProcess(const std::vector<std::wstring>& names) {
  PROCESSENTRY32W pe{sizeof(pe)};
  HANDLE snap = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS,0);
  if (snap == INVALID_HANDLE_VALUE) return 0;
  DWORD pid = 0;
  if (Process32FirstW(snap,&pe)) do {
    for (const auto& n : names) if (_wcsicmp(pe.szExeFile,n.c_str())==0) { pid=pe.th32ProcessID; break; }
  } while (!pid && Process32NextW(snap,&pe));
  CloseHandle(snap); return pid;
}

int wmain(int argc, wchar_t** argv) {
  std::filesystem::path dll = argc > 1 ? argv[1] : std::filesystem::path(argv[0]).parent_path() / L"AshenVoice.dll";
  dll = std::filesystem::absolute(dll);
  if (!std::filesystem::exists(dll)) { std::wcerr << L"Missing DLL: " << dll << L"\n"; return 2; }
  std::wcout << L"Waiting for WoW.exe / OctoWoW.exe...\n";
  DWORD pid = 0; while (!(pid=FindProcess({L"WoW.exe",L"OctoWoW.exe"}))) Sleep(500);
  HANDLE p = OpenProcess(PROCESS_CREATE_THREAD|PROCESS_QUERY_INFORMATION|PROCESS_VM_OPERATION|PROCESS_VM_WRITE|PROCESS_VM_READ,FALSE,pid);
  if (!p) { std::wcerr << L"OpenProcess failed. Try Run as administrator. Error " << GetLastError() << L"\n"; return 3; }
  const std::wstring path = dll.wstring(); size_t bytes=(path.size()+1)*sizeof(wchar_t);
  void* remote=VirtualAllocEx(p,nullptr,bytes,MEM_COMMIT|MEM_RESERVE,PAGE_READWRITE);
  if (!remote || !WriteProcessMemory(p,remote,path.c_str(),bytes,nullptr)) { std::wcerr << L"Could not copy DLL path.\n"; CloseHandle(p); return 4; }
  auto load=(LPTHREAD_START_ROUTINE)GetProcAddress(GetModuleHandleW(L"kernel32.dll"),"LoadLibraryW");
  HANDLE t=CreateRemoteThread(p,nullptr,0,load,remote,0,nullptr);
  if (!t) { std::wcerr << L"CreateRemoteThread failed. Error " << GetLastError() << L"\n"; VirtualFreeEx(p,remote,0,MEM_RELEASE); CloseHandle(p); return 5; }
  WaitForSingleObject(t,10000); DWORD result=0; GetExitCodeThread(t,&result);
  CloseHandle(t); VirtualFreeEx(p,remote,0,MEM_RELEASE); CloseHandle(p);
  if (!result) { std::wcerr << L"DLL load failed. Confirm both injector and DLL are Win32/x86.\n"; return 6; }
  std::wcout << L"Overlay injected successfully. You can close this window.\n";
  return 0;
}

@echo off
setlocal
where cmake >nul 2>nul || (echo Developer build dependency missing: CMake.& pause & exit /b 1)
where npm >nul 2>nul || (echo Developer build dependency missing: Node.js.& pause & exit /b 1)
if not exist release mkdir release
cmake -S native -B native\build -A Win32 || goto :error
cmake --build native\build --config Release || goto :error
copy /Y native\build\Release\AshenVoice.dll release\AshenVoice.dll >nul
copy /Y native\build\Release\AshenVoiceInjector.exe release\AshenVoiceInjector.exe >nul
call npm install || goto :error
call npm run dist:win || goto :error
echo.
echo Installer created in the dist folder.
pause
exit /b 0
:error
echo.
echo Build failed.
pause
exit /b 1

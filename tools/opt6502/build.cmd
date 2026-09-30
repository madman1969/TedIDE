@echo off
rem Builds opt6502.exe (Tedide's patched fork - see TEDIDE.md) with the Visual Studio C++ tools.
rem Output: bin\opt6502.exe. Needs Visual Studio 2022+ with "Desktop development with C++".
setlocal
set "HERE=%~dp0"
set "VSWHERE=%ProgramFiles(x86)%\Microsoft Visual Studio\Installer\vswhere.exe"
if not exist "%VSWHERE%" (echo vswhere.exe not found - install Visual Studio with the C++ tools. & exit /b 1)
for /f "usebackq tokens=*" %%i in (`"%VSWHERE%" -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath`) do set "VSDIR=%%i"
if not defined VSDIR (echo No Visual Studio install with the C++ x64 tools found. & exit /b 1)
call "%VSDIR%\VC\Auxiliary\Build\vcvars64.bat" >nul || exit /b 1
cd /d "%HERE%"
if not exist bin mkdir bin
if not exist obj mkdir obj
rem winshim\strings.h maps POSIX strcasecmp/strncasecmp onto the MSVC CRT.
cl /nologo /O2 /W3 /std:c11 /D_CRT_SECURE_NO_WARNINGS /D_CRT_NONSTDC_NO_WARNINGS /Iwinshim /Foobj\ /Febin\opt6502.exe ^
  src\main.c src\types.c src\ast\ast.c src\ast\parser.c src\analysis\analysis.c src\analysis\registers.c ^
  src\analysis\nodeinfo.c src\optimizations\optimizer.c src\optimizations\peephole.c src\optimizations\deadcode.c ^
  src\optimizations\jumps.c src\optimizations\loadstore.c src\optimizations\regusage.c src\optimizations\constant.c ^
  src\optimizations\cpu65c02.c src\optimizations\cpu45gs02.c src\optimizations\inline.c src\optimizations\inline_runtime.c src\output\output.c ^
  src\program\program.c
exit /b %ERRORLEVEL%

@echo off
setlocal

REM ============================================================================
REM  VideoDirector - build a portable, self-contained x64 release.
REM
REM  Works from a fresh clone: every setting is passed explicitly, so this does
REM  not depend on a .pubxml (the publish profiles are gitignored and local to
REM  the author's machine).
REM
REM  Usage:
REM    publish.bat                     -> publishes to .\bin\Release\
REM    publish.bat "D:\somewhere"      -> publishes to that folder
REM    publish.bat "D:\somewhere" nosmoke
REM
REM  Two builds, two folders, nothing anywhere else:
REM
REM      bin\Debug\     dotnet build   - the test build
REM      bin\Release\   this script    - the shippable build
REM
REM  These match where `dotnet build` actually puts things: the csproj sets
REM  AppendTargetFrameworkToOutputPath and AppendRuntimeIdentifierToOutputPath
REM  to false, so there is no TFM or RID subfolder and no bin\x64 at all.
REM
REM  Release IS the publish output: dotnet publish has to build before it copies,
REM  and that build lands in bin\Release anyway.
REM
REM  Deliberately NO -p:Platform=x64: the architecture comes from the RID (-r win-x64),
REM  and setting the MSBuild Platform property only moves the BUILD INTERMEDIATES into
REM  bin\x64\Release - a duplicate tree beside the real output that nothing consumes.
REM
REM  Deliberately NOT single-file: single-file self-extracts the whole runtime
REM  to %TEMP% on the first launch after every publish, which measurably slows
REM  cold start.
REM ============================================================================

if exist "%~dp0bin\Debug" rd /s /q "%~dp0bin\Debug"
set "OUTDIR=%~1"
if "%OUTDIR%"=="" set "OUTDIR=%~dp0bin\Release"
if /i "%OUTDIR%"=="nosmoke" set "OUTDIR=%~dp0bin\Release"

set "PROJ=%~dp0VideoDirector.csproj"

echo ============================================================
echo  Publishing VideoDirector  (x64, self-contained, loose)
echo  Output: %OUTDIR%
echo ============================================================
echo.

REM The trailing backslash on PublishDir is DOUBLED deliberately. MSBuild wants a trailing
REM separator, but \" escapes the closing quote, which swallowed every argument after it
REM into the directory name ("...publish   -p:PublishProfile=   --nologo").
dotnet publish "%PROJ%" ^
  -c Release ^
  -r win-x64 ^
  -p:SelfContained=true ^
  -p:PublishSingleFile=false ^
  -p:PublishTrimmed=false ^
  -p:PublishReadyToRun=true ^
  -p:PublishDir="%OUTDIR%\\" ^
  -p:PublishProfile= ^
  --nologo

if errorlevel 1 goto :failed
if not exist "%OUTDIR%\VideoDirector.exe" goto :failed

echo.
echo Publish reported success. Copying documentation...
copy /Y "%~dp0LICENSE" "%OUTDIR%\" >nul
copy /Y "%~dp0README.md" "%OUTDIR%\" >nul

echo Verifying output...

REM A part-installed .NET SDK can produce a build that compiles cleanly but
REM crashes at startup, with framework assemblies published stripped.
REM System.Private.Xml.dll is a reliable canary: ~7.6 MB healthy, ~3 MB bad.
for %%F in ("%OUTDIR%\System.Private.Xml.dll") do set "XMLSIZE=%%~zF"
if not defined XMLSIZE goto :suspect
if %XMLSIZE% LSS 5000000 goto :suspect
echo   [ok] Framework assemblies look intact.

if /i "%~1"=="nosmoke" goto :done
if /i "%~2"=="nosmoke" goto :done

tasklist /fi "imagename eq VideoDirector.exe" 2>nul | find /i "VideoDirector.exe" >nul
if not errorlevel 1 (
  echo   [skip] VideoDirector already running - smoke test skipped.
  goto :done
)

echo   Launching to confirm it starts...
start "" "%OUTDIR%\VideoDirector.exe"
REM ping, not "timeout" - timeout fails if stdin is redirected (publish.bat > log.txt)
ping -n 7 127.0.0.1 >nul 2>&1
tasklist /fi "imagename eq VideoDirector.exe" 2>nul | find /i "VideoDirector.exe" >nul
if errorlevel 1 goto :crashed
taskkill /im VideoDirector.exe /f >nul 2>&1
echo   [ok] App started and stayed running.

:done
echo.
echo ============================================================
echo  PUBLISH COMPLETE
echo  %OUTDIR%
echo ============================================================
exit /b 0

:suspect
echo.
echo ============================================================
echo  WARNING - output looks wrong
echo.
echo  System.Private.Xml.dll is smaller than expected, meaning the
echo  framework assemblies were published stripped. This build will
echo  most likely crash on startup.
echo.
echo  Usual cause: a .NET SDK or Visual Studio update part-way
echo  through installing. Check this returns the same answer twice,
echo  then publish again:
echo.
echo      dotnet --list-sdks
echo ============================================================
exit /b 2

:crashed
echo.
echo ============================================================
echo  SMOKE TEST FAILED - the app exited immediately.
echo  The published output is not usable. See the note above about
echo  checking "dotnet --list-sdks" for an in-progress SDK update.
echo ============================================================
exit /b 3

:failed
echo.
echo ============================================================
echo  PUBLISH FAILED - see errors above.
echo ============================================================
exit /b 1


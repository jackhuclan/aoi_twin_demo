@echo off
rem ============================================================================
rem  AoiTwin TA.18 field soak runner (render loop + VM, 72h default)
rem
rem  Usage:
rem    soak-visual.cmd                 -> 72h, report docs\_soak-visual-72h.txt
rem    soak-visual.cmd 60              -> 60 minutes (smoke)
rem    soak-visual.cmd 4320 D:\soak\s1.txt
rem
rem  Outputs next to the report:
rem    <report>            final key=value report (result=PASS/FAIL)
rem    <report>.progress   per-sample heartbeat (crash evidence: last line = time of death)
rem    <report>.csv        curves for Excel/pandas (workingSetMb, fps, points)
rem
rem  Exit code: 0 pass / 1 criteria failed / 2 bad args / 3 environment or asset error
rem ============================================================================
setlocal
set "ROOT=%~dp0.."
set "EXE=%ROOT%\tools\AoiTwin.Soak\bin\Release\net10.0-windows\AoiTwin.Soak.exe"

set "MIN=%~1"
if "%MIN%"=="" set "MIN=4320"

set "REPORT=%~2"
if "%REPORT%"=="" set "REPORT=%ROOT%\docs\_soak-visual-72h.txt"

if not exist "%EXE%" (
    echo [soak-visual] exe not found: %EXE%
    echo [soak-visual] build first: dotnet build AoiTwin.slnx -c Release
    exit /b 3
)

echo [soak-visual] minutes=%MIN% report=%REPORT%
echo [soak-visual] keep this console open; the render window is parked off-screen.
echo [soak-visual] watch: .progress heartbeat grows every 30s.

"%EXE%" soak-visual %MIN% "%REPORT%" --sample-sec 30 --warmup-min 30
set "CODE=%ERRORLEVEL%"
echo [soak-visual] exit=%CODE%
exit /b %CODE%

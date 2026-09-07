@echo off
rem ---------------------------------------------------------------------------
rem Open Emacs with the throwaway cslite config and a sample C# file.
rem Your real .emacs.d is not loaded.
rem
rem   try.cmd                  opens the sandbox project
rem   try.cmd path\to\File.cs  opens that file instead
rem ---------------------------------------------------------------------------
setlocal

set "REPO=%~dp0"
set "CONFIG=%REPO%emacs\test-config"

if not exist "%REPO%dist\cslite.exe" (
  echo The server is not built yet. Run this first:
  echo     dotnet publish -c Release -o dist
  exit /b 1
)

rem Prefer Emacs on PATH; otherwise take the newest install under Program Files.
set "EMACS="
for /f "delims=" %%E in ('where runemacs 2^>nul') do set "EMACS=%%E"
if not defined EMACS for /f "delims=" %%E in ('where emacs 2^>nul') do set "EMACS=%%E"
if not defined EMACS (
  for /f "delims=" %%D in ('dir /b /ad /o-n "%ProgramFiles%\Emacs\emacs-*" 2^>nul') do (
    if not defined EMACS set "EMACS=%ProgramFiles%\Emacs\%%D\bin\runemacs.exe"
  )
)
if not defined EMACS (
  echo Could not find Emacs. Set EMACS to its path and re-run.
  exit /b 1
)

set "TARGET=%~1"
if "%TARGET%"=="" set "TARGET=%USERPROFILE%\OneDrive\Desktop\cslite-sandbox\Program.cs"

if not exist "%TARGET%" (
  echo No such file: %TARGET%
  echo Pass a .cs file, or create the sandbox with: dotnet new console -o "%USERPROFILE%\OneDrive\Desktop\cslite-sandbox"
  exit /b 1
)

echo Emacs:  %EMACS%
echo Config: %CONFIG%
echo File:   %TARGET%
start "" "%EMACS%" --init-directory="%CONFIG%" "%TARGET%"

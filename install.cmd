@echo off
rem ---------------------------------------------------------------------------
rem Publish the server and copy it, plus cslite.el, into your Emacs config so
rem the config is self-contained and the same init.el works on every machine.
rem
rem   install.cmd                 installs into %APPDATA%\.emacs.d
rem   install.cmd D:\my\.emacs.d  installs somewhere else
rem
rem The server locks its own DLL while running, so stop it in Emacs first
rem (C-c l s, or M-x eglot-shutdown).
rem ---------------------------------------------------------------------------
setlocal

set "REPO=%~dp0"
set "EMACSD=%~1"
if "%EMACSD%"=="" set "EMACSD=%APPDATA%\.emacs.d"

if not exist "%EMACSD%" (
  echo No Emacs configuration at %EMACSD%
  exit /b 1
)

echo Publishing...
pushd "%REPO%"
dotnet publish -c Release -o dist --nologo -v q
if errorlevel 1 (
  echo.
  echo Publish failed. If it says the file is locked, stop the server in Emacs
  echo first with C-c l s, then run this again.
  popd
  exit /b 1
)
popd

echo Copying the server to %EMACSD%\cslite ...
if not exist "%EMACSD%\cslite" mkdir "%EMACSD%\cslite"
xcopy /E /I /Y /Q "%REPO%dist\*" "%EMACSD%\cslite\" >nul
if errorlevel 1 exit /b 1

echo Copying cslite.el to %EMACSD%\lisp ...
if not exist "%EMACSD%\lisp" mkdir "%EMACSD%\lisp"
copy /Y "%REPO%emacs\cslite.el" "%EMACSD%\lisp\" >nul

echo.
echo Done. Restart Emacs, or M-x cslite-restart in a C# buffer.

@echo off
rem Startet xpserver, nach "xpctl update" (Exit 42) oder Absturz startet er neu (max. 3 Abstuerze am Stueck).
rem Nach einem Absturz wird xpserver.py.bak zurueckgespielt, falls vorhanden.
cd /d "%~dp0"
rem Python-Interpreter: Umgebungsvariable PYTHON, sonst python aus dem PATH (Python 3.4).
if not defined PYTHON set PYTHON=python
title xpserver
set XPCTL_SUPERVISED=1
rem Liegt xpctl.key neben der .bat, startet der Server ohne Passwortabfrage.
set KF=
if exist xpctl.key set KF=--key-file xpctl.key
if not defined KF set /p XPCTL_KEY=Passwort: 
cls
set n=0
:loop
%PYTHON% xpserver.py %KF%
set rc=%errorlevel%
if %rc%==0 goto ende
if %rc%==42 goto neustart
echo Absturz, Exit-Code %rc%
if exist xpserver.py.bak echo Rollback auf xpserver.py.bak
if exist xpserver.py.bak copy /y xpserver.py.bak xpserver.py
if exist xpserver.py.bak del xpserver.py.bak
set /a n=n+1
if %n% gtr 3 goto ende
ping -n 3 127.0.0.1 >nul
goto loop
:neustart
set n=0
goto loop
:ende
pause

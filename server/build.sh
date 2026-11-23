#!/bin/sh
# Baut xpserver.exe (.NET 3.5, läuft auf Windows XP) und legt alles für den XP-Rechner nach server/dist/.
# Braucht nur das dotnet SDK (6 oder neuer), läuft auf macOS, Linux und Windows (Git Bash).
set -e
cd "$(dirname "$0")"
dotnet build -c Release -nologo -v q
rm -rf dist
mkdir -p dist
cp bin/Release/net35/xpserver.exe dist/
cp xpserver.example.json dist/
perl -pe "s/\n/\r\n/" LIESMICH.txt | iconv -f UTF-8 -t WINDOWS-1252 > dist/LIESMICH.txt   # XP-Notepad: ANSI + CRLF
echo
echo "fertig: $(pwd)/dist"
ls -l dist

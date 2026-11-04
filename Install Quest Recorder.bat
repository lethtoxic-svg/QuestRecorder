@echo off
rem Quest Recorder - made by LethToxic.
rem Put this file in your BuddyCron folder (next to BuddyCron.exe) and double-click it.
rem It downloads the newest Quest Recorder from GitHub and puts it in Plugins\QuestRecorder.
rem Your old copy is saved in PluginBackups first. Recordings and profiles are not touched.
cd /d "%~dp0"
if not exist "Plugins" (
  echo This file has to be in your BuddyCron folder - the one with the Plugins folder in it.
  echo.
  pause
  exit /b 1
)
echo Close BuddyCron before you continue.
pause
powershell -NoProfile -ExecutionPolicy Bypass -Command ^
  "$ErrorActionPreference='Stop';" ^
  "$src='https://raw.githubusercontent.com/lethtoxic-svg/QuestRecorder/main/';" ^
  "[Net.ServicePointManager]::SecurityProtocol=[Net.SecurityProtocolType]::Tls12;" ^
  "$lines=(Invoke-WebRequest -UseBasicParsing ($src+'patch.txt')).Content -split \"`n\";" ^
  "$ver=$lines[0].Trim();" ^
  "Write-Host ('Downloading Quest Recorder '+$ver+' ...');" ^
  "$tmp=Join-Path $env:TEMP 'QuestRecorderInstall'; if(Test-Path $tmp){Remove-Item $tmp -Recurse -Force}; New-Item -ItemType Directory $tmp | Out-Null;" ^
  "Invoke-WebRequest -UseBasicParsing 'https://github.com/lethtoxic-svg/QuestRecorder/archive/refs/heads/main.zip' -OutFile (Join-Path $tmp 'qr.zip');" ^
  "Expand-Archive (Join-Path $tmp 'qr.zip') (Join-Path $tmp 'files');" ^
  "$new=Get-ChildItem (Join-Path $tmp 'files') -Directory | Where-Object { Test-Path (Join-Path $_.FullName 'QuestRecorder.cs') } | Select-Object -First 1;" ^
  "if(-not $new){ throw 'The download does not look like Quest Recorder.' }" ^
  "$dest='Plugins\QuestRecorder';" ^
  "if(Test-Path $dest){ $bk='PluginBackups\QuestRecorder before '+$ver+' '+(Get-Date -Format 'yyyy-MM-dd HH-mm'); New-Item -ItemType Directory -Force $bk | Out-Null; Copy-Item (Join-Path $dest '*') $bk -Recurse -Force; Remove-Item $dest -Recurse -Force; Write-Host ('Old copy saved in '+$bk) }" ^
  "New-Item -ItemType Directory -Force $dest | Out-Null;" ^
  "Get-ChildItem $new.FullName -File | Where-Object { $_.Name -notmatch '^(patch|latest)\.txt$' -and $_.Extension -notin '.zip','.bat' -and -not $_.Name.StartsWith('.') } | Copy-Item -Destination $dest -Force;" ^
  "Write-Host ''; Write-Host ('Quest Recorder '+$ver+' is installed. Start BuddyCron and tick Quest Recorder.')"
if errorlevel 1 (
  echo.
  echo Something went wrong - copy the text above and send it over.
)
echo.
pause

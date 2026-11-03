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
  "$lines=(Invoke-WebRequest -UseBasicParsing ($src+'latest.txt')).Content -split \"`n\";" ^
  "$ver=$lines[0].Trim(); $zip=$lines[1].Trim();" ^
  "if($zip -notmatch '^[\w.\-]+\.zip$'){ throw 'The update file name looks wrong.' }" ^
  "Write-Host ('Downloading Quest Recorder '+$ver+' ...');" ^
  "$tmp=Join-Path $env:TEMP 'QuestRecorderInstall'; if(Test-Path $tmp){Remove-Item $tmp -Recurse -Force}; New-Item -ItemType Directory $tmp | Out-Null;" ^
  "Invoke-WebRequest -UseBasicParsing ($src+$zip) -OutFile (Join-Path $tmp 'qr.zip');" ^
  "Expand-Archive (Join-Path $tmp 'qr.zip') (Join-Path $tmp 'files');" ^
  "$new=Join-Path $tmp 'files\QuestRecorder'; if(-not (Test-Path (Join-Path $new 'QuestRecorder.cs'))){ throw 'The download does not look like Quest Recorder.' }" ^
  "$dest='Plugins\QuestRecorder';" ^
  "if(Test-Path $dest){ $bk='PluginBackups\QuestRecorder before '+$ver+' '+(Get-Date -Format 'yyyy-MM-dd HH-mm'); New-Item -ItemType Directory -Force $bk | Out-Null; Copy-Item (Join-Path $dest '*') $bk -Recurse -Force; Remove-Item $dest -Recurse -Force; Write-Host ('Old copy saved in '+$bk) }" ^
  "Copy-Item $new 'Plugins' -Recurse -Force;" ^
  "Write-Host ''; Write-Host ('Quest Recorder '+$ver+' is installed. Start BuddyCron and tick Quest Recorder.')"
if errorlevel 1 (
  echo.
  echo Something went wrong - copy the text above and send it over.
)
echo.
pause

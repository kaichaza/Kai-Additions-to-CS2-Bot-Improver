dotnet build -c Release

set DEST=D:\SteamLibrary\steamapps\common\Counter-Strike Global Offensive\game\csgo\addons\counterstrikesharp\plugins\KaiBotTactics

copy /Y bin\Release\net10.0\KaiBotTactics.dll       "%DEST%"
copy /Y bin\Release\net10.0\KaiBotTactics.deps.json "%DEST%"


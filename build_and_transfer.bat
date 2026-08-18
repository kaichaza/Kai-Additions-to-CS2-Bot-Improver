
dotnet build -c Release

set DEST=D:\SteamLibrary\steamapps\common\Counter-Strike Global Offensive\game\csgo\addons\counterstrikesharp\plugins\KaiBotTactics

copy /Y bin\Release\net10.0\KaiBotTactics.dll       "%DEST%"
copy /Y bin\Release\net10.0\KaiBotTactics.deps.json "%DEST%"











kai_plays          the playbook and win record — empty until the transition
kai_plays list     every play with won/called and abandoned counts
kai_routes         the route book, spawns learned, live rotation state
kai_routes list    every generated route with waypoint count and coverage
kai_solve status   solved post counts per bombsite
kai_crumbs         node/edge counts, saturated, usable
kai_crumbs coverage    the detailed graph breakdown
kai_learn status   sample counts by category
kai_list           the generated spots



mp_maxrounds 200;
mp_winlimit 100;

bot_defer_to_human_items 0;

can you double check the assign defuser logic please, i keep seeing every once in a while CTs all crowding the bomb to defuse it, as if their role wasn't assigned properly, and i observed a defuser come off the bomb instead of sticking it even though they were surrounder by friends.
I also observed a CT bot retake the site, kill the last T, and then proceed to just stand above the bomb and not try to defuse it. The algorithm needs to release the bots fully at this point if the bomb keeps ticking but no one is defusing, it's a sign that something has gone wrong with the bot logic, a silent unhandled exception of some sort and for the remainder of the round the CTs need to be under native CS AI control.
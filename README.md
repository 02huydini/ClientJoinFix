# A plugin for Client to detect newer Clients in the server

## Core problem
Clients failed to register newer Client when they join in, this includes problems like:
- Unable to recruit newer Client because it will show there's no dead player in the server.
- Unable to track their location when SHIFT.
- Unable to see their state.
- Unable to see their skin through Noah Multiplayer Sprites Changer.

## What does this do?
Refresh newer Client when they join the server you're in that work for your side. This prevents bleeding when rejoining with amputated limbs, rejoining which causes other Client to take on your bug instead.

## Notes
Work on the base KrokMP.
For Client themselves only, other player still unable to update you if they were there before you.
Work along with [Multiplayer Sprite Replacer](https://www.nexusmods.com/scavprototype/mods/74) and [Distress Signal](https://www.nexusmods.com/scavprototype/mods/661).

<details>
<summary><b>Installation</b></summary>

1. Extract .dll file from download file.
2. Move your .dll file to "\Casualties Unknown Demo\BepInEx\plugins"

For step-by-step installation:
1. Open ClientJoinFix.zip file.
2. Copy ClientJoinFix.dll, by right click on it and choose "Copy", or by left click on it and combo Ctrl+C.
3. Go to Casualties:Unknown Steam library page.
4. Click on Manage (settings or gear icon).
5. Manage > Browse local file.
6. Locate ..\BepInEx\plugins\ (remember it as your default plugin folder, you should know this if you have KrokMP).
7. Right click on empty space and choose "Paste", or combo Ctrl+V.
</details>

[Client Join Fix on Nexusmods](https://www.nexusmods.com/scavprototype/mods/590)
# Pete Online: how to play with friends

Pete Online is the normal Pete game plus online co-op. Everyone plays in the same world at the same time.

## How co-op works

- **Your Pete is yours.** Your own game moves your Pete, so running and jumping feel exactly like the normal game. You see your friends as Petes in their own colours, with their names and health over their heads.
- **One world.** The host's game runs the enemies, the orbs, the health pickups and the finish, and everyone sees the same thing. If your sword hits a fly, it's hit for everyone.
- **Players pass through each other** and can't hurt each other.
- **Orbs count for the whole team.** When anyone grabs one, the team has it. The finish opens when the team has both.
- **When anyone reaches the finish** with both orbs, the run ends for everyone, with one team time.
- **If you die,** you start again where you first stood in the room you were in, with full health. Everyone else keeps playing. **R** does the same.
- The pause menu doesn't stop the game online, because everyone else is still playing.
- Up to 6 players.

## Starting a game

1. Everyone opens **Pete Online.exe**. On the title screen, the **PLAY WITH FRIENDS** panel is in the bottom-right corner.
2. Everyone types their name.
3. **The host** presses **Host a game**. The panel shows the host's address (see "Connecting" below). The host sends it to their friends, for example on Discord.
4. **Friends** type the host's address in the box and press **Join**.
5. When everyone's in the player list, the host presses **Start the run**. Everyone starts together.
6. At the end, the host can press **Play again** or **Back to the title**.

The first time you host, Windows may ask if Pete Online may use the network. Click **Allow**.

Everyone needs the same version of Pete Online.

## Connecting

Pick one of these. The first one is the easiest.

**A. Same house or same Wi-Fi.** Friends join with the host's address that starts with `192.168.` (or `10.`). It's shown in the host's panel.

**B. Over the internet with Radmin VPN** (free, no router setup).
1. Everyone installs Radmin VPN and joins the same Radmin network (the host makes one and shares its name and password).
2. Friends join with the host's Radmin address. It starts with `26.` and is shown in Radmin and in the host's panel.

**C. Over the internet with playit.gg** (only the host installs something).
1. The host installs playit.gg and makes a **UDP** tunnel to port **7777**.
2. playit.gg shows an address like `something.gl.at.ply.gg:12345`. Friends type exactly that, including the `:12345` part.

**D. Over the internet with port forwarding** (no extra programs, but needs your router).
1. The host forwards **UDP port 7777** on their router to their PC.
2. The host presses **Show my internet address** in the panel and sends that address to their friends.

## If something goes wrong

- **"Couldn't reach the host":** check the address. If you're joining over the internet, check that the host's connection method from above is set up (Radmin, playit.gg, or the port forward). Also check the host clicked **Allow** when Windows asked.
- **"Different version":** everyone needs the same Pete Online.
- **Someone's Pete looks jumpy:** their internet is slow or busy. It smooths out when it gets better.

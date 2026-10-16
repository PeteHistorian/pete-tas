# Pete TAS: how to use it

The TAS tool lets you play Pete one frame at a time, go backwards, change any input, and see exactly how fast Pete is moving. When you've found the fastest route, it can play the whole run back for a video.

## Starting it

- **Easiest:** open the `PeteTAS Build` folder and double-click **`Pete TAS.exe`**.
- **In Unity:** open the Pete project, then use the top menu **Pete TAS → Play TAS**. The normal Play button still plays the normal game.

The level starts frozen on **frame 0**. One frame is 1/50th of a second, the same speed the game's physics runs at.

## The screen

- **Top left:** the frame number, the time, and Pete's numbers:
  - position and velocity (speed left/right and up/down)
  - whether he's on the ground, coyote time and jump buffer (shown in frames)
  - knockback, HP, orbs
- **Right side, INPUTS grid:** one row per frame. The columns are **L R U D J A** (left, right, up, down, jump, attack).
  - Click a square to turn that button on or off for that frame.
  - Drag down a column to paint lots of frames at once.
  - Click a frame number to jump to that frame.
  - Green rows are checked exact, yellow rows aren't checked yet, and red marks a problem.
- **Bottom, SPEED graph:** Pete's x speed (blue) and y speed (pink) over time, with the newest ghost's speed as a grey dashed line.
- **In the level:**
  - Pete's route is drawn as a line, colored by speed (blue is slow, yellow is top speed, red is faster than running, like after a knockback).
  - The dots are every 10 frames, and the big dots every 50.

## Keys

| Key | What it does |
|---|---|
| **F** / **B** | one frame forward / back (hold to keep going) |
| **Shift + F / B** | 10 frames |
| **P** | play / pause |
| **Q** | record on/off |
| **Home / End** | start / end of the movie |
| **V** | Verify (replay from the start to check everything is exact) |
| **M** | free camera: zoom out, look around, click the route to jump to that frame |
| **X** | show / hide hitboxes |
| **C** | center the camera on Pete |
| **Shift + click** frame numbers | select a range of frames |
| **Ctrl + C / Ctrl + V** | copy / paste frames |
| **Delete** | delete the selected frames |
| **G / L** | View tab / Lines tab |
| **H** | hide the panel |
| **Ctrl + S** | save |
| **Esc** | stop the video or the auto-search |

## Two ways to make inputs

1. **Record:** press **Q**. Then play with the normal game controls and press **P** to play at normal speed, or **F** to step one frame at a time.
   - **Move / aim:** arrows or WASD, or the controller's left stick.
   - **Jump:** Space, or A (Cross). Hold it for a higher jump.
   - **Attack:** O, a left click in the level, or X (Square). Clicks on the TAS panel never count as attacks.
   - Whatever you hold gets written into each frame as you play.
   - You can set the speed to 50% or 25% to make playing easier.
2. **Edit the grid:** click squares in the INPUTS grid. If you change an earlier frame, the tool replays up to where you were, so you see the new result straight away.

## Selecting, copying, presets and moving frames

- **Select frames:** hold **Shift** and click two frame numbers in the INPUTS grid. The selected frames turn blue. Press **Esc** or **Unselect** to clear.
- **Copy / paste:** **Copy** (Ctrl+C) copies the selected frames. **Paste** (Ctrl+V) puts them in:
  - **If frames are selected,** the pasted piece *replaces* them. If it's shorter or longer, everything after moves to match, so the rest of the run stays lined up.
  - **If nothing is selected,** it goes in at the next frame, and everything after moves later.
- **Presets** (Movie tab) are pieces of a run you keep for later, like your fastest way through a room.
  - Select the frames, type a name and press **Save selection**.
  - Later, press **Paste** next to the preset to drop it in (same rules as above).
  - Presets are saved as files in `TAS Files\Presets`, so they work in any movie.
- **Moving a lot of frames at once:** under "Move everything from frame X on", set a number with **-** and **+** (Shift+click changes it by 10). Then:
  - **Later +N** inserts N empty frames, pushing everything after later.
  - **Earlier -N** deletes N frames, pulling everything after earlier.

  Use this when you find a faster way earlier in the run and need the rest to line up again. It works from the selected frame, or from the next frame if nothing is selected.
- **Delete** (or the Delete key) removes the selected frames, and everything after moves up.

## Center camera

Press **C**, or tick **Center camera on Pete** in the View tab. The camera then follows Pete and keeps him in the middle of the open part of the screen. Scroll the mouse wheel to zoom. Press C again for the game's normal room cameras. It also applies during video playback.

## Hitboxes

Press **X**, or tick **Hitboxes** in the View tab, to see the collision boxes:
- **green:** Pete's body (including the little strip under his feet that checks for ground)
- **orange:** the sword swing, only while it's out
- **red:** enemies and spikes. Faint red shows where an enemy notices Pete or turns around.
- **blue:** health pickups and orbs
- **gold:** the finish zone

Tick **Level collision too** to also see every floor and wall tile (white) and the room triggers (faint purple). Hitboxes can also be shown in video playback (Video tab).

## Finding the fastest line

- **Ghosts:** whenever you change an earlier input, your old route stays on screen as a faded colored line, with a see-through "ghost Pete". You can see both routes at once. Pin the ghosts you want to keep in the **View** tab.
- **Lines:** in the **Lines** tab, click *Add up-down line* (or left-right), then click in the level to place a finish line or checkpoint. The tab tells you which route reaches it first, and by how many frames (for example "3.40 frames faster").
- **Auto-search:** in the **Search** tab, pick a line and press *Start search*. The tool tries lots of inputs on its own, starting from the frame you're on.
  - **Beam search** tries any mix of buttons.
  - **Jump timing** holds one direction and tries every jump start and hold length.
  - Each result has two buttons: **Ghost** shows it in the level, and **Apply** puts it into your movie. Applied results are checked with an exact replay straight away.

## The colored light (top left)

- **Green:** every frame up to here has been checked exact.
- **Yellow:** you've rewound since the last check. Rewinds come out exact in testing (the tool loads a savestate from half a second earlier and replays forward), but press **V** if you want to be 100% sure.
- **Red:** something came out different from last time. Press **V** and the tool replays from the very start to get the true result.

**Verify is always the final word.**

## Saving

- Movies are saved in the **TAS Files** folder as plain text files. You can open them in Notepad. Each line is "how many frames, then the buttons", for example `12,R,J`.
- The tool also autosaves to `autosave.txt` every 20 seconds and loads it the next time you start.

## Making a video

In the **Video** tab, press **Play video from the start**. The panel hides and the whole run plays at normal speed. You can turn on:
- a button display
- the frame counter
- the route line
- going to the end screen at the finish

For a perfectly smooth video, also tick **save every frame as a picture**. The frames go into the TAS Files folder, and a tool like ffmpeg can turn them into a 50 fps video.

## Good to know

- The game itself is unchanged when you play it normally. The TAS only switches on in `Pete TAS.exe` or through **Pete TAS → Play TAS** in Unity.
- The TAS uses a controller-style instant up/down for aiming attacks. With a keyboard, the real game ramps up/down in over a few frames, so a keyboard player can't quite match every attack aim the TAS can.
- Pete dying stops the TAS on that frame (in the real game the level would restart). Go back and change the inputs.
- Reaching the finish stops the TAS too, and shows the final in-game time.
- The tool found some speed tech in the game code that's worth trying:
  - A **down-attack on an enemy** launches Pete upward at speed 15.
  - **Taking a hit** knocks Pete away at speed 15 for 0.2 seconds.
  - An orb can give power again **5 seconds** after you touch it, if you touch it again.

## Checking the tool itself

The tool has a built-in self-test. In Unity, use **Pete TAS → Run Self-Test**. It plays a long made-up run and checks that:
- replays, rewinds and edits all come out exact
- the auto-search works

The results go into `TAS Files\selftest-results.txt`.

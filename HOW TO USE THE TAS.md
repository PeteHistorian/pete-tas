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
| **T** | show / hide prediction lines |
| **C** | center the camera on Pete |
| **Shift + click** frame numbers | select a range of frames |
| **Ctrl + Z / Ctrl + Y** | undo / redo (Ctrl + Shift + Z also redoes) |
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
- **Moving a lot of frames at once:** under "Move everything from frame X on", type a number in the box (or use **-** and **+**; Shift+click changes it by 10). Then:
  - **Later +N** inserts N empty frames, pushing everything after later.
  - **Earlier -N** deletes N frames, pulling everything after earlier.

  Use this when you find a faster way earlier in the run and need the rest to line up again. It works from the selected frame, or from the next frame if nothing is selected.
- **Delete** (or the Delete key) removes the selected frames, and everything after moves up.

## Undo and redo

**Undo** (Ctrl+Z) takes back the last change to the movie, and **Redo** (Ctrl+Y) puts it back. The buttons are under Record / Speed / Verify.

- Every kind of change counts: clicking or painting the grid, pasting, inserting or deleting frames, labels, applying a search result, loading a movie, and recording.
  - A whole drag of painting is one step.
  - Each stretch of recording is one step.
- After an undo, the tool replays from the first frame that changed, so you see the old result straight away. You stay on the same frame.
- Up to 300 steps are kept. Making a new change after undoing clears the redo steps.

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

## Prediction lines (like Geometry Dash's "show trajectory")

Press **T**, or tick **Prediction lines** in the View tab. Lines show where Pete is heading from the frame you're on. They update every frame, even while the run is playing or you're recording.

Each kind of line has its own tick box in the View tab, so you can show only the ones you want:

- **Green: your path.** Where your inputs take Pete.
  - While recording, it follows the keys you're holding right now, so you can see where you'll land before you get there.
  - Normally, past the end of the movie, it keeps holding the last frame's buttons.
  - Tick **...to where inputs end** and it follows the whole rest of the movie instead, then lets go of everything and shows where Pete **stops**.
- **Blue: longest jump.** Hold jump from the next frame on.
- **Purple: shortest jump.** Tap jump for a single frame, the lowest hop you can do.
- **Yellow: let go of jump.** Useful while Pete is rising with jump held: it shows how much shorter the jump gets if you let go now.
  - The jump lines only show up when they'd go somewhere different from the green line (no line means no jump is possible right now).
- **Red: if an enemy hits you.** One line for each enemy or spike near Pete that can hurt him right now, showing where the knockback throws him.
- **Attacks: what happens if you attack on the next frame.** Orange is forward, pink is up, tan is down (the pogo). They only show up when the swing actually hits something and changes where Pete goes. For example, they show a **fly pull**: Pete's sword hits the fly while the fly hits Pete.
- **Knockback & labels:**
  - Any part of a line where Pete is being knocked back gets a wide red band.
  - A red dot marks each hit.
  - Labels say what happens and when: **HIT**, **knockback over (10 f)**, **hits the Fly**, **pogo**, **defeats the Larva**, **lands (+12 f)**, **inputs end**, **stops here**.
- **Markers:** dots every 10 frames, a small circle where a jump starts, a bar under Pete's feet where he lands, and a green outline of Pete where the green line first lands.
- **- / +** next to the main tick box sets how far ahead the lines go (0.5 to 5 seconds).
- The Video tab has **Show prediction lines** too, if you want them in a video.

How it works: a stand-in Pete runs ahead in a hidden copy of the level. It uses the game's own physics and Pete's own rules: speed, jump height, jump hold, coyote time, knockback, and the sword (which frames it's out, its hitbox, damage, enemy knockback and pogo). So the lines never change your run.

When the real route ahead is already known (frames you've played before), the View tab tells you how close the green line is to it.

Limits: enemies are moved in a simple way. Walking enemies keep walking (and turn at walls), and flying enemies chase Pete once they've noticed him.

## Finding the fastest line

- **Ghosts:** whenever you change an earlier input, your old route stays on screen as a faded colored line, with a see-through "ghost Pete". You can see both routes at once. Pin the ghosts you want to keep in the **View** tab.
- **Lines:** in the **Lines** tab, click *Add up-down line* (or left-right), then click in the level to place a finish line or checkpoint. The tab tells you which route reaches it first, and by how many frames (for example "3.40 frames faster").
- **Auto-search:** in the **Search** tab, pick a line and press *Start search*. The tool tries lots of inputs on its own, starting from the frame you're on.
  - **Beam search** tries any mix of buttons.
  - **Jump timing** holds one direction and tries every jump start and hold length.
  - Each result has two buttons: **Ghost** shows it in the level, and **Apply** puts it into your movie. Applied results are checked with an exact replay straight away.

## Going back a frame

To go back, the tool loads a savestate from just over half a second earlier and replays forward at top speed with the level hidden. While it does, the screen keeps showing the level as it was, so it doesn't flash. The same goes for Verify, loading a movie and the auto-search.

## The colored light (top left)

- **Green:** every frame up to here has been checked exact.
- **Yellow:** you've rewound since the last check. Rewinds come out exact in testing (the tool loads a savestate from half a second earlier and replays forward), but press **V** if you want to be 100% sure.
- **Red:** something came out different from last time. Press **V** and the tool replays from the very start to get the true result.

**Verify is always the final word.**

## Saving

- Movies are saved in the **TAS Files** folder as plain text files. You can open them in Notepad. Each line is "how many frames, then the buttons", for example `12,R,J`.
- The tool also autosaves to `autosave.txt` every 20 seconds and loads it the next time you start.

## Making a video

In the **Video** tab, pick what to show:
- a button display
- the frame counter
- the route line
- hitboxes
- going to the end screen at the finish

Then choose one of these:
- **Play video from the start:** watch the whole run at normal speed with the panel hidden.
- **Export video file (MP4 with sound):** saves the whole run as a ready-to-upload MP4.
  - The file goes in `TAS Files\Videos`. **Open videos folder** takes you there.
  - Every frame is saved at 50 fps with the game's sound perfectly in sync. It runs faster than normal while saving and never drops a frame, even on a slow computer.
  - The video is the size of the TAS window. For a sharp 1080p video, make the window bigger or press **Alt+Enter** for fullscreen before exporting, and don't resize it while it saves.
  - If you also turned on the end screen, the video ends with a few seconds of it, then the tool returns to the editor by itself.
  - Press **Esc** to stop an export early. Whatever was saved so far still becomes a video.

The MP4 export uses **ffmpeg**, a free video tool that's installed on this PC. On a computer without it, the export saves every frame as a picture plus a `sound.wav` file instead, and most video editors can import those.

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

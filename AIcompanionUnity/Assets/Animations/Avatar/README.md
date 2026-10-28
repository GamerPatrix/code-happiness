# Avatar animations

Drop animation clips in this folder and run **Tools > Code Happiness > Scan Avatar Animations**.

The scan reads every `.anim`, `.fbx` or `.bvh` file below this folder, makes sure the avatar's
AnimatorController has a state for each clip, and writes the list into the `AvatarAnimationLibrary`
component on `AICompanion`. That component hands the list to the LLM in its system message, so the
model knows which `[anim:Name]` tags it is allowed to use.

## Folder layout decides where a clip plays

| Folder | Layer | Notes |
| --- | --- | --- |
| `Avatar/` (root) | Base layer | Full body clips that replace the idle pose |
| `Avatar/Base/` | Base layer | Same as putting them in the root |
| `Avatar/FullBody/` | Base layer | Whole-body clips (sitting, laying, aiming...). Base layer, *not* idles |
| `Avatar/Idle/` | Base layer | Registered as idle animations the avatar picks at random |
| `Avatar/Idle/Standing/`, `Avatar/Idle/Sitting/` | Base layer | The subfolder name **is** the idle mode, so the model can ask for it with `[idle:sitting]` |
| `Avatar/Pose/` | Base layer | The avatar *stays* in these instead of playing them out |
| any other subfolder, e.g. `Avatar/Gesture/` | its own additive layer | Overlays on top of the base layer, so it can play while the avatar talks |

The subfolder name becomes the Animator layer name, so keep the names short and sensible
(`Gesture`, `Arms`, `Head`...). Idle mode names are shown to the model exactly as the folder is
named, and the lookup ignores case, so `Idle/Standing/` answers to both `[idle:Standing]` and
`[idle:standing]`.

Only clips left loose in `Avatar/` itself are guessed to be idles from their name, so a clip called
`Sitting Idle` inside `FullBody/` will not hijack the idle rotation. Put idles in `Idle/<mode>/`
instead - that is what makes them both idles *and* a mode the model can switch to.

## Poses vs. animations

An **animation** plays out and ends. A **pose** holds. The difference is where the clip lives (or a
`#pose` keyword in its sidecar), not how long it is.

* Anything under `Pose/` (or `Poses/`) is a pose.
* A pose with no `#hold=` holds forever - until the model asks for something else or changes the idle
  mode. That is what lets the avatar sit down and *stay* sitting.
* A pose filed into an `Idle/` folder gets an 8 second hold, otherwise it would block the random idle
  rotation forever.

The model asks for them with different tags:

```
[anim:"Standing Clap" loops=2 next="Female Standing Pose"]  play the clap twice, then land in a pose
[pose:"Female Sitting Pose"]                              sit down and stay there until told otherwise
[pose:"Female Sitting Pose" hold=5 next="Look Around"]    hold 5s, then play Look Around
[idle:sitting]                                            sit down and stay sitting while idling
```

Clip names contain spaces, so put them in double quotes. `[idle:...]` takes a single word and is not
quoted.

`[anim:...]` options, all optional and separated by spaces:

| Option | Meaning |
| --- | --- |
| `loops=3` | play the clip 3 times in a row |
| `hold=2` | stay in the last frame 2 extra seconds |
| `next="OtherName"` | move into that animation/pose when this one ends |

`[pose:...]` takes the same options. Without `hold=` a pose holds forever; with `hold=5` it releases
back to idle after 5 seconds.

There is no `loops=` on a pose - a pose does not repeat. Use `hold=` for how long it should last and
`next=` for what should follow it.

## Idle modes and idle randomisation

Clips under `Idle/<mode>/` form the idle rotation for that mode. While the avatar is doing nothing it
picks one at random from the current mode, so a mode with several clips never looks like a looping
video.

* Clips longer than a second get their length jittered by 0.85x-1.35x, and a random 0.2-1.5 s gap is
  left between idles, so the timing never feels metronomic.
* `[idle:mode]` switches modes and *stays* switched - the avatar keeps sitting until it is told
  otherwise. Ask for it when the avatar sits, lies down, or leans.
* Add a mode by making the folder. `Idle/Laying/` gives the model `[idle:laying]` automatically on the
  next scan.

## Importing Mixamo / third party FBX files

The avatar is **Humanoid**, so a clip can only move it if the FBX is imported as Humanoid too. A
Generic FBX keeps its curves bound to its own bone names (`mixamorig:Hips.m_LocalPosition.x`) and the
`Animator` silently binds nothing - the clip "plays" and the avatar does not move.

If that happens, select the FBX and set:

* **Rig > Animation Type** = `Humanoid`, **Avatar Definition** = `Create From This Model`
* **Animation > Take 001** frame range covering the whole clip (Mixamo takes are called `mixamo.com`)

The `FullBody/` clips are already set up this way.

## Root motion is switched off

`AvatarAnimationLibrary` sets `Animator.applyRootMotion = false` on every Animator under the avatar at
startup. Mixamo clips carry `RootT`/`RootQ` curves, and with root motion enabled those curves drag the
whole avatar across (and off) the screen. The companion is a chatdoll - it stays where it is put, so the
root curve is ignored and only the pose plays.

## How long an animation is held

`ModelController.AnimatedSay()` waits for the animation of each sentence to play out before moving on,
so a reply made of several tagged sentences shows each pose in turn instead of collapsing into the last
one. When the speech synthesizer returns no audio (the `DummySpeechSynthesizer` this companion uses),
nothing else would occupy that time and the avatar would snap straight back to idle.

`ModelController.Max Animation Hold Seconds` (default `30`) caps that wait, so a long clip or a big
`loops=` count cannot stall the conversation. Raise it if you add long clips, lower it if replies feel
sluggish.

The full length the controller waits is `clip length x loops + hold`, which is what the model is told in
the system prompt next to each animation. A `next=` chain counts too: the reply stays alive for the whole
chain, not just the first clip, so `[anim:"Standing Clap" next="Female Sitting Pose"]` is not cut off when
the next sentence arrives. A `next=` that leads to a permanent pose ends the wait there, since a pose that
holds forever has no length to wait for.

Returning to idle is driven by the reply that ends the turn, or by an explicit
`ModelController.StartIdling()` call. A pose with no `hold=` survives both - `StartIdling()` deliberately
does nothing while a permanent pose is playing, otherwise the avatar would stand back up the moment a
reply finished. It also does nothing while animations are still queued, so the chained clip above still
gets its turn even though the reply has already been spoken.

## Naming

The animation name the model uses is the clip name. For `.anim` files that is the file name. For FBX
files it is the take/clip name inside the FBX - if the FBX was imported without named takes it comes
in as `Take 001`, and the scan falls back to the FBX file name instead.

## Teaching the model *when* to use a clip

Put a `.txt` next to the clip with the same file name and write one sentence describing when the
avatar should play it. That sentence is what the model sees.

```
Avatar/Gesture/A_Cheer.fbx
Avatar/Gesture/A_Cheer.txt   <- "Happy celebratory gesture. Use it when the avatar is excited."
```

Lines in that file that start with `#` are metadata for the scanner instead of text for the model, and
are stripped from the description:

| Keyword | Effect |
| --- | --- |
| `#pose` | treat the clip as a pose even outside `Pose/` |
| `#hold=5` | hold 5 seconds (for a pose this is the whole hold) |
| `#loops=3` | play 3 times in a row |
| `#next=Wave` | move into `Wave` when this clip ends |

```
Use when the avatar applauds while standing.
#loops=2
#next=Female Standing Pose
```

The model is told each clip's total length, loop count and what it ends in, so it can time a movement
to the sentence it is saying.

**Tools > Code Happiness > Create Missing Animation Description Files** creates a stub `.txt` for
every clip that does not have one, so you can fill them in.

## After adding or removing clips

Run the scan again. It reuses the existing AnimatorController, deletes the states it made for clips that
are gone, rebuilds the rest, and replaces the catalogue in the component. States you added by hand are
left alone. Save the scene afterwards.

Idle clips always get the lowest `BaseParam` values, because the parameter starts at `0` and whatever owns
`0` is the pose the avatar stands in when the scene starts. Additive layers get values starting at `1` -
`0` means "no gesture playing".

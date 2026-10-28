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
| `Avatar/Idle/` | Base layer | Also registered as idle animations the avatar picks at random |
| any other subfolder, e.g. `Avatar/Gesture/` | its own additive layer | Overlays on top of the base layer, so it can play while the avatar talks |

The subfolder name becomes the Animator layer name, so keep the names short and sensible
(`Gesture`, `Arms`, `Head`...).

Only clips left loose in `Avatar/` itself are guessed to be idles from their name, so a clip called
`Sitting Idle` inside `FullBody/` will not hijack the idle rotation.

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

## How long a pose is held

`ModelController.AnimatedSay()` waits for the animation of each sentence to play out before moving on,
so a reply made of several tagged sentences shows each pose in turn instead of collapsing into the last
one. When the speech synthesizer returns no audio (the `DummySpeechSynthesizer` this companion uses),
nothing else would occupy that time and the avatar would snap straight back to idle.

`ModelController.Max Animation Hold Seconds` (default `10`) caps that wait, so a reply made of many
tagged sentences cannot stall. Raise it if you add long clips, lower it if replies feel sluggish.

Returning to idle is driven by the reply that ends the turn, or by an explicit
`ModelController.StartIdling()` call. An untagged reply in the middle of a conversation leaves the last
pose playing.

## Static poses

A one or two frame clip (a pose rather than an animation) is held for **10 seconds** instead of the
1 second minimum given to normal clips, otherwise it flashes past too fast to notice.

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

**Tools > Code Happiness > Create Missing Animation Description Files** creates a stub `.txt` for
every clip that does not have one, so you can fill them in.

## After adding or removing clips

Run the scan again. It reuses the existing AnimatorController, deletes the states it made for clips that
are gone, rebuilds the rest, and replaces the catalogue in the component. States you added by hand are
left alone. Save the scene afterwards.

Idle clips always get the lowest `BaseParam` values, because the parameter starts at `0` and whatever owns
`0` is the pose the avatar stands in when the scene starts. Additive layers get values starting at `1` -
`0` means "no gesture playing".

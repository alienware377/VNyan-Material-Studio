# Material Studio for VNyan

**Edit your avatar's materials and shaders live inside [VNyan](https://suvidriel.itch.io/vnyan),
keep the edits per model, and bake them into a new `.vsfavatar` with the plugin's own exporter.**

Material Studio lists every material on the avatar you have loaded and gives each one an
inspector that is built from the shader itself, so it is not limited to a few known shaders.
Changes show on the model straight away, are saved for that model file, and come back every
time the model loads. When you are happy, export a new `.vsfavatar` with everything baked in;
VNyan and VSeeFace both load it. Warudo character mods (`.warudo`) can be exported to a
`.vsfavatar` too.

## Features

- **Any shader.** Poiyomi (locked and unlocked), lilToon, MToon (VRM 0.x and 1.0), Unity's
  built-in shaders (Standard, Unlit, Legacy, Particles) and any custom shader you add from a
  `.unitypackage`.
- **Every property.** Sliders, toggles, dropdowns, colours (HDR too), vectors, and textures
  with tiling/offset. Poiyomi's sections, section on/off ticks and render presets work like
  they do in Unity. Type in *Find* to search every section; **R** resets one property.
- **Pick on model.** Turn it on and point at the avatar: the part under the cursor gets an
  outline. Click to edit its material, Shift or Ctrl-click to add more.
- **Edit several materials together.** Tick them in the list on the left. **Copy / Paste** a
  whole material (same shader: an exact copy; different shader: every property both share),
  or copy and paste one section, even between matching sections such as Matcap 0 and
  Matcap 1.
- **Switch shaders** on one or many materials at once; values both shaders share carry over.
- **Make unique** gives one mesh its own copy of a shared material. **Reset selected** and
  **Reset ALL materials** put things back exactly how the model shipped.
- **Textures:** load a PNG/JPG from a file, pick one already on the avatar, clear the slot, or
  **Show** it in Explorer (a texture packed inside the avatar is saved as a PNG copy first, so
  you can edit it and load it back).
- **Undo/redo for everything** (Ctrl+Z / Ctrl+Shift+Z with the cursor over the window).
- **Saved per model.** Edits save by themselves a moment after each change (or only when you
  press *Save*) and are applied again whenever that model loads.
- **Missing shader features are compiled for you.** A shader inside an avatar only has the
  feature combinations the avatar shipped with. Switch on one it lacks and Material Studio
  keeps the last look that worked, compiles the new combination in the background (about
  20 seconds) and swaps it in. VNyan stays usable meanwhile.
- **Locked Poiyomi, both ways.** *Unlock for editing* to change every value live, then
  **Lock & optimise** (Poiyomi's own optimizer) from inside VNyan, or **Lock all Poiyomi** in
  one go. Tick **L** on values you want to keep changing after locking. Poiyomi is never
  swapped for MToon.
- **On switch** in the title bar. Off shows the model's original materials, saves nothing
  and pauses editing and background work; your edits stay in their file and come back when
  you turn it on.
- **Its own `.vsfavatar` exporter.** Your edits are baked in, plus the bone weights exactly as
  VNyan shows them, so weights painted with Weight Studio (more than 4 per vertex included)
  come along. The new file is opened again and checked against the original before it
  replaces anything. It does not need any other plugin.
- **Export from a Warudo mod.** Pick a `.warudo` file and get a `.vsfavatar` with your edits:
  the mod's own components and physics are rebuilt, and it gets the humanoid and VRM setup
  the way the [Warudo Importer](https://github.com/alienware377/VNyan-Warudo-Importer) does
  it in VNyan. A mod loaded with the Warudo Importer is picked up by itself.
- **Progress banner** under the window for every background job: what is running, for which
  materials, how long it has taken, and the result.

## Requirements

- VNyan, with *Settings > Misc > Allow Third Party Plugins* turned on.
- **Live editing needs nothing else.**
- **Adding shader packages, compiling missing shader features, locking or unlocking Poiyomi,
  and exporting** run Unity in the background, so they also need:
  - **Unity 2019.4.31f1** (install it with Unity Hub). It is the VSF SDK's Unity version, so the
    export loads in both VNyan and VSeeFace.
  - **Your VSF SDK Unity project** (the one you export avatars with). Material Studio never
    changes it: on the *Packages* tab it copies the SDK parts it needs (VSF SDK, UniVRM,
    DynamicBone, your shader folders) into its own private Unity project. You can tell it to
    use your project directly instead, if you prefer.
- **Exporting from a `.warudo` file** needs a newer Unity, because Warudo mods are made with
  Unity 2021.3 or later and Unity can only open files from its own version or an older one.
  Any installed Unity from the mod's version up to VNyan's own (2022.3) works. If none is
  installed, the *Export* tab has a button that opens Unity Hub's install window for VNyan's
  version. Unity itself cannot come with the plugin (its licence does not allow passing it
  on), but it is free: sign in to Unity Hub once for the Personal licence. Material Studio
  sets up a second private project for these exports by itself, from your VSF SDK project if
  you have one, otherwise from the UniVRM package that comes with the plugin. The result
  loads in VNyan; VSeeFace's older Unity cannot open it.

## Install

1. Download **`VNyan-Material-Studio-v1.1.0.zip`** from
   [Releases](https://github.com/alienware377/VNyan-Material-Studio/releases) and unzip it.
2. Close VNyan.
3. Run **`install.bat`**. It looks for your VNyan folder by itself (a running VNyan, VNyan's own
   log, Steam, installed apps, itch) and asks you to confirm, or asks you to **open your VNyan
   folder** (the one with `VNyan.exe` inside). It only asks for admin rights if that folder is
   write-protected.
4. Start VNyan and open **Material Studio** from the plugins menu.

The installer copies `MaterialStudio.dll`, `MaterialStudio.vnobj` and the `UnityToolset` folder
(the Unity editor scripts used for shader packages, locking and export) into
`<your VNyan folder>\Items\Assemblies\MaterialStudio\`.

## Usage

Open Material Studio with the avatar loaded. The list on the left shows each mesh with its
materials; click a material (or use **Pick on model**) and edit it on the tabs:

| Tab | What it does |
|---|---|
| **Properties** | Every property of the picked material, grouped like the shader's own inspector, with per-section on/off ticks and Copy/Paste. Locked Poiyomi shows *Unlock for editing*; unlocked Poiyomi shows *Lock & optimise*. |
| **Shader** | Switch the selected materials to another shader: the avatar's own, ones built from your packages, Unity built-ins, or VRM/MToon. |
| **Packages** | Pick your VSF SDK project, add shader `.unitypackage`s (or shader folders), tick the shaders you want, press **Build shader bundle**. Unity runs in the background; VNyan stays usable. |
| **Export** | Build a new `.vsfavatar` with your edits applied, from this avatar's file, from its prefab in its Unity project, or from a `.warudo` mod. Options: include Weight Studio's live weight paint, keep Poiyomi locked where it is locked now, include unused assets from the original file (on by default), and load the new avatar in VNyan when done. |
| **Settings** | Apply saved edits on load, save automatically, compile missing shader features automatically, show hidden properties, Unity editor override, the saved-edits folder, Reset ALL materials, and the message log. |

### Locked Poiyomi

Locked (optimised) Poiyomi bakes most values into the shader itself, so only properties
marked *animated* change live; the others show a `*`. They still go into the export. To edit
**everything** live: add your Poiyomi `.unitypackage` on *Packages*, press *Build shader
bundle*, then press **Unlock for editing**. When you are done, **Lock & optimise** locks it
again. The export keeps each Poiyomi material locked or unlocked exactly as it is in VNyan
(turn that option off on *Export* to export everything unlocked).

Before locking, the export works out from each material's values which features it uses. If
a lock would leave one out (it would look flat), nothing is written and the log names the
material; with that option off it exports unlocked, with every feature.

### Where your edits are kept

`%USERPROFILE%\AppData\LocalLow\Suvidriel\VNyan\MaterialStudio\Recipes\`: one `.json` per
model plus a folder for textures you loaded from files. *Settings > Open folder* opens it.

### Works with other plugins and exporters

- **Exporting by hand from Unity**: copy `UnityToolset/MaterialStudioTools` from the plugin
  folder into your project's `Assets`, then use *VSF SDK > Material Studio > Apply VNyan
  material edits to selected avatar...*.
- **Other plugins** can pick up your saved edits too, in their versions that support Material
  Studio (check their release notes; older versions simply export without your edits):
  - [Weight Studio](https://github.com/alienware377/VNyan-Weight-Studio): its `.vbx` and VRM
    exports read the live materials, its "export from a `.vsfavatar` file" asks Material
    Studio for the saved edits, and the Weight Studio Reader keeps live edits when it
    rebuilds an avatar from a `.vbx`. Its Unity window has *Apply VNyan material edits
    (Material Studio)...*, which applies the saved edits (to copies of the materials) before
    you export with the VSF SDK.
  - [Warudo Importer](https://github.com/alienware377/VNyan-Warudo-Importer): its
    `.vsfavatar` export applies the saved edits when Material Studio is installed. Material
    Studio's own *From a .warudo file* export uses the same loading steps, so either works.

### Limits

- A running VNyan cannot compile shaders or build bundles; that is why Unity runs in the
  background for packages, feature compiles, locking and export.
- Exporting from a `.vsfavatar` file rebuilds the avatar from the file. Animator controllers
  (VSF custom animations), audio clips, fonts and 3D or array textures cannot be rebuilt from
  a bundle and are left out with a warning; export from the avatar's prefab in its Unity
  project to keep them. If some components use scripts that are not in the private project,
  the export stops and says which; add those folders to the project.
- Exporting from a `.warudo` file: the mod's bone physics become DynamicBone (as in Warudo)
  only when your VSF SDK project has DynamicBone; the mod's own VRM spring bones work either
  way. Magica Cloth needs Magica Cloth 2 in the project. Shaders the mod uses must be in the
  project, the same as for a `.vsfavatar`; add their package on the *Packages* tab.
- Textures inside a `.vsfavatar` can only be read back from the GPU, so they come out at the
  quality the avatar shipped with: any detail lost to its texture compression stays lost.
- Live weights are matched by vertex count and bone names; a mesh whose shape changed is left
  as it is (the log says which).

## Updates

Material Studio checks this repository's releases at most every 6 hours. When a newer version
is out, a green **Update vX** button appears at the top of the window; it opens the release
page so you can download the new zip and run `install.bat` again. Your saved edits are kept.

## Build from source

`python build.py` compiles `MaterialStudio.dll` against VNyan's own managed DLLs (Unity's
Roslyn compiler), compile-checks the Unity editor scripts against Unity 2019.4.31f1 and
2022.3.22f1, copies the loading code it shares with the Warudo Importer from a checkout of that
repository next to this one (when there is one), builds
`MaterialStudio.vnobj` with Unity 2022.3.22f1 in batch mode, and stages
`dist/MaterialStudio/`. `python build.py --dll` skips the bundle step. The tool paths at the
top of `build.py` and the references in `build/build.rsp` point at the build machine's Unity
editors and VNyan folder: change them to match yours.

`python make_release_zip.py` packs `dist/MaterialStudio/`, the installer, this README and the
license into the release zip. `build/headless_test.py` runs the editor toolset end to end
without VNyan (see the notes at its top for the environment variables it needs).

## License

MIT. See [LICENSE](LICENSE). `UnityToolset/UniVRM` holds the unchanged UniVRM 0.66.0 package
by the VRM Consortium, under its own MIT licences (included next to it).

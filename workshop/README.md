# Publishing to the Steam Workshop

The game has no in-game publish flow - it only *reads* Workshop items. Uploading
is done with MegaCrit's [mod uploader](https://github.com/megacrit/sts2-mod-uploader),
installed here as `C:\Users\Sean\sts2-workshop\ModUploader.exe`.

## Publishing an update

```
./sync.sh                                  # from WSL: build + refresh the workspace
```
then, from a Windows terminal in `C:\Users\Sean\sts2-workshop`:
```
.\ModUploader.exe upload -w RdpsMeter
```

Steam must be running and logged in as the account that owns the mod.
`mod-uploader.log` appears next to the exe if something fails.

## What lives where

This directory holds the parts worth versioning:

* `workshop.json` - the listing: title, description, visibility, change note,
  tags, dependencies, content descriptors. Fields set to `null` or removed keep
  whatever the Workshop page already has, so an update need only set
  `changeNote`.
* `image.png` - the store image. Required, must stay under 1MB. Currently a
  placeholder drawn by `make_preview.py`; replace it with a screenshot of the
  meter in a real fight, which sells the mod far better.
* `previews/` - optional extra screenshots, same size limit. Steam keys them by
  filename and deletes any that disappear, so this directory is either mirrored
  whole or left alone.
* `mod_id.txt` - written by the first upload and copied back here by `sync.sh`.
  **Keep it.** Without it an update publishes a second, unrelated Workshop item.

The workspace the uploader actually reads is `C:\Users\Sean\sts2-workshop\RdpsMeter`
- Windows-side because the uploader is a Windows exe talking to the Steam client.
`sync.sh` only ever copies into it, so nothing the uploader writes there is lost.

## Notes

* `visibility` starts at `private`. Flip it to `public` once you have subscribed
  to your own item and seen it load.
* Tags are left empty to be picked on the Steam page; `Tools & APIs` is reserved
  for mods that are tools or APIs.
* `minBranch` / `maxBranch` exist for restricting the mod to a game branch, but
  the uploader's own docs say they misbehave and to set them on the web instead.
  The manifest's `min_game_version` already refuses to load on older builds.
* A copy in the game's `mods/` folder and a subscribed copy of the same mod id
  collide; the game disables one, preferring the higher version. The local dev
  copy usually wins, so test the Workshop copy with `mods/` cleared.

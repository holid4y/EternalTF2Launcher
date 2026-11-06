## 2.2.6

Bug-fix release: sound previews, the self-updater, the screenshot editor and the server lists.

## Fixed

- GameBanana: opening submission pages no longer leaks memory; each opened page stayed subscribed to the preview player forever.
- GameBanana: a sound preview now returns to its play button when the sound finishes, and a network or audio-device failure while starting a preview no longer crashes the launcher.
- Updater: a failed update install no longer reopens the old or half-written launcher; it reopens only after the install succeeded.
- Screenshots editor: resizing a leftward or upward arrow no longer flips its direction, arrows can no longer be dragged off the picture, and text marks get a real selection frame and resize grip around the rendered text.
- Servers: the Steam master server list keeps the servers already fetched when a later page times out.
- GameBanana: embedded video links no longer produce a double slash when the base URL ends in one.
- Screenshots editor: stamped pictures keep their aspect ratio near the right/bottom edge, unreadable picture files show an error instead of crashing, and each picture is decoded once instead of twice.
- Launcher → Look: the background buttons bind directly to the chosen mode; dead code removed.

## Internal

- 12 new regression tests (366 total).

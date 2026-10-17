# SoundFont for MIDI music (development)

The client plays the original MIDI music through [MeltySynth](https://github.com/sinshu/meltysynth) (MIT) and needs a
General MIDI SoundFont (`.sf2`). **No SoundFont is committed or bundled** (ADR 0020). Without one, the viewer's Music
page shows why playback is disabled; everything else works.

## Getting one

```sh
tools/soundfont/fetch-soundfont.sh        # macOS / Linux
pwsh tools/soundfont/fetch-soundfont.ps1  # Windows
```

downloads **GeneralUser GS v2.0.3** by S. Christian Collins from its author's GitHub repository
([mrbumpy409/GeneralUser-GS](https://github.com/mrbumpy409/GeneralUser-GS), pinned commit, SHA-256 verified) into
`soundfonts/` at the repository root, which is git-ignored. The license file is saved next to it.

The client looks for a SoundFont in this order: `--soundfont <path>`, the path chosen with the Music page's
"SoundFont…" button (stored in `user://viewer.cfg`), the `TZAR2_SOUNDFONT` environment variable, then
`soundfonts/GeneralUser-GS.sf2` in the source checkout. Any General MIDI `.sf2` works (SF3/Ogg-compressed files are not
supported by MeltySynth), e.g. one emulating a Roland SC-55 for the late-90s sound.

## License

GeneralUser GS License v2.0 (full text in the downloaded `GeneralUser-GS-LICENSE.txt`): use without restriction for
music creation, private or commercial; free to use in software projects, to modify, and to redistribute. The author
notes that the provenance of some samples cannot be proven with certainty, which is why the project does **not** bundle
it in builds (TDD REQ-AUD-02: verify the license before bundling) and treats it as a development convenience only.
Requests from the author: do not hot-link his download files from websites (the script downloads from his GitHub
repository, which exists for automated packaging).

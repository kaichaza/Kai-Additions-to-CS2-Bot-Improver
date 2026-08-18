KaiBotTactics data folder. Schema version 2.

One file per map, named <mapname>.json, plus a raw sample bank under
learned/<mapname>.samples.json.

No map files are shipped. Coordinates are map specific and cannot be guessed;
a file full of plausible-looking numbers would put bots on positions they can
never reach, and the plugin would appear broken when the data is what is wrong.

Generate real ones instead:

  kai_learn on         (on by default)
  ...play rounds...
  kai_learn status     see how many samples are banked, and when
  kai_learn build      cluster them and write <mapname>.json

kai_learn build only runs during freezetime or warmup. It clears live
assignments and resets the retake director, so running it mid-round would
throw away that round's post-plant behaviour. Use "kai_learn build force" to
override if you really mean it.

Every write backs up the previous file first:

  <mapname>.json                -> <mapname>.json.backup
  <mapname>.samples.json        -> <mapname>.samples.json.backup

One rolling generation, not a timestamped series. The tactics file is always
regenerable from the sample bank; the sample bank is the irreplaceable one.

V1 sample banks are NOT read. Their samples have no timestamps, no engagement
ids, and were recorded before the airborne filter existed. A v1 file on disk is
left untouched but ignored; start the bank fresh.

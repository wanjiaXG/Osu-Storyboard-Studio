Preview rules follow the [osu!lazer source](https://github.com/ppy/osu) (reference only; no checkout or installed game is required to build or run):

- Skinning/LegacyBeatmapSkin and BeatmapSkinProvidingContainer: beatmap textures at 1x, .osu configuration and classic fallback for legacy beatmap resources. As requested, mania-specific skin.ini settings are honoured for beatmap skin previews as well.
- Rulesets/UI/Scrolling/{Sequential,Overlapping}ScrollAlgorithm, Rulesets/Timing/MultiplierControlPoint and Beatmap.GetMostCommonBeatLength: mania BPM normalisation; taiko per-object origin velocity.
- Mania/UI/{ManiaPlayfieldAdjustmentContainer,ManiaPlayfield,ColumnFlow,Stage} and Mania/Skinning/{Argon,Legacy}: centred Argon stages, native key/note sizing and hold tail orientation. Legacy placement follows stable Graphics/Skinning/SkinMania and GameModes/Play/Rulesets/Mania/Stage/StageMania: ColumnStart=136 from the game frame left, ColumnRight=19, stage separation and width fitting from skin.ini. Layout uses the game frame, independently of editor padding/zoom/pan.
- Taiko/UI/TaikoPlayfieldAdjustmentContainer, TaikoPlayfield, Objects/DrawableHit and Skinning/{Argon,Legacy}: screen-left layout, aspect limits, strong scale, native texture dimensions, ring widths and hit trajectory.
  The adjustment container's parent is the 640/854 by 480 game frame. Editor target padding, zoom and pan only transform that frame; they must not change Taiko's layout or scroll range.
- Catch/UI/CatchPlayfieldAdjustmentContainer, Catcher, Objects/Drawables and Skinning/{Argon,Legacy}: stable fall interval -100..340, catcher scale, sprite aspect ratio, droplets, banana colour/scale/rotation and hyperdash borders.

The Argon blob shader adapts the MIT-licensed osu!framework algorithm:
https://github.com/ppy/osu-framework/blob/master/osu.Framework/Graphics/UserInterface/CircularBlob.cs
https://github.com/ppy/osu-framework/blob/master/osu.Framework/Resources/Shaders/sh_CircularBlobUtils.h

Preview random visual seeds are fixed per object so scrubbing, exporting and switching skins remain reproducible. Lazer uses drawable-lifetime random seeds for these blob layers. This preview draws hit objects and playfield skin components; the full gameplay HUD, score judgements and fruit plate stacking are not implemented.

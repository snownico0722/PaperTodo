# Global parameter injection and Good Lock

[中文](USER_INJECTION.md) · [User guide](USER_GUIDE.en.md#84-installing-and-configuring-plugins) · [Plugin API](../plugin-samples/README.md#56-global-userjson-injection-api-24) · [Machine-readable catalog](user-injection-catalog.json)

PaperTodo's official injection interface uses one `user.json` beside the application. Every workspace shares this file while ordinary settings retain their original preferences. It exposes fixed values, coefficients in dynamic formulas, and selected intermediate results. Include only the keys you want to override.

## Edit and apply

1. Follow the [plugin installation steps](USER_GUIDE.en.md#84-installing-and-configuring-plugins) to install `plugins/official.good-lock.native/`, then select **Good Lock** as a note paper's body type. The plugin requires API 2.4 and is distributed separately from the application release.
2. Choose **Open file** and edit `user.json` in an external editor. The first explicit editor request creates an empty object; opening an existing file does not rewrite it.
3. Save and return to Good Lock. It rereads the file and lists modified entries, including running overrides still waiting to be removed. Choose **Apply** at the top right.
4. A red dot means the accepted change needs a restart. **Quick restart** first applies the page's draft, then uses normal save-and-exit and returns to the calling paper after startup.

You can also edit the same file without installing Good Lock and restart PaperTodo to adopt it. The application does not continuously watch the file; saving an external edit alone does not update running values.

```json
{
  "fullscreen.pollIntervalMs": 1500,
  "todo.drag.autoScroll": false,
  "capsule.labelFontDip": 15,
  "menu.paper.newTodo": false
}
```

Keys are case-sensitive; dots belong to the literal key name. The root must be an object. Comments and trailing commas are accepted. Use the listed number, integer or boolean type rather than a quoted string. A missing file, a blank file or `{}` means no file overrides.

**Presence defines an override.** A value equal to the current default is still explicit. Remove the key and apply to restore the original source; removing a restart-bound key also requires a restart. Ordinary Settings page resets restore that page's preferences while retaining `user.json`.

## Read the page

| Element | Meaning |
| --- | --- |
| Default on the left | A fixed value, a short description of the calculation, or no value. It need not be a constant that can be copied into JSON. |
| Modified value on the right | The file value or the page's unapplied draft. Hover the title for the full key. |
| Gray dot | A file or draft change is pending. Unknown keys remain unadopted. |
| Green dot | The running process has adopted the value. A `next_use` consumer reads it at its next applicable operation or construction. |
| Red dot | Apply accepted the request, but changing the current value requires a restart. |
| Red modified number | The value is outside its declared advisory range. It is still accepted; this is not a write limit. |
| Per-item reset | Draft removal of that key, written by Apply. |
| Wand | An explicit request for a limited suggestion affecting this item. A suggestion becomes a draft; no applicable rule leaves the value unchanged. |
| Reset all | Draft removal of all global overrides, including unknown keys when applied. |
| Save-as icon | Export the latest file plus the current draft as sparse JSON without adopting it or replacing the active file. |

If another editor changes the file before a draft is submitted, Good Lock rereads it, retains the per-key draft and asks you to inspect it before another Apply. Adopting an external file alone preserves its formatting. Applying reset or suggestion edits reformats the JSON object while retaining other keys and values.

## Dynamic formulas and ordinary settings

For example, `capsule.labelBaseDip` changes an input to the label-size calculation, so the current size preset still participates. `capsule.labelFontDip` replaces its final result. Without that final override, the original formula continues using current settings. Preview dimensions, scroll edge zones, scroll speed and typography metrics expose similar selected result points.

An ordinary setting whose result is completely superseded becomes read-only while retaining its original choice. Removing and applying the override restores that choice. Overriding a coefficient or only some note/todo metrics leaves ordinary settings able to affect the remaining calculation. Global `user.json` is shared across workspaces, including secondary workspaces that follow main settings.

New-paper dimensions apply to the new model and first display through the common GUI/plugin/MCP creation path; existing papers are unaffected. Later restoration has a separate `window.restore.constrainSize` switch. Window minimum dimensions and existing layout rules retain their own meanings.

## Application timing and cost

| Catalog value | Meaning |
| --- | --- |
| `live` | Apply refreshes an existing cache or an existing runtime step reads the value, including necessary timer and predictor-history updates. |
| `next_use` | Apply binds the slot for its next object construction, operation or read. An already-started animation or object is not automatically rebuilt. |
| `restart` | The current process retains the old binding. Startup adopts the value before constructing the controller and windows. |

Fixed parameters read typed in-memory fields. Dynamic results retain the original formula and add an override branch. Reflection, file access, keyed lookup and catalog localization occur at startup, inspection or Apply boundaries. There is no file watcher, additional polling or per-frame dictionary scan. Unconfigured startup does not build the reflection catalog. Slots still have one-time initialization and small read costs; absolute zero overhead is not promised.

Larger sample, content, cache or image budgets change the associated algorithm's workload. The official wand provides limited suggestions, including queue snap/release relationships, compression versus stored-image dimensions, and a rough capsule-font/body-height relationship. It does not automatically tune every related value or guarantee freedom from clipping, stalls, crashes or data damage. Users own the effects of custom values; injection adds no automatic recovery or rollback system.

Existing default preview layout and viewport clipping are preserved. A `maximum` is an upper bound and a `minimum` is a lower bound; final-result overrides are identified in their descriptions. Raising a maximum does not force every preview to use it. Built-in host capacity and preload budgets follow the relevant maximum/final dimension slots.

## Agent and plugin use

Native Body and provider Runtime contexts expose `context.InjectionApi`. Call `Read()` for the installed version's actual catalog and latest file revision. Use `Apply()` to adopt an external edit, or submit a revision with `Set` and `Remove` for per-key edits. `Read` / `Suggest` require `settings.read`; `Apply` / `Restart` require `settings.update`. Body restart returns to the calling paper; Runtime has no paper return target.

The optional draft map supplied to `Suggest` is the entire proposed override document: an omitted key restores its original source. A suggestion returns only the named replacement and explanation without writing anything. See the [plugin API examples](../plugin-samples/README.md#56-global-userjson-injection-api-24). Internal injection keys may evolve with their modules. Agents should inspect the current catalog, retain unknown keys and write sparse overrides.

## Complete parameter catalog

The following tables are exported from the typed catalog. The machine-readable catalog supplies the same keys, types, defaults, lifecycle, advisory ranges and Chinese/English descriptions. Select the required `id` and value when editing `user.json`.

There are **434 entries**, including **45 dynamic default sources**; `live` 168, `next_use` 89, `restart` 177.

### Overall appearance (1)

| Key / title | Type | Default / calculation | When | Description |
| --- | --- | --- | --- | --- |
| `appearance.fontScale` · Overall font scale | `number` | Calculated from the current settings or runtime state | `live` | Overrides the current font scale while preserving the choice in ordinary settings. Advisory: 0.8–1.2. |

### Animation (2)

| Key / title | Type | Default / calculation | When | Description |
| --- | --- | --- | --- | --- |
| `animation.fastDurationScale` · Fast animation duration multiplier | `number` | 0.6 | `next_use` | Multiplier applied to the existing duration in fast animation mode. Advisory: 0–1. |
| `animation.snapAmplitude` · Spring curve amplitude | `number` | 0.3 | `restart` | Amplitude used when constructing the shared spring animation curve. |

### Saving and display events (3)

| Key / title | Type | Default / calculation | When | Description |
| --- | --- | --- | --- | --- |
| `app.displayRefreshMs` · Display change debounce delay | `number` | 250 ms | `live` | Interval for this timer or throttle; other timer intervals stay unchanged. Advisory: ≥ 1. |
| `app.saveForceMs` · Forced save interval during continuous editing | `number` | 10000 ms | `live` | Interval for this timer or throttle; other timer intervals stay unchanged. Advisory: ≥ 1. |
| `app.saveIdleMs` · Save delay after editing stops | `number` | 1000 ms | `live` | Interval for this timer or throttle; other timer intervals stay unchanged. Advisory: ≥ 1. |

### Paper background (2)

| Key / title | Type | Default / calculation | When | Description |
| --- | --- | --- | --- | --- |
| `background.blendedImageOpacity` · Blended background image opacity | `number` | 0.3 | `restart` | Image brush opacity when blending with the background color is enabled. |
| `background.maxDecodeDimensionPx` · Background image decode dimension | `integer` | 4096 px | `restart` | Maximum pixel dimension used when decoding a background image. |

### Capsule text, dimensions and gaps (15)

| Key / title | Type | Default / calculation | When | Description |
| --- | --- | --- | --- | --- |
| `capsule.edge.widthDip` · Final edge capsule width | `number` | Computed from the icon, title or plugin content | `restart` | Replace the measured edge capsule width, including the interior shadow margin. |
| `capsule.gap.baseDip` · Capsule base gap | `number` | 4 DIP | `live` | Base gap for the standard preset; narrow and wide still use the preset delta. |
| `capsule.gap.deltaDip` · Capsule gap preset delta | `number` | 4 DIP | `live` | Narrow subtracts this value from the base gap; wide adds it. |
| `capsule.gap.finalDip` · Final capsule gap | `number` | Computed from the current gap preset | `live` | Replace the result computed from the current gap preset and base values. |
| `capsule.iconBaseDip` · Capsule icon base font size | `number` | 13 DIP | `live` | Base font size before applying the current preset and global scale. |
| `capsule.labelBaseDip` · Capsule label base font size | `number` | 12 DIP | `live` | Base font size before applying the current preset and global scale. |
| `capsule.labelFontDip` · Final capsule label font size | `number` | Calculated from the current settings or runtime state | `live` | Uses the existing text, content or scale calculation until an override replaces its result. |
| `capsule.master.glyphBaseDip` · Master capsule arrow base font size | `number` | 12 DIP | `live` | Base font size before applying the current preset and global scale. |
| `capsule.master.glyphGapDip` · Master capsule icon-to-count gap | `number` | 3 DIP | `restart` | Dimension shared by master capsule content measurement and display. |
| `capsule.master.interiorBorderThicknessDip` · Master capsule inner border thickness | `number` | 1 DIP | `restart` | Dimension shared by master capsule content measurement and display. |
| `capsule.master.labelFontDip` · Final master capsule count font size | `number` | Calculated from the current settings or runtime state | `live` | Uses the existing text, content or scale calculation until an override replaces its result. |
| `capsule.master.leftPaddingDip` · Master capsule left padding | `number` | 4 DIP | `restart` | Dimension shared by master capsule content measurement and display. |
| `capsule.master.rightPaddingDip` · Master capsule right padding | `number` | 3 DIP | `restart` | Dimension shared by master capsule content measurement and display. |
| `capsule.master.widthDip` · Final master capsule width | `number` | Calculated from the current settings or runtime state | `restart` | Uses the existing text, content or scale calculation until an override replaces its result. |
| `capsule.normal.widthDip` · Final ordinary capsule width | `number` | Calculated from the current settings or runtime state | `restart` | Uses the existing text, content or scale calculation until an override replaces its result. |

### Edge capsules, hover prediction and previews (205)

| Key / title | Type | Default / calculation | When | Description |
| --- | --- | --- | --- | --- |
| `edge.hover.acceleratingVetoHorizonScale` · Accelerating pass-through veto multiplier | `number` | 1.2 | `live` | Coefficient in the existing hover-intent formula, read by the next evaluation. |
| `edge.hover.accelerationDeltaDipPerMs` · Hover acceleration speed difference | `number` | 0.04 DIP/ms | `live` | Absolute speed difference also required to identify an acceleration trend. |
| `edge.hover.accelerationRatio` · Hover acceleration speed ratio | `number` | 1.15 | `live` | Ratio used to compare recent vertical speed with the preceding speed when detecting acceleration. |
| `edge.hover.accelerationRiskIncrement` · Acceleration dwell-risk increment | `number` | 0.2 | `live` | Coefficient in the existing hover-intent formula, read by the next evaluation. Advisory: 0–1. |
| `edge.hover.brakingDeltaDipPerMs` · Hover braking speed difference | `number` | 0.05 DIP/ms | `live` | Absolute speed difference also required to identify a braking trend. |
| `edge.hover.brakingRatio` · Hover braking speed ratio | `number` | 0.72 | `live` | Ratio used to compare recent vertical speed with the preceding speed when detecting braking. Advisory: 0–1. |
| `edge.hover.brakingRiskScale` · Braking dwell-risk multiplier | `number` | 0.25 | `live` | Coefficient in the existing hover-intent formula, read by the next evaluation. Advisory: 0–1. |
| `edge.hover.corridorGeometryWeight` · Gap-crossing geometry weight | `number` | 0.6 | `live` | Weight used when combining geometry and motion evidence in the empty region between capsules. Advisory: 0–1. |
| `edge.hover.corridorKeepEnterConfidence` · Gap-crossing keep-alive entry threshold | `number` | 0.58 | `live` | Combined confidence needed before the keep-alive state has been latched. Advisory: 0–1. |
| `edge.hover.corridorKeepExitConfidence` · Gap-crossing keep-alive exit threshold | `number` | 0.42 | `live` | Combined confidence needed to retain an already latched keep-alive state. Advisory: 0–1. |
| `edge.hover.corridorMeaningfulMovementDip` · Gap-crossing meaningful movement | `number` | 0.5 DIP | `live` | Distance required to update the timestamp of the last meaningful pointer movement. |
| `edge.hover.corridorMotionFadeMs` · Gap-crossing confidence fade horizon | `number` | 900 ms | `live` | Motion confidence finishes fading when the estimated target arrival time reaches this value. |
| `edge.hover.corridorMotionFullConfidenceMs` · Gap-crossing full-confidence arrival horizon | `number` | 250 ms | `live` | Motion confidence remains full when the estimated target arrival time is within this horizon. |
| `edge.hover.corridorMotionWeight` · Gap-crossing motion weight | `number` | 0.4 | `live` | Weight used when combining geometry and motion evidence in the empty region between capsules. Advisory: 0–1. |
| `edge.hover.corridorStationaryGraceMs` · Gap-crossing stationary geometry grace | `number` | 160 ms | `live` | Time after pointer motion stops during which geometry evidence retains its original strength. |
| `edge.hover.corridorStrongConfidence` · Gap-crossing strong-evidence threshold | `number` | 0.84 | `live` | Confidence at which a single type of evidence is enough to retain browsing intent across a gap. Advisory: 0–1. |
| `edge.hover.duplicateSampleMs` · Hover duplicate sample interval | `number` | 1.5 ms | `live` | Interval used to merge repeated samples at the same position. |
| `edge.hover.historyWindowMs` · Hover motion history window | `number` | 64 ms | `live` | Time window used to estimate recent motion and changes in speed. |
| `edge.hover.profile.high.corridor.minimumPathConsistency` · Hover High: Gap crossing: path consistency | `number` | 0.68 | `live` | Apply rebuilds the cached sensitivity profile; subsequent intent evaluations use this value. Advisory: 0–1. |
| `edge.hover.profile.high.corridor.minimumSpeedDipPerMs` · Hover High: Gap crossing: minimum speed | `number` | 0.075 DIP/ms | `live` | Apply rebuilds the cached sensitivity profile; subsequent intent evaluations use this value. |
| `edge.hover.profile.high.corridor.noTargetIntentCloseMs` · Hover High: Gap crossing: close delay without target intent | `number` | 350 ms | `live` | Apply rebuilds the cached sensitivity profile; subsequent intent evaluations use this value. |
| `edge.hover.profile.high.corridor.targetPaddingDip` · Hover High: Gap crossing: target geometry padding | `number` | 16 DIP | `live` | Apply rebuilds the cached sensitivity profile; subsequent intent evaluations use this value. |
| `edge.hover.profile.high.initial.dynamicDelayHorizonMs` · Hover High: Initial entry: adaptive dwell risk horizon | `number` | 180 ms | `live` | Apply rebuilds the cached sensitivity profile; subsequent intent evaluations use this value. |
| `edge.hover.profile.high.initial.maximumDelayMs` · Hover High: Initial entry: maximum adaptive dwell | `number` | 32 ms | `live` | Apply rebuilds the cached sensitivity profile; subsequent intent evaluations use this value. |
| `edge.hover.profile.high.initial.minimumDelayMs` · Hover High: Initial entry: minimum adaptive dwell | `number` | 8 ms | `live` | Apply rebuilds the cached sensitivity profile; subsequent intent evaluations use this value. |
| `edge.hover.profile.high.initial.minimumObservationMs` · Hover High: Initial entry: minimum observation | `number` | 8 ms | `live` | Apply rebuilds the cached sensitivity profile; subsequent intent evaluations use this value. |
| `edge.hover.profile.high.initial.passThroughVetoHorizonMs` · Hover High: Initial entry: pass-through veto horizon | `number` | 80 ms | `live` | Apply rebuilds the cached sensitivity profile; subsequent intent evaluations use this value. |
| `edge.hover.profile.high.minimumDirectionalSpeedDipPerMs` · Hover High: Minimum directional speed | `number` | 0.1 DIP/ms | `live` | Apply rebuilds the cached sensitivity profile; subsequent intent evaluations use this value. |
| `edge.hover.profile.high.minimumDirectionConsistency` · Hover High: Minimum direction consistency | `number` | 0.72 | `live` | Apply rebuilds the cached sensitivity profile; subsequent intent evaluations use this value. Advisory: 0–1. |
| `edge.hover.profile.high.minimumVerticalDominance` · Hover High: Minimum vertical dominance | `number` | 0.55 | `live` | Apply rebuilds the cached sensitivity profile; subsequent intent evaluations use this value. Advisory: 0–1. |
| `edge.hover.profile.high.stableFallbackMs` · Hover High: Stable dwell bypass time | `number` | 50 ms | `live` | Apply rebuilds the cached sensitivity profile; subsequent intent evaluations use this value. |
| `edge.hover.profile.high.transfer.dynamicDelayHorizonMs` · Hover High: Transfer: adaptive dwell risk horizon | `number` | 240 ms | `live` | Apply rebuilds the cached sensitivity profile; subsequent intent evaluations use this value. |
| `edge.hover.profile.high.transfer.maximumDelayMs` · Hover High: Transfer: maximum adaptive dwell | `number` | 50 ms | `live` | Apply rebuilds the cached sensitivity profile; subsequent intent evaluations use this value. |
| `edge.hover.profile.high.transfer.minimumDelayMs` · Hover High: Transfer: minimum adaptive dwell | `number` | 12 ms | `live` | Apply rebuilds the cached sensitivity profile; subsequent intent evaluations use this value. |
| `edge.hover.profile.high.transfer.minimumObservationMs` · Hover High: Transfer: minimum observation | `number` | 12 ms | `live` | Apply rebuilds the cached sensitivity profile; subsequent intent evaluations use this value. |
| `edge.hover.profile.high.transfer.passThroughVetoHorizonMs` · Hover High: Transfer: pass-through veto horizon | `number` | 120 ms | `live` | Apply rebuilds the cached sensitivity profile; subsequent intent evaluations use this value. |
| `edge.hover.profile.low.corridor.minimumPathConsistency` · Hover Low: Gap crossing: path consistency | `number` | 0.79 | `live` | Apply rebuilds the cached sensitivity profile; subsequent intent evaluations use this value. Advisory: 0–1. |
| `edge.hover.profile.low.corridor.minimumSpeedDipPerMs` · Hover Low: Gap crossing: minimum speed | `number` | 0.11 DIP/ms | `live` | Apply rebuilds the cached sensitivity profile; subsequent intent evaluations use this value. |
| `edge.hover.profile.low.corridor.noTargetIntentCloseMs` · Hover Low: Gap crossing: close delay without target intent | `number` | 650 ms | `live` | Apply rebuilds the cached sensitivity profile; subsequent intent evaluations use this value. |
| `edge.hover.profile.low.corridor.targetPaddingDip` · Hover Low: Gap crossing: target geometry padding | `number` | 24 DIP | `live` | Apply rebuilds the cached sensitivity profile; subsequent intent evaluations use this value. |
| `edge.hover.profile.low.initial.dynamicDelayHorizonMs` · Hover Low: Initial entry: adaptive dwell risk horizon | `number` | 240 ms | `live` | Apply rebuilds the cached sensitivity profile; subsequent intent evaluations use this value. |
| `edge.hover.profile.low.initial.maximumDelayMs` · Hover Low: Initial entry: maximum adaptive dwell | `number` | 44 ms | `live` | Apply rebuilds the cached sensitivity profile; subsequent intent evaluations use this value. |
| `edge.hover.profile.low.initial.minimumDelayMs` · Hover Low: Initial entry: minimum adaptive dwell | `number` | 14 ms | `live` | Apply rebuilds the cached sensitivity profile; subsequent intent evaluations use this value. |
| `edge.hover.profile.low.initial.minimumObservationMs` · Hover Low: Initial entry: minimum observation | `number` | 10 ms | `live` | Apply rebuilds the cached sensitivity profile; subsequent intent evaluations use this value. |
| `edge.hover.profile.low.initial.passThroughVetoHorizonMs` · Hover Low: Initial entry: pass-through veto horizon | `number` | 110 ms | `live` | Apply rebuilds the cached sensitivity profile; subsequent intent evaluations use this value. |
| `edge.hover.profile.low.minimumDirectionalSpeedDipPerMs` · Hover Low: Minimum directional speed | `number` | 0.055 DIP/ms | `live` | Apply rebuilds the cached sensitivity profile; subsequent intent evaluations use this value. |
| `edge.hover.profile.low.minimumDirectionConsistency` · Hover Low: Minimum direction consistency | `number` | 0.64 | `live` | Apply rebuilds the cached sensitivity profile; subsequent intent evaluations use this value. Advisory: 0–1. |
| `edge.hover.profile.low.minimumVerticalDominance` · Hover Low: Minimum vertical dominance | `number` | 0.45 | `live` | Apply rebuilds the cached sensitivity profile; subsequent intent evaluations use this value. Advisory: 0–1. |
| `edge.hover.profile.low.stableFallbackMs` · Hover Low: Stable dwell bypass time | `number` | 85 ms | `live` | Apply rebuilds the cached sensitivity profile; subsequent intent evaluations use this value. |
| `edge.hover.profile.low.transfer.dynamicDelayHorizonMs` · Hover Low: Transfer: adaptive dwell risk horizon | `number` | 410 ms | `live` | Apply rebuilds the cached sensitivity profile; subsequent intent evaluations use this value. |
| `edge.hover.profile.low.transfer.maximumDelayMs` · Hover Low: Transfer: maximum adaptive dwell | `number` | 90 ms | `live` | Apply rebuilds the cached sensitivity profile; subsequent intent evaluations use this value. |
| `edge.hover.profile.low.transfer.minimumDelayMs` · Hover Low: Transfer: minimum adaptive dwell | `number` | 34 ms | `live` | Apply rebuilds the cached sensitivity profile; subsequent intent evaluations use this value. |
| `edge.hover.profile.low.transfer.minimumObservationMs` · Hover Low: Transfer: minimum observation | `number` | 18 ms | `live` | Apply rebuilds the cached sensitivity profile; subsequent intent evaluations use this value. |
| `edge.hover.profile.low.transfer.passThroughVetoHorizonMs` · Hover Low: Transfer: pass-through veto horizon | `number` | 205 ms | `live` | Apply rebuilds the cached sensitivity profile; subsequent intent evaluations use this value. |
| `edge.hover.profile.medium.corridor.minimumPathConsistency` · Hover Medium: Gap crossing: path consistency | `number` | 0.74 | `live` | Apply rebuilds the cached sensitivity profile; subsequent intent evaluations use this value. Advisory: 0–1. |
| `edge.hover.profile.medium.corridor.minimumSpeedDipPerMs` · Hover Medium: Gap crossing: minimum speed | `number` | 0.09 DIP/ms | `live` | Apply rebuilds the cached sensitivity profile; subsequent intent evaluations use this value. |
| `edge.hover.profile.medium.corridor.noTargetIntentCloseMs` · Hover Medium: Gap crossing: close delay without target intent | `number` | 500 ms | `live` | Apply rebuilds the cached sensitivity profile; subsequent intent evaluations use this value. |
| `edge.hover.profile.medium.corridor.targetPaddingDip` · Hover Medium: Gap crossing: target geometry padding | `number` | 20 DIP | `live` | Apply rebuilds the cached sensitivity profile; subsequent intent evaluations use this value. |
| `edge.hover.profile.medium.initial.dynamicDelayHorizonMs` · Hover Medium: Initial entry: adaptive dwell risk horizon | `number` | 200 ms | `live` | Apply rebuilds the cached sensitivity profile; subsequent intent evaluations use this value. |
| `edge.hover.profile.medium.initial.maximumDelayMs` · Hover Medium: Initial entry: maximum adaptive dwell | `number` | 36 ms | `live` | Apply rebuilds the cached sensitivity profile; subsequent intent evaluations use this value. |
| `edge.hover.profile.medium.initial.minimumDelayMs` · Hover Medium: Initial entry: minimum adaptive dwell | `number` | 10 ms | `live` | Apply rebuilds the cached sensitivity profile; subsequent intent evaluations use this value. |
| `edge.hover.profile.medium.initial.minimumObservationMs` · Hover Medium: Initial entry: minimum observation | `number` | 8 ms | `live` | Apply rebuilds the cached sensitivity profile; subsequent intent evaluations use this value. |
| `edge.hover.profile.medium.initial.passThroughVetoHorizonMs` · Hover Medium: Initial entry: pass-through veto horizon | `number` | 90 ms | `live` | Apply rebuilds the cached sensitivity profile; subsequent intent evaluations use this value. |
| `edge.hover.profile.medium.minimumDirectionalSpeedDipPerMs` · Hover Medium: Minimum directional speed | `number` | 0.075 DIP/ms | `live` | Apply rebuilds the cached sensitivity profile; subsequent intent evaluations use this value. |
| `edge.hover.profile.medium.minimumDirectionConsistency` · Hover Medium: Minimum direction consistency | `number` | 0.68 | `live` | Apply rebuilds the cached sensitivity profile; subsequent intent evaluations use this value. Advisory: 0–1. |
| `edge.hover.profile.medium.minimumVerticalDominance` · Hover Medium: Minimum vertical dominance | `number` | 0.5 | `live` | Apply rebuilds the cached sensitivity profile; subsequent intent evaluations use this value. Advisory: 0–1. |
| `edge.hover.profile.medium.stableFallbackMs` · Hover Medium: Stable dwell bypass time | `number` | 60 ms | `live` | Apply rebuilds the cached sensitivity profile; subsequent intent evaluations use this value. |
| `edge.hover.profile.medium.transfer.dynamicDelayHorizonMs` · Hover Medium: Transfer: adaptive dwell risk horizon | `number` | 310 ms | `live` | Apply rebuilds the cached sensitivity profile; subsequent intent evaluations use this value. |
| `edge.hover.profile.medium.transfer.maximumDelayMs` · Hover Medium: Transfer: maximum adaptive dwell | `number` | 66 ms | `live` | Apply rebuilds the cached sensitivity profile; subsequent intent evaluations use this value. |
| `edge.hover.profile.medium.transfer.minimumDelayMs` · Hover Medium: Transfer: minimum adaptive dwell | `number` | 20 ms | `live` | Apply rebuilds the cached sensitivity profile; subsequent intent evaluations use this value. |
| `edge.hover.profile.medium.transfer.minimumObservationMs` · Hover Medium: Transfer: minimum observation | `number` | 14 ms | `live` | Apply rebuilds the cached sensitivity profile; subsequent intent evaluations use this value. |
| `edge.hover.profile.medium.transfer.passThroughVetoHorizonMs` · Hover Medium: Transfer: pass-through veto horizon | `number` | 155 ms | `live` | Apply rebuilds the cached sensitivity profile; subsequent intent evaluations use this value. |
| `edge.hover.profile.veryHigh.corridor.minimumPathConsistency` · Hover Very high: Gap crossing: path consistency | `number` | 0.62 | `live` | Apply rebuilds the cached sensitivity profile; subsequent intent evaluations use this value. Advisory: 0–1. |
| `edge.hover.profile.veryHigh.corridor.minimumSpeedDipPerMs` · Hover Very high: Gap crossing: minimum speed | `number` | 0.06 DIP/ms | `live` | Apply rebuilds the cached sensitivity profile; subsequent intent evaluations use this value. |
| `edge.hover.profile.veryHigh.corridor.noTargetIntentCloseMs` · Hover Very high: Gap crossing: close delay without target intent | `number` | 200 ms | `live` | Apply rebuilds the cached sensitivity profile; subsequent intent evaluations use this value. |
| `edge.hover.profile.veryHigh.corridor.targetPaddingDip` · Hover Very high: Gap crossing: target geometry padding | `number` | 12 DIP | `live` | Apply rebuilds the cached sensitivity profile; subsequent intent evaluations use this value. |
| `edge.hover.profile.veryHigh.initial.dynamicDelayHorizonMs` · Hover Very high: Initial entry: adaptive dwell risk horizon | `number` | 135 ms | `live` | Apply rebuilds the cached sensitivity profile; subsequent intent evaluations use this value. |
| `edge.hover.profile.veryHigh.initial.maximumDelayMs` · Hover Very high: Initial entry: maximum adaptive dwell | `number` | 22 ms | `live` | Apply rebuilds the cached sensitivity profile; subsequent intent evaluations use this value. |
| `edge.hover.profile.veryHigh.initial.minimumDelayMs` · Hover Very high: Initial entry: minimum adaptive dwell | `number` | 4 ms | `live` | Apply rebuilds the cached sensitivity profile; subsequent intent evaluations use this value. |
| `edge.hover.profile.veryHigh.initial.minimumObservationMs` · Hover Very high: Initial entry: minimum observation | `number` | 6 ms | `live` | Apply rebuilds the cached sensitivity profile; subsequent intent evaluations use this value. |
| `edge.hover.profile.veryHigh.initial.passThroughVetoHorizonMs` · Hover Very high: Initial entry: pass-through veto horizon | `number` | 55 ms | `live` | Apply rebuilds the cached sensitivity profile; subsequent intent evaluations use this value. |
| `edge.hover.profile.veryHigh.minimumDirectionalSpeedDipPerMs` · Hover Very high: Minimum directional speed | `number` | 0.13 DIP/ms | `live` | Apply rebuilds the cached sensitivity profile; subsequent intent evaluations use this value. |
| `edge.hover.profile.veryHigh.minimumDirectionConsistency` · Hover Very high: Minimum direction consistency | `number` | 0.76 | `live` | Apply rebuilds the cached sensitivity profile; subsequent intent evaluations use this value. Advisory: 0–1. |
| `edge.hover.profile.veryHigh.minimumVerticalDominance` · Hover Very high: Minimum vertical dominance | `number` | 0.6 | `live` | Apply rebuilds the cached sensitivity profile; subsequent intent evaluations use this value. Advisory: 0–1. |
| `edge.hover.profile.veryHigh.stableFallbackMs` · Hover Very high: Stable dwell bypass time | `number` | 38 ms | `live` | Apply rebuilds the cached sensitivity profile; subsequent intent evaluations use this value. |
| `edge.hover.profile.veryHigh.transfer.dynamicDelayHorizonMs` · Hover Very high: Transfer: adaptive dwell risk horizon | `number` | 190 ms | `live` | Apply rebuilds the cached sensitivity profile; subsequent intent evaluations use this value. |
| `edge.hover.profile.veryHigh.transfer.maximumDelayMs` · Hover Very high: Transfer: maximum adaptive dwell | `number` | 38 ms | `live` | Apply rebuilds the cached sensitivity profile; subsequent intent evaluations use this value. |
| `edge.hover.profile.veryHigh.transfer.minimumDelayMs` · Hover Very high: Transfer: minimum adaptive dwell | `number` | 8 ms | `live` | Apply rebuilds the cached sensitivity profile; subsequent intent evaluations use this value. |
| `edge.hover.profile.veryHigh.transfer.minimumObservationMs` · Hover Very high: Transfer: minimum observation | `number` | 10 ms | `live` | Apply rebuilds the cached sensitivity profile; subsequent intent evaluations use this value. |
| `edge.hover.profile.veryHigh.transfer.passThroughVetoHorizonMs` · Hover Very high: Transfer: pass-through veto horizon | `number` | 90 ms | `live` | Apply rebuilds the cached sensitivity profile; subsequent intent evaluations use this value. |
| `edge.hover.profile.veryLow.corridor.minimumPathConsistency` · Hover Very low: Gap crossing: path consistency | `number` | 0.84 | `live` | Apply rebuilds the cached sensitivity profile; subsequent intent evaluations use this value. Advisory: 0–1. |
| `edge.hover.profile.veryLow.corridor.minimumSpeedDipPerMs` · Hover Very low: Gap crossing: minimum speed | `number` | 0.14 DIP/ms | `live` | Apply rebuilds the cached sensitivity profile; subsequent intent evaluations use this value. |
| `edge.hover.profile.veryLow.corridor.noTargetIntentCloseMs` · Hover Very low: Gap crossing: close delay without target intent | `number` | 800 ms | `live` | Apply rebuilds the cached sensitivity profile; subsequent intent evaluations use this value. |
| `edge.hover.profile.veryLow.corridor.targetPaddingDip` · Hover Very low: Gap crossing: target geometry padding | `number` | 30 DIP | `live` | Apply rebuilds the cached sensitivity profile; subsequent intent evaluations use this value. |
| `edge.hover.profile.veryLow.initial.dynamicDelayHorizonMs` · Hover Very low: Initial entry: adaptive dwell risk horizon | `number` | 300 ms | `live` | Apply rebuilds the cached sensitivity profile; subsequent intent evaluations use this value. |
| `edge.hover.profile.veryLow.initial.maximumDelayMs` · Hover Very low: Initial entry: maximum adaptive dwell | `number` | 54 ms | `live` | Apply rebuilds the cached sensitivity profile; subsequent intent evaluations use this value. |
| `edge.hover.profile.veryLow.initial.minimumDelayMs` · Hover Very low: Initial entry: minimum adaptive dwell | `number` | 18 ms | `live` | Apply rebuilds the cached sensitivity profile; subsequent intent evaluations use this value. |
| `edge.hover.profile.veryLow.initial.minimumObservationMs` · Hover Very low: Initial entry: minimum observation | `number` | 12 ms | `live` | Apply rebuilds the cached sensitivity profile; subsequent intent evaluations use this value. |
| `edge.hover.profile.veryLow.initial.passThroughVetoHorizonMs` · Hover Very low: Initial entry: pass-through veto horizon | `number` | 135 ms | `live` | Apply rebuilds the cached sensitivity profile; subsequent intent evaluations use this value. |
| `edge.hover.profile.veryLow.minimumDirectionalSpeedDipPerMs` · Hover Very low: Minimum directional speed | `number` | 0.04 DIP/ms | `live` | Apply rebuilds the cached sensitivity profile; subsequent intent evaluations use this value. |
| `edge.hover.profile.veryLow.minimumDirectionConsistency` · Hover Very low: Minimum direction consistency | `number` | 0.6 | `live` | Apply rebuilds the cached sensitivity profile; subsequent intent evaluations use this value. Advisory: 0–1. |
| `edge.hover.profile.veryLow.minimumVerticalDominance` · Hover Very low: Minimum vertical dominance | `number` | 0.4 | `live` | Apply rebuilds the cached sensitivity profile; subsequent intent evaluations use this value. Advisory: 0–1. |
| `edge.hover.profile.veryLow.stableFallbackMs` · Hover Very low: Stable dwell bypass time | `number` | 110 ms | `live` | Apply rebuilds the cached sensitivity profile; subsequent intent evaluations use this value. |
| `edge.hover.profile.veryLow.transfer.dynamicDelayHorizonMs` · Hover Very low: Transfer: adaptive dwell risk horizon | `number` | 500 ms | `live` | Apply rebuilds the cached sensitivity profile; subsequent intent evaluations use this value. |
| `edge.hover.profile.veryLow.transfer.maximumDelayMs` · Hover Very low: Transfer: maximum adaptive dwell | `number` | 115 ms | `live` | Apply rebuilds the cached sensitivity profile; subsequent intent evaluations use this value. |
| `edge.hover.profile.veryLow.transfer.minimumDelayMs` · Hover Very low: Transfer: minimum adaptive dwell | `number` | 48 ms | `live` | Apply rebuilds the cached sensitivity profile; subsequent intent evaluations use this value. |
| `edge.hover.profile.veryLow.transfer.minimumObservationMs` · Hover Very low: Transfer: minimum observation | `number` | 22 ms | `live` | Apply rebuilds the cached sensitivity profile; subsequent intent evaluations use this value. |
| `edge.hover.profile.veryLow.transfer.passThroughVetoHorizonMs` · Hover Very low: Transfer: pass-through veto horizon | `number` | 255 ms | `live` | Apply rebuilds the cached sensitivity profile; subsequent intent evaluations use this value. |
| `edge.hover.sampleCapacity` · Hover pointer sample capacity | `integer` | 20 | `restart` | Length of the ring buffer allocated when the intent predictor is created. |
| `edge.hover.staleHistoryMs` · Hover history expiration | `number` | 180 ms | `live` | Do not continue using the old trajectory after a sample gap this long. |
| `edge.hover.stationaryGeometryConfidenceCap` · Stationary geometry confidence ceiling | `number` | 0.55 | `live` | Coefficient in the existing hover-intent formula, read by the next evaluation. Advisory: 0–1. |
| `edge.hover.uncertainTrendRiskScale` · Dwell-risk multiplier without a speed trend | `number` | 0.85 | `live` | Coefficient in the existing hover-intent formula, read by the next evaluation. Advisory: 0–1. |
| `edge.layout.capsuleCloseWidth` · Edge capsule close area width | `number` | 14 DIP | `restart` | Used by the existing shared edge capsule geometry or transition calculations. |
| `edge.layout.cornerRadius` · Edge capsule corner radius | `number` | 12 DIP | `restart` | Used by the existing shared edge capsule geometry or transition calculations. |
| `edge.layout.crossQueueDragUnlockDistance` · Distance to detach a capsule from its queue | `number` | 56 DIP | `next_use` | Used by the existing shared edge capsule geometry or transition calculations. |
| `edge.layout.dockingHandoffMilliseconds` · Capsule return-to-edge duration | `integer` | 160 ms | `next_use` | Used by the existing shared edge capsule geometry or transition calculations. |
| `edge.layout.dockingRevealMilliseconds` · Capsule reveal duration at the edge | `integer` | 80 ms | `next_use` | Used by the existing shared edge capsule geometry or transition calculations. |
| `edge.layout.expandedEdgeInset` · Expanded paper edge inset | `number` | 36 DIP | `restart` | Used by the existing shared edge capsule geometry or transition calculations. |
| `edge.layout.horizontalResizeMilliseconds` · Capsule width change duration | `integer` | 160 ms | `next_use` | Used by the existing shared edge capsule geometry or transition calculations. |
| `edge.layout.outlineOverlap` · Edge capsule outline overlap | `number` | 1 DIP | `restart` | Used by the existing shared edge capsule geometry or transition calculations. |
| `edge.layout.outlineThickness` · Edge capsule outline thickness | `number` | 2 DIP | `restart` | Used by the existing shared edge capsule geometry or transition calculations. |
| `edge.layout.slotMoveMilliseconds` · Queue movement duration | `integer` | 200 ms | `next_use` | Used by the existing shared edge capsule geometry or transition calculations. |
| `edge.layout.slotRetractMoveMilliseconds` · Queue retraction duration | `integer` | 120 ms | `next_use` | Used by the existing shared edge capsule geometry or transition calculations. |
| `edge.layout.startTopMargin` · New queue starting position | `number` | 48 DIP | `restart` | Used by the existing shared edge capsule geometry or transition calculations. |
| `edge.layout.topMargin` · Edge queue vertical margin | `number` | 8 DIP | `restart` | Used by the existing shared edge capsule geometry or transition calculations. |
| `edge.layout.windowChromeMargin` · Capsule transparent outer margin | `number` | 8 DIP | `restart` | Used by the existing shared edge capsule geometry or transition calculations. |
| `edge.preview.activation.activationTrackingIntervalMs` · Preview activation intent check interval | `number` | 16 ms | `next_use` | Read by the existing preview-intent and timer paths. Apply cancels pending old waits; subsequent scheduling uses the new values. |
| `edge.preview.activation.corridorTrackingIntervalMs` · Gap-crossing preview check interval | `number` | 24 ms | `next_use` | Read by the existing preview-intent and timer paths. Apply cancels pending old waits; subsequent scheduling uses the new values. |
| `edge.preview.activation.cursorReadRetryCount` · Preview physical pointer read retries | `integer` | 1 | `next_use` | Read by the existing preview-intent and timer paths. Apply cancels pending old waits; subsequent scheduling uses the new values. |
| `edge.preview.activation.layoutSuppressionMarginMs` · Extra preview suppression after queue movement | `number` | 50 ms | `live` | Added to the current queue slot movement animation duration. |
| `edge.preview.activation.layoutSuppressionMs` · Final preview suppression after queue movement | `number` | Current queue slot movement animation duration plus the extra suppression time. | `live` | Current queue slot movement animation duration plus the extra suppression time. |
| `edge.preview.activation.pointerToleranceDip` · Preview pointer stability tolerance | `number` | 2 DIP | `live` | Read by the existing preview-intent and timer paths. Apply cancels pending old waits; subsequent scheduling uses the new values. |
| `edge.preview.activation.postDragResidenceMs` · Post-drag preview minimum dwell | `number` | 140 ms | `live` | Read by the existing preview-intent and timer paths. Apply cancels pending old waits; subsequent scheduling uses the new values. |
| `edge.preview.activation.postDragStableMs` · Post-drag preview stable dwell | `number` | 48 ms | `live` | Read by the existing preview-intent and timer paths. Apply cancels pending old waits; subsequent scheduling uses the new values. |
| `edge.preview.activation.transferResidenceMs` · Preview transfer minimum dwell | `number` | 32 ms | `live` | Read by the existing preview-intent and timer paths. Apply cancels pending old waits; subsequent scheduling uses the new values. |
| `edge.preview.fallback.estimatedLineHeightDip` · Default status preview estimated line height | `number` | 20 DIP | `restart` | Estimation input for the status card shown when dedicated preview content is unavailable. |
| `edge.preview.fallback.heightBaseDip` · Default status preview fixed estimated height | `number` | 150 DIP | `restart` | Estimation input for the status card shown when dedicated preview content is unavailable. |
| `edge.preview.fallback.heightDip` · Default status preview final height | `number` | Estimated from the current status text wrapping, using the default status card height range. | `restart` | Estimated from the current status text wrapping, using the default status card height range. |
| `edge.preview.fallback.maximumHeightDip` · Default status preview maximum height | `number` | 280 DIP | `restart` | Estimation input for the status card shown when dedicated preview content is unavailable. |
| `edge.preview.fallback.maximumWidthDip` · Default status preview maximum width | `number` | 440 DIP | `restart` | Estimation input for the status card shown when dedicated preview content is unavailable. |
| `edge.preview.fallback.minimumHeightDip` · Default status preview minimum height | `number` | 160 DIP | `restart` | Estimation input for the status card shown when dedicated preview content is unavailable. |
| `edge.preview.fallback.minimumTextWidthDip` · Default status preview minimum text width | `number` | 72 DIP | `restart` | Estimation input for the status card shown when dedicated preview content is unavailable. |
| `edge.preview.fallback.minimumWidthDip` · Default status preview minimum width | `number` | 120 DIP | `restart` | Estimation input for the status card shown when dedicated preview content is unavailable. |
| `edge.preview.fallback.textReserveDip` · Default status preview wrapping reserve | `number` | 40 DIP | `restart` | Estimation input for the status card shown when dedicated preview content is unavailable. |
| `edge.preview.fallback.widthDip` · Default status preview final width | `number` | Estimated from the current title and status text, using the default status card width range. | `restart` | Estimated from the current title and status text, using the default status card width range. |
| `edge.preview.markdown.artifactBodyHeightDip` · Note preview pre-rendered body height budget | `number` | The greater of the original 410 DIP preparation height and the current maximum possible note preview height. | `restart` | The greater of the original 410 DIP preparation height and the current maximum possible note preview height. |
| `edge.preview.markdown.blockBudget` · Note preview block budget | `integer` | 16 | `restart` | Blocks admitted when capturing preview content. Fenced code in Full mode keeps the existing block-counting rule. |
| `edge.preview.markdown.blockCharacterBudget` · Note preview characters scanned per block | `integer` | Follows the current total preview character budget by default; an explicit value overrides it independently. | `restart` | Follows the current total preview character budget by default; an explicit value overrides it independently. |
| `edge.preview.markdown.bodyWidthReserveDip` · Note preview wrapping width reserve | `number` | 44 DIP | `restart` | Keeps the current note preview formula and clipping. Cached geometry, pre-rendering and host reservation use this value after restart. |
| `edge.preview.markdown.characterBudget` · Note preview total character budget | `integer` | 6000 | `restart` | Total character budget shared by capture and measurement, including separators between admitted lines. |
| `edge.preview.markdown.emptyHeightDip` · Empty note preview height | `number` | 120 DIP | `restart` | Keeps the current note preview formula and clipping. Cached geometry, pre-rendering and host reservation use this value after restart. |
| `edge.preview.markdown.emptyMinimumWidthDip` · Empty note preview minimum width | `number` | 130 DIP | `restart` | Keeps the current note preview formula and clipping. Cached geometry, pre-rendering and host reservation use this value after restart. |
| `edge.preview.markdown.estimatedLineHeightDip` · Note preview estimated base line height | `number` | 22 DIP | `restart` | Keeps the current note preview formula and clipping. Cached geometry, pre-rendering and host reservation use this value after restart. |
| `edge.preview.markdown.heightBaseDip` · Note preview fixed estimated height | `number` | 74 DIP | `restart` | Keeps the current note preview formula and clipping. Cached geometry, pre-rendering and host reservation use this value after restart. |
| `edge.preview.markdown.heightDip` · Note preview final height | `number` | Computed from the admitted Markdown line estimate, fixed height, line height and scale, using the current height range. | `restart` | Computed from the admitted Markdown line estimate, fixed height, line height and scale, using the current height range. |
| `edge.preview.markdown.hostHeightDip` · Note preview reserved host height | `number` | The greater of the original 410 DIP reservation and the current maximum possible note preview height. | `restart` | The greater of the original 410 DIP reservation and the current maximum possible note preview height. |
| `edge.preview.markdown.maximumHeightDip` · Nonempty note preview maximum height | `number` | 320 DIP | `restart` | Keeps the current note preview formula and clipping. Cached geometry, pre-rendering and host reservation use this value after restart. |
| `edge.preview.markdown.maximumWidthDip` · Note preview maximum width | `number` | 460 DIP | `restart` | Keeps the current note preview formula and clipping. Cached geometry, pre-rendering and host reservation use this value after restart. |
| `edge.preview.markdown.minimumHeightDip` · Nonempty note preview minimum height | `number` | 150 DIP | `restart` | Keeps the current note preview formula and clipping. Cached geometry, pre-rendering and host reservation use this value after restart. |
| `edge.preview.markdown.minimumWidthDip` · Note preview minimum width | `number` | 120 DIP | `restart` | Keeps the current note preview formula and clipping. Cached geometry, pre-rendering and host reservation use this value after restart. |
| `edge.preview.markdown.textScale` · Note preview estimated text scale | `number` | The current note base font size times per-note zoom, rounded, then divided by globally scaled 14 DIP. | `restart` | The current note base font size times per-note zoom, rounded, then divided by globally scaled 14 DIP. |
| `edge.preview.markdown.titleFontDip` · Note preview base title font size | `number` | 13 DIP | `restart` | Note preview title font size before global font scaling. |
| `edge.preview.markdown.widthDip` · Note preview final width | `number` | Estimated from the title, admitted Markdown text and current scale, using the current width range. | `restart` | Estimated from the title, admitted Markdown text and current scale, using the current width range. |
| `edge.preview.markdown.widthReserveDip` · Note preview width-estimation reserve | `number` | 72 DIP | `restart` | Keeps the current note preview formula and clipping. Cached geometry, pre-rendering and host reservation use this value after restart. |
| `edge.preview.measure.chromeReserveWidthDip` · Preview default horizontal reserve | `number` | 72 DIP | `restart` | Used for lightweight width and wrapping estimates; the existing controls still render the content. |
| `edge.preview.measure.glyphWidthDip` · Preview estimated base glyph width | `number` | 6.4 DIP | `restart` | Used for lightweight width and wrapping estimates; the existing controls still render the content. |
| `edge.preview.measure.minimumLineUnits` · Preview wrapping minimum character units | `integer` | 12 | `restart` | Limits the input to lightweight text measurement or the estimated character units per line. |
| `edge.preview.measure.widthLineBudget` · Preview width-estimation line budget | `integer` | 32 | `restart` | Limits the input to lightweight text measurement or the estimated character units per line. |
| `edge.preview.measure.widthUnitBudget` · Preview width-estimation character units | `number` | 64 | `restart` | Limits the input to lightweight text measurement or the estimated character units per line. |
| `edge.preview.measure.wrappedLineBudget` · Preview wrapping input-line budget | `integer` | 80 | `restart` | Limits the input to lightweight text measurement or the estimated character units per line. |
| `edge.preview.monitorInsetDip` · Preview monitor work-area width and height deduction | `number` | 16 DIP | `restart` | Subtracted separately from work-area width and height when calculating available Mini size on the current monitor. |
| `edge.preview.plugin.defaultMaximumHeightDip` · Plugin Mini compatibility maximum height | `number` | 600 DIP | `restart` | Compatibility baseline used to lock the session maximum height when miniMaxSize is not declared. |
| `edge.preview.plugin.defaultMaximumWidthDip` · Plugin Mini compatibility maximum width | `number` | 800 DIP | `restart` | Compatibility baseline used to lock the session maximum width when miniMaxSize is not declared. |
| `edge.preview.plugin.fallbackContentWidthDip` · Plugin capsule fallback default content width | `number` | 110 DIP | `restart` | Base width for fallback estimation when the capsule does not provide a content width. |
| `edge.preview.plugin.fallbackHeightDip` · Plugin capsule fallback preview height | `number` | 140 DIP | `restart` | Height used when a plugin without a dedicated Mini builds a fallback preview from its capsule content. |
| `edge.preview.plugin.fallbackMaximumWidthDip` · Plugin capsule fallback maximum width | `number` | 360 DIP | `restart` | Width range for a capsule-derived fallback preview when a plugin has no dedicated Mini. |
| `edge.preview.plugin.fallbackMinimumWidthDip` · Plugin capsule fallback minimum width | `number` | 220 DIP | `restart` | Width range for a capsule-derived fallback preview when a plugin has no dedicated Mini. |
| `edge.preview.plugin.fallbackWidthDip` · Plugin capsule fallback final width | `number` | Computed from the current capsule content width, close area, outer margin and fallback multiplier, using the fallback width range. | `restart` | Computed from the current capsule content width, close area, outer margin and fallback multiplier, using the fallback width range. |
| `edge.preview.plugin.fallbackWidthScale` · Plugin capsule fallback width multiplier | `number` | 1.65 | `restart` | Multiplier applied to capsule content width plus the close area and outer margin when estimating fallback width. |
| `edge.preview.todo.emptyFontDip` · Empty Todo preview base font size | `number` | 16 DIP | `restart` | Font size before global scaling. Existing text clipping remains unchanged. |
| `edge.preview.todo.emptyHeightDip` · Empty Todo preview height | `number` | 120 DIP | `restart` | Keeps the current Todo preview formula and clipping. Cached geometry and host reservation use this value after restart. |
| `edge.preview.todo.emptyMinimumWidthDip` · Empty Todo preview minimum width | `number` | 130 DIP | `restart` | Keeps the current Todo preview formula and clipping. Cached geometry and host reservation use this value after restart. |
| `edge.preview.todo.estimatedLineHeightDip` · Todo preview estimated base line height | `number` | 28 DIP | `restart` | Keeps the current Todo preview formula and clipping. Cached geometry and host reservation use this value after restart. |
| `edge.preview.todo.heightBaseDip` · Todo preview fixed estimated height | `number` | 62 DIP | `restart` | Keeps the current Todo preview formula and clipping. Cached geometry and host reservation use this value after restart. |
| `edge.preview.todo.heightDip` · Todo preview final height | `number` | Computed from the admitted Todo wrapping estimate, fixed height and line height, using the current height range. | `restart` | Computed from the admitted Todo wrapping estimate, fixed height and line height, using the current height range. |
| `edge.preview.todo.hostHeightDip` · Todo preview reserved host height | `number` | The greater of the original 400 DIP reservation and the current maximum possible Todo preview height. | `restart` | The greater of the original 400 DIP reservation and the current maximum possible Todo preview height. |
| `edge.preview.todo.itemBudget` · Todo preview item budget | `integer` | 12 | `restart` | Shared by Todo preview capture, estimation and rendering. Takes effect after restart. |
| `edge.preview.todo.itemCharacterBudget` · Todo preview characters per item | `integer` | 512 | `restart` | Shared by Todo preview capture, estimation and rendering. Takes effect after restart. |
| `edge.preview.todo.itemEstimatedLineCap` · Todo preview estimated lines per item | `integer` | 3 | `restart` | Keeps the current Todo preview formula and clipping. Cached geometry and host reservation use this value after restart. |
| `edge.preview.todo.itemFontDip` · Todo preview base item font size | `number` | 12 DIP | `restart` | Font size before global scaling. Existing text clipping remains unchanged. |
| `edge.preview.todo.linkedTargetButtonSizeDip` · Todo preview link button size | `number` | 20 DIP | `restart` | Shared by Todo preview capture, estimation and rendering. Takes effect after restart. |
| `edge.preview.todo.markerFontDip` · Todo preview base reminder marker size | `number` | 10.5 DIP | `restart` | Font size before global scaling. Existing text clipping remains unchanged. |
| `edge.preview.todo.maximumHeightDip` · Nonempty Todo preview maximum height | `number` | 320 DIP | `restart` | Keeps the current Todo preview formula and clipping. Cached geometry and host reservation use this value after restart. |
| `edge.preview.todo.maximumWidthDip` · Todo preview maximum width | `number` | 450 DIP | `restart` | Keeps the current Todo preview formula and clipping. Cached geometry and host reservation use this value after restart. |
| `edge.preview.todo.minimumHeightDip` · Nonempty Todo preview minimum height | `number` | 150 DIP | `restart` | Keeps the current Todo preview formula and clipping. Cached geometry and host reservation use this value after restart. |
| `edge.preview.todo.minimumWidthDip` · Todo preview minimum width | `number` | 120 DIP | `restart` | Keeps the current Todo preview formula and clipping. Cached geometry and host reservation use this value after restart. |
| `edge.preview.todo.summaryFontDip` · Todo preview base count font size | `number` | 11 DIP | `restart` | Font size before global scaling. Existing text clipping remains unchanged. |
| `edge.preview.todo.textReserveDip` · Todo preview fixed text-lane reserve | `number` | 80 DIP | `restart` | The same reserve is added to the width estimate and subtracted for wrapping. It does not change the actual Grid layout. |
| `edge.preview.todo.titleFontDip` · Todo preview base title font size | `number` | 13 DIP | `restart` | Font size before global scaling. Existing text clipping remains unchanged. |
| `edge.preview.todo.truncationFontDip` · Todo preview base truncation marker size | `number` | 14 DIP | `restart` | Font size before global scaling. Existing text clipping remains unchanged. |
| `edge.preview.todo.widthDip` · Todo preview final width | `number` | Estimated from the title, admitted Todo text and marker widths, using the current empty or nonempty width range. | `restart` | Estimated from the title, admitted Todo text and marker widths, using the current empty or nonempty width range. |
| `edge.queue.exitSnapDistanceDip` · Distance to detach from the queue edge | `number` | 18 DIP | `next_use` | Independent distance used by the existing free-queue snapping and drop calculations. |
| `edge.queue.releaseDistanceDip` · Queue release distance | `number` | 30 DIP | `next_use` | Independent distance used by the existing free-queue snapping and drop calculations. |
| `edge.queue.snapDistanceDip` · Queue snap distance | `number` | 18 DIP | `next_use` | Independent distance used by the existing free-queue snapping and drop calculations. |
| `edge.queue.takeoverAdvantageDip` · Queue target takeover advantage | `number` | 6 DIP | `next_use` | Independent distance used by the existing free-queue snapping and drop calculations. |

### Find popup (13)

| Key / title | Type | Default / calculation | When | Description |
| --- | --- | --- | --- | --- |
| `find.baseWidthDip` · Find panel base width | `number` | 296 DIP | `restart` | Find panel width before global text scaling. |
| `find.controlSizeDip` · Find control base size | `number` | 23 DIP | `restart` | Minimum input height and button side length, subsequently adjusted by text scaling. |
| `find.counterFontSizeDip` · Find counter font size | `number` | 10.5 DIP | `restart` | Match counter font size before global scaling. |
| `find.counterMinWidthDip` · Find counter minimum width | `number` | 42 DIP | `restart` | Base minimum width of the match count area. |
| `find.iconSizeDip` · Find button icon size | `number` | 16 DIP | `restart` | Icon side length for previous match, next match and close buttons. |
| `find.inputFontSizeDip` · Find input font size | `number` | 11.5 DIP | `restart` | Font size for find input and button text before global scaling. |
| `find.inputMinWidthDip` · Find input minimum width | `number` | 72 DIP | `restart` | Minimum width of the column containing the find input field. |
| `find.maxWidthDip` · Find panel maximum width | `number` | 420 DIP | `restart` | Upper width bound when the find panel follows text scaling. |
| `find.minLeftDip` · Find panel minimum left position | `number` | 6 DIP | `next_use` | Minimum distance of the default find panel position from the paper's left edge. |
| `find.minWidthDip` · Find panel minimum width | `number` | 236 DIP | `restart` | Lower width bound when the find panel follows text scaling. |
| `find.rightOverhangDip` · Find panel right overhang | `number` | 20 DIP | `next_use` | How far the default find panel extends beyond the paper's right edge. |
| `find.topOffsetDip` · Find panel vertical offset | `number` | 33 DIP | `next_use` | Default position offset from the bottom edge of the title bar. |
| `find.widthDip` · Final find panel width | `number` | Computed from current settings and layout | `restart` | Override the find panel width after text scaling, limiting and rounding. |

### Fullscreen detection (3)

| Key / title | Type | Default / calculation | When | Description |
| --- | --- | --- | --- | --- |
| `fullscreen.eventDebounceMs` · Fullscreen event debounce delay | `number` | 75 ms | `live` | Interval for this timer or throttle; other timer intervals stay unchanged. Advisory: ≥ 1. |
| `fullscreen.globalScanIntervalMs` · Global fullscreen scan interval | `number` | 5000 ms | `live` | Interval for this timer or throttle; other timer intervals stay unchanged. Advisory: ≥ 1. |
| `fullscreen.pollIntervalMs` · Fullscreen check interval | `number` | 1000 ms | `live` | Interval for this timer or throttle; other timer intervals stay unchanged. Advisory: ≥ 1. |

### Markdown parsing, layout and interaction (7)

| Key / title | Type | Default / calculation | When | Description |
| --- | --- | --- | --- | --- |
| `markdown.layout.maxLinesPerTurn` · Layout lines per turn | `integer` | 4 lines | `live` | Maximum lines processed in one scheduled turn of the background layout worker. |
| `markdown.layout.maxTurnMs` · Layout time per turn | `number` | 1.5 ms | `live` | Time budget for one scheduled turn of the background layout worker. |
| `markdown.parse.fullThresholdChars` · Full parse threshold | `integer` | 8000 chars | `live` | Text shorter than this is fully parsed on every edit; longer text uses the existing local parsing path when possible. |
| `markdown.parse.localWindowChars` · Local parse window | `integer` | 1024 chars | `live` | Initial character window for local parsing; the existing semantic boundary rules may extend it. |
| `markdown.reveal.fadeMs` · Markdown marker fade-in duration | `number` | 140 ms | `live` | Base duration of the animation that reveals Markdown syntax markers while editing. |
| `markdown.reveal.tickMs` · Markdown marker animation interval | `number` | 16 ms | `restart` | Timer interval for fading in Markdown syntax markers. |
| `markdown.tasks.hitPaddingDip` · Markdown checkbox hit padding | `number` | 2 DIP | `live` | Extra clickable distance around each rendered checkbox. |

### Menu entry visibility (11)

| Key / title | Type | Default / calculation | When | Description |
| --- | --- | --- | --- | --- |
| `menu.edge.association` · Edge capsule menu: Association | `boolean` | true | `next_use` | Controls only this overflow or context-menu entry; other menu, top bar and command entry points stay available. |
| `menu.edge.externalEditor` · Edge capsule menu: External editor | `boolean` | true | `next_use` | Controls only this overflow or context-menu entry; other menu, top bar and command entry points stay available. |
| `menu.edge.newNote` · Edge capsule menu: New note | `boolean` | true | `next_use` | Controls only this overflow or context-menu entry; other menu, top bar and command entry points stay available. |
| `menu.edge.newTodo` · Edge capsule menu: New todo | `boolean` | true | `next_use` | Controls only this overflow or context-menu entry; other menu, top bar and command entry points stay available. |
| `menu.edge.workspace` · Edge capsule menu: Workspace | `boolean` | true | `next_use` | Controls only this overflow or context-menu entry; other menu, top bar and command entry points stay available. |
| `menu.paper.association` · Paper menu: Association | `boolean` | true | `next_use` | Controls only this overflow or context-menu entry; other menu, top bar and command entry points stay available. |
| `menu.paper.externalEditor` · Paper menu: External editor | `boolean` | true | `next_use` | Controls only this overflow or context-menu entry; other menu, top bar and command entry points stay available. |
| `menu.paper.newNote` · Paper menu: New note | `boolean` | true | `next_use` | Controls only this overflow or context-menu entry; other menu, top bar and command entry points stay available. |
| `menu.paper.newTodo` · Paper menu: New todo | `boolean` | true | `next_use` | Controls only this overflow or context-menu entry; other menu, top bar and command entry points stay available. |
| `menu.paper.runScript` · Paper menu: Run script | `boolean` | true | `next_use` | Controls only this overflow or context-menu entry; other menu, top bar and command entry points stay available. |
| `menu.paper.workspace` · Paper menu: Workspace | `boolean` | true | `next_use` | Controls only this overflow or context-menu entry; other menu, top bar and command entry points stay available. |

### New papers (4)

| Key / title | Type | Default / calculation | When | Description |
| --- | --- | --- | --- | --- |
| `newPaper.note.heightDip` · New note paper height | `number` | 360 DIP | `next_use` | Final initial dimension for a new paper; existing papers and legacy-data defaults stay unchanged. |
| `newPaper.note.widthDip` · New note paper width | `number` | 320 DIP | `next_use` | Final initial dimension for a new paper; existing papers and legacy-data defaults stay unchanged. |
| `newPaper.todo.heightDip` · New todo paper height | `number` | 340 DIP | `next_use` | Final initial dimension for a new paper; existing papers and legacy-data defaults stay unchanged. |
| `newPaper.todo.widthDip` · New todo paper width | `number` | 280 DIP | `next_use` | Final initial dimension for a new paper; existing papers and legacy-data defaults stay unchanged. |

### Notes, images and typography (37)

| Key / title | Type | Default / calculation | When | Description |
| --- | --- | --- | --- | --- |
| `note.clipboard.imageMaxWidthDip` · Clipboard image maximum width | `number` | 640 DIP | `next_use` | Default and maximum display width of images exported as HTML or RTF. |
| `note.clipboard.imageMinWidthDip` · Clipboard image minimum width | `number` | 24 DIP | `next_use` | Minimum display width of images exported as HTML or RTF. |
| `note.clipboard.maxHtmlImageBytes` · HTML clipboard image budget | `integer` | 25165824 bytes | `next_use` | Source image byte budget for mixed HTML export, counting each image occurrence separately. |
| `note.clipboard.maxImageBytes` · Clipboard image input limit | `integer` | 33554432 bytes | `next_use` | Byte limit when reading encoded image data from the clipboard. |
| `note.clipboard.maxRtfImageBytes` · RTF clipboard image budget | `integer` | 16777216 bytes | `next_use` | Source image byte budget for mixed RTF export, counting each image occurrence separately. |
| `note.editor.indentationSize` · Note indentation width | `integer` | 4 columns | `restart` | Tab and indentation display width in AvalonEdit. |
| `note.images.autoCompressedDimensionPx` · Automatic compression target dimension | `integer` | 2048 px | `next_use` | Maximum dimension after resizing large images when automatic compression is enabled. |
| `note.images.cachePruneMs` · Image block cache cleanup delay | `number` | 200 ms | `restart` | Debounce delay before cleaning up cached image controls after the visible lines change. |
| `note.images.fallbackWidthDip` · Note image width before layout | `number` | 240 DIP | `restart` | Target width used while the image viewport has no valid width yet. |
| `note.images.horizontalPaddingDip` · Note image horizontal padding | `number` | 2 DIP | `restart` | Space reserved on each side when calculating the available image width. |
| `note.images.maxCacheEntries` · Decoded image cache count | `integer` | 20 items | `next_use` | Entry budget for the LRU decoded-image cache, excluding images currently visible. |
| `note.images.maxDecodedBytes` · Decoded image cache budget | `integer` | 52428800 bytes | `next_use` | Byte budget for the LRU decoded-image cache, excluding images currently visible. |
| `note.images.maxInputBytes` · Image import input budget | `integer` | 33554432 bytes | `next_use` | Byte limit for original image data supplied during import. |
| `note.images.maxStoredBytes` · Single image storage budget | `integer` | 8388608 bytes | `next_use` | Maximum encoded byte size of one image asset. |
| `note.images.maxStoredDimensionPx` · Stored image maximum dimension | `integer` | 4096 px | `next_use` | Maximum pixel dimension accepted when importing or loading image assets. |
| `note.images.maxTotalBytes` · Total image asset budget | `integer` | 125829120 bytes | `next_use` | Total encoded byte limit for all images in the current image store. |
| `note.images.minTargetWidthDip` · Note image minimum target width | `number` | 80 DIP | `restart` | Built-in lower bound for the image target width after viewport calculations. |
| `note.images.resizeSettleMs` · Image resize settling delay | `number` | 200 ms | `restart` | Wait after image viewport resizing stops before resuming high-quality decoding. |
| `note.images.resizeToleranceDip` · Image reflow width tolerance | `number` | 1 DIP | `live` | Reflow images once their viewport width changes by this amount. |
| `note.images.targetWidthDip` · Final note image available width | `number` | Computed from current settings and layout | `restart` | Override the target image width calculated from the content viewport. |
| `note.images.verticalPaddingDip` · Note image vertical padding | `number` | 7 DIP | `restart` | Base vertical padding used by image block layout. |
| `note.images.viewportReserveDip` · Note image viewport reserve | `number` | 10 DIP | `restart` | Extra space subtracted from image width in addition to left and right padding. |
| `note.maxTextChars` · Note text length limit | `integer` | 100000 chars | `restart` | Character limit shared by the note editor and the host write APIs. |
| `note.padding.bottomDip` · Note content bottom padding | `number` | 8 DIP | `restart` | Inner padding of the note content area. |
| `note.padding.leftDip` · Note content left padding | `number` | 13 DIP | `restart` | Inner padding of the note content area. |
| `note.padding.rightDip` · Note content right padding | `number` | 6 DIP | `restart` | Inner padding of the note content area. |
| `note.padding.topDip` · Note content top padding | `number` | 8 DIP | `restart` | Inner padding of the note content area. |
| `note.typography.baseCodeFontSize` · Note code base font size | `number` | 13 DIP | `restart` | Font size before the font preset adjustment and global scaling. |
| `note.typography.baseFontSize` · Note text base font size | `number` | 14 DIP | `restart` | Font size before the font preset adjustment and global scaling. |
| `note.typography.baseHeading1FontSize` · Note heading 1 base font size | `number` | 17 DIP | `restart` | Font size before the font preset adjustment and global scaling. |
| `note.typography.baseHeading2FontSize` · Note heading 2 base font size | `number` | 15 DIP | `restart` | Font size before the font preset adjustment and global scaling. |
| `note.typography.baseHeading3FontSize` · Note heading 3 base font size | `number` | 14 DIP | `restart` | Font size before the font preset adjustment and global scaling. |
| `note.typography.codeFontSize` · Note code computed font size | `number` | Computed from current settings and layout | `restart` | Override the font size after the preset and global scaling; per-note zoom is still applied afterward. |
| `note.typography.fontSize` · Note text computed font size | `number` | Computed from current settings and layout | `restart` | Override the font size after the preset and global scaling; per-note zoom is still applied afterward. |
| `note.typography.heading1FontSize` · Note heading 1 computed font size | `number` | Computed from current settings and layout | `restart` | Override the font size after the preset and global scaling; per-note zoom is still applied afterward. |
| `note.typography.heading2FontSize` · Note heading 2 computed font size | `number` | Computed from current settings and layout | `restart` | Override the font size after the preset and global scaling; per-note zoom is still applied afterward. |
| `note.typography.heading3FontSize` · Note heading 3 computed font size | `number` | Computed from current settings and layout | `restart` | Override the font size after the preset and global scaling; per-note zoom is still applied afterward. |

### Paper shell and interaction (24)

| Key / title | Type | Default / calculation | When | Description |
| --- | --- | --- | --- | --- |
| `paper.capsuleCloseGlyphNormalOffset` · Capsule close symbol offset | `number` | -1 DIP | `restart` | Used by the paper’s existing interface or interaction behavior. |
| `paper.capsuleIconGap` · Capsule icon-to-text gap | `number` | 4 DIP | `restart` | Used by the paper’s existing interface or interaction behavior. |
| `paper.capsuleLeftPadding` · Ordinary capsule left padding | `number` | 6 DIP | `restart` | Used by the paper’s existing interface or interaction behavior. |
| `paper.capsuleNormalCloseWidth` · Ordinary capsule close area width | `number` | 21 DIP | `restart` | Used by the paper’s existing interface or interaction behavior. |
| `paper.capsuleNormalMinWidth` · Minimum ordinary capsule width | `number` | 76 DIP | `restart` | Used by the paper’s existing interface or interaction behavior. |
| `paper.capsuleRightPadding` · Ordinary capsule right padding | `number` | 6 DIP | `restart` | Used by the paper’s existing interface or interaction behavior. |
| `paper.collapseResizeMilliseconds` · Paper collapse resize duration | `integer` | 150 ms | `next_use` | Used by the paper’s existing interface or interaction behavior. |
| `paper.collapseShellFadeMilliseconds` · Paper collapse fade-out duration | `integer` | 70 ms | `next_use` | Used by the paper’s existing interface or interaction behavior. |
| `paper.deepCapsuleCrossQueueDragMorphMilliseconds` · Shape transition duration between queues | `integer` | 90 ms | `next_use` | Used by the paper’s existing interface or interaction behavior. |
| `paper.deepCapsuleCrossQueueDragScaleFrom` · Initial scale when dragging between queues | `number` | 0.97 | `next_use` | Used by the paper’s existing interface or interaction behavior. Advisory: 0–1. |
| `paper.deepCapsuleReorderDragExtraThreshold` · Additional capsule reorder threshold | `number` | 4 DIP | `next_use` | Used by the paper’s existing interface or interaction behavior. |
| `paper.expandAnimationMilliseconds` · Paper expansion duration | `integer` | 220 ms | `next_use` | Used by the paper’s existing interface or interaction behavior. |
| `paper.expandCapsuleFadeOutMilliseconds` · Capsule fade-out duration during expansion | `integer` | 80 ms | `next_use` | Used by the paper’s existing interface or interaction behavior. |
| `paper.expandShellFadeInMilliseconds` · Paper fade-in duration during expansion | `integer` | 140 ms | `next_use` | Used by the paper’s existing interface or interaction behavior. |
| `paper.maxUndoDepth` · Todo undo history depth | `integer` | 100 | `next_use` | Used by the paper’s existing interface or interaction behavior. |
| `paper.radiusBlock` · Panel corner radius | `number` | 12 DIP | `restart` | Used by the paper’s existing interface or interaction behavior. |
| `paper.radiusControl` · Control corner radius | `number` | 8 DIP | `restart` | Used by the paper’s existing interface or interaction behavior. |
| `paper.radiusShell` · Paper shell corner radius | `number` | 16 DIP | `restart` | Used by the paper’s existing interface or interaction behavior. |
| `paper.radiusSmall` · Small element corner radius | `number` | 4 DIP | `restart` | Used by the paper’s existing interface or interaction behavior. |
| `paper.titleBarDragThreshold` · Title bar drag threshold | `number` | 1 DIP | `next_use` | Used by the paper’s existing interface or interaction behavior. |
| `paper.titleBarHeightDip` · Final paper top bar height | `number` | Calculated from the current settings or runtime state | `restart` | Uses the existing text, content or scale calculation until an override replaces its result. |
| `paper.titleBaseDip` · Paper title base font size | `number` | 12 DIP | `live` | Base font size before applying the current preset and global scale. |
| `paper.titleFontDip` · Final paper title font size | `number` | Calculated from the current settings or runtime state | `live` | Uses the existing text, content or scale calculation until an override replaces its result. |
| `paper.topBarCollisionGap` · Top bar button collision gap | `number` | 2 DIP | `restart` | Used by the paper’s existing interface or interaction behavior. |

### Plugin presentation and events (9)

| Key / title | Type | Default / calculation | When | Description |
| --- | --- | --- | --- | --- |
| `plugin.capsule.componentGapDip` · Plugin capsule component gap | `number` | 5 DIP | `restart` | Spacing between adjacent components in the plugin template. |
| `plugin.capsule.fillProgressBarMinWidthDip` · Plugin capsule filling progress bar minimum width | `number` | 18 DIP | `restart` | Minimum width of a progress bar that fills the available space. |
| `plugin.capsule.maxContentWidthDip` · Plugin capsule maximum content width | `number` | 320 DIP | `restart` | Upper bound for a plugin capsule's desired content width. |
| `plugin.capsule.minContentWidthDip` · Plugin capsule minimum content width | `number` | 34 DIP | `restart` | Lower bound for a plugin capsule's desired content width. |
| `plugin.capsule.progressBarWidthDip` · Plugin capsule progress bar width | `number` | 30 DIP | `restart` | Progress bar width when the plugin does not specify a size. |
| `plugin.capsule.progressRingSizeDip` · Plugin capsule progress ring size | `number` | 18 DIP | `restart` | Progress ring diameter when the plugin does not specify a size. |
| `plugin.capsule.statusDotSizeDip` · Plugin capsule status dot size | `number` | 7 DIP | `restart` | Diameter of a plugin capsule's status dot. |
| `plugin.events.userChangeDebounceMs` · Plugin user-change event debounce | `number` | 180 ms | `restart` | Wait to combine consecutive user edits before creating plugin notifications. |
| `plugin.settings.textPropagationMs` · Plugin text setting propagation delay | `number` | 180 ms | `restart` | Wait to coalesce changes to plugin string and numeric settings. |

### Todo reminders (15)

| Key / title | Type | Default / calculation | When | Description |
| --- | --- | --- | --- | --- |
| `reminder.advanceNoticeMs` · Reminder advance notice time | `number` | 5000 ms | `next_use` | How far ahead of the due time the reminder preview event is sent to plugins. |
| `reminder.maxCustomSoundBytes` · Custom reminder sound budget | `integer` | 10485760 bytes | `next_use` | Maximum file size accepted when loading a custom reminder sound. |
| `reminder.maxTimerIntervalMs` · Maximum reminder check interval | `number` | 60000 ms | `next_use` | Longest allowed timer interval before the next reminder check. |
| `reminder.minTimerIntervalMs` · Minimum reminder check interval | `number` | 250 ms | `next_use` | Shortest allowed interval for reminder scheduling and retry timers. |
| `reminder.retryMs` · Reminder failure retry interval | `number` | 30000 ms | `next_use` | Default retry delay after some or all reminder deliveries fail. |
| `reminder.summaryChars` · Reminder summary length | `integer` | 90 chars | `next_use` | Maximum characters in a system tray reminder summary. |
| `reminder.ui.countdownFontOffsetDip` · Reminder countdown font size reduction | `number` | 2 DIP | `restart` | Base amount subtracted from the text font size for the countdown. |
| `reminder.ui.iconFontOffsetDip` · Reminder icon font size reduction | `number` | 1.5 DIP | `restart` | Base amount subtracted from the todo text font size for the reminder icon. |
| `reminder.ui.iconOpacity` · Reminder icon idle opacity | `number` | 0.44 | `restart` | Reminder settings icon opacity when the pointer is not over it. |
| `reminder.ui.iconWidthOffsetDip` · Reminder icon width reduction | `number` | 6 DIP | `restart` | Base amount subtracted from the checkbox column width for the reminder settings button. |
| `reminder.ui.minCountdownFontDip` · Reminder countdown minimum font size | `number` | 10 DIP | `restart` | Base lower bound when calculating the countdown font size from the text size. |
| `reminder.ui.minCountdownWidthDip` · Reminder countdown minimum width | `number` | 24 DIP | `restart` | Base minimum width of the countdown area. |
| `reminder.ui.minIconFontDip` · Reminder icon minimum font size | `number` | 10.5 DIP | `restart` | Base lower bound when calculating the reminder icon font size from the text size. |
| `reminder.ui.minIconWidthDip` · Reminder icon minimum width | `number` | 17 DIP | `restart` | Base minimum width of the reminder settings button. |
| `reminder.ui.pressedOpacity` · Reminder icon pressed opacity | `number` | 0.66 | `restart` | Reminder settings icon opacity while pressed. |

### Settings UI (10)

| Key / title | Type | Default / calculation | When | Description |
| --- | --- | --- | --- | --- |
| `settings.sidebar.baseWidthDip` · Settings sidebar base width | `number` | 158 DIP | `restart` | Base lower bound used when calculating the settings navigation width. |
| `settings.sidebar.maxWidthRatio` · Settings sidebar maximum expansion ratio | `number` | 1.2 | `restart` | Maximum sidebar width relative to its base width when expanding to fit measured text. |
| `settings.sidebar.rowHeightDip` · Settings navigation row height | `number` | 34 DIP | `restart` | Fixed height of navigation items on the left side of the settings window. |
| `settings.sidebar.widthDip` · Final settings sidebar width | `number` | Computed from current settings and layout | `restart` | Override the settings sidebar width after measuring translated text and applying limits. |
| `settings.window.baseHeightDip` · Settings window base height | `number` | 620 DIP | `restart` | Normal target height of the settings window. |
| `settings.window.baseWidthDip` · Settings window base width | `number` | 840 DIP | `restart` | Target settings window width before adding the sidebar's extra width. |
| `settings.window.boundaryMarginDip` · Settings window boundary space reserve | `number` | 32 DIP | `restart` | Total space subtracted from the available screen area when applying the final screen bounds. |
| `settings.window.minHeightDip` · Settings window reference minimum height | `number` | 320 DIP | `restart` | Reference lower bound when calculating a settings window height that fits the screen. |
| `settings.window.minWidthDip` · Settings window reference minimum width | `number` | 360 DIP | `restart` | Reference lower bound when calculating a settings window width that fits the screen. |
| `settings.window.preferredMarginDip` · Settings window preferred space reserve | `number` | 48 DIP | `restart` | Total space subtracted from the available screen area when calculating the target settings window size. |

### Todo input, layout, folding and dragging (55)

| Key / title | Type | Default / calculation | When | Description |
| --- | --- | --- | --- | --- |
| `todo.delete.animationMs` · Todo deletion animation duration | `integer` | 200 ms | `next_use` | Base fade-out and horizontal movement duration when deleting one todo. |
| `todo.delete.offsetDip` · Todo deletion animation offset | `number` | 30 DIP | `next_use` | Target horizontal displacement of a single todo's deletion animation. |
| `todo.drag.autoScroll` · Auto-scroll while dragging todos | `boolean` | true | `live` | Controls only scrolling during a drag; reordering and mouse-capture cleanup stay unchanged. |
| `todo.drag.ghostMinHeightDip` · Todo drag preview minimum height | `number` | 30 DIP | `next_use` | Minimum height of the todo preview that follows the pointer. |
| `todo.drag.ghostMinWidthDip` · Todo drag preview minimum width | `number` | 160 DIP | `next_use` | Minimum width of the todo preview that follows the pointer while dragging between positions. |
| `todo.drag.indicatorHeightDip` · Todo drop indicator height | `number` | 3 DIP | `restart` | Height of the insertion line shown while reordering todos by dragging. |
| `todo.drag.indicatorMinWidthDip` · Todo drop indicator minimum width | `number` | 24 DIP | `live` | Minimum width of the insertion position indicator. |
| `todo.drag.indicatorRadiusDip` · Todo drop indicator corner radius | `number` | 1.5 DIP | `restart` | Corner radius of the todo reordering indicator. |
| `todo.drag.indicatorWidthInsetDip` · Todo drop indicator width reduction | `number` | 8 DIP | `live` | Total width subtracted from the todo row width for its insertion indicator. |
| `todo.drag.scrollAcceleration` · Scroll acceleration coefficient | `number` | 520 | `live` | Independent coefficient in the existing drag auto-scroll formula; viewport and pointer position remain live inputs. |
| `todo.drag.scrollBaseSpeed` · Initial scroll speed | `number` | 28 | `live` | Independent coefficient in the existing drag auto-scroll formula; viewport and pointer position remain live inputs. |
| `todo.drag.scrollEaseSeconds` · Scroll acceleration time | `number` | 0.2 | `live` | Independent coefficient in the existing drag auto-scroll formula; viewport and pointer position remain live inputs. |
| `todo.drag.scrollEdgeDip` · Base drag auto-scroll edge distance | `number` | 48 | `live` | Independent coefficient in the existing drag auto-scroll formula; viewport and pointer position remain live inputs. |
| `todo.drag.scrollEdgeFinalDip` · Final drag auto-scroll edge distance | `number` | Calculated from the current settings or runtime state | `live` | Replaces the edge distance normally calculated from scale and viewport height on each update. |
| `todo.drag.scrollMaxElapsedSeconds` · Maximum elapsed time per scroll step | `number` | 0.05 | `live` | Independent coefficient in the existing drag auto-scroll formula; viewport and pointer position remain live inputs. |
| `todo.drag.scrollProximityPower` · Scroll proximity exponent | `number` | 2 | `live` | Independent coefficient in the existing drag auto-scroll formula; viewport and pointer position remain live inputs. |
| `todo.drag.scrollSpeedFinal` · Final drag auto-scroll speed | `number` | Calculated from the current settings or runtime state | `live` | Replaces the speed normally calculated from the pointer’s proximity to the edge on each update. |
| `todo.drag.scrollTickMs` · Drag auto-scroll tick interval | `number` | 16 | `live` | Independent coefficient in the existing drag auto-scroll formula; viewport and pointer position remain live inputs. Advisory: ≥ 1. |
| `todo.drag.scrollViewportRatio` · Drag auto-scroll edge ratio | `number` | 0.25 | `live` | Independent coefficient in the existing drag auto-scroll formula; viewport and pointer position remain live inputs. Advisory: 0–0.5. |
| `todo.fold.thresholdLines` · Todo folding line threshold | `integer` | 4 lines | `next_use` | Allow folding when the todo's displayed line count after layout exceeds this value. |
| `todo.fold.visibleLines` · Folded todo visible lines | `integer` | 2 lines | `next_use` | Number of text lines kept visible in a folded todo. |
| `todo.maxTextChars` · Todo text length limit | `integer` | 5000 chars | `restart` | Character limit shared by the todo editor and the host write APIs. |
| `todo.metrics.appendGlyphFontSize` · Todo add symbol font size | `number` | Computed from current settings and layout | `next_use` | Override this measurement after the todo preset and global text scaling. Used when a todo row is next built. |
| `todo.metrics.appendMinHeight` · Todo add-row height | `number` | Computed from current settings and layout | `next_use` | Override this measurement after the todo preset and global text scaling. Used when a todo row is next built. |
| `todo.metrics.checkColumnWidth` · Todo checkbox column width | `number` | Computed from current settings and layout | `next_use` | Override this measurement after the todo preset and global text scaling. Used when a todo row is next built. |
| `todo.metrics.ghostTextFontSize` · Todo drag preview font size | `number` | Computed from current settings and layout | `next_use` | Override this measurement after the todo preset and global text scaling. Used when a todo row is next built. |
| `todo.metrics.linkedPaperIconFontSize` · Todo linked paper icon font size | `number` | Computed from current settings and layout | `next_use` | Override this measurement after the todo preset and global text scaling. Used when a todo row is next built. |
| `todo.metrics.linkedPaperNameFontSize` · Todo linked paper name font size | `number` | Computed from current settings and layout | `next_use` | Override this measurement after the todo preset and global text scaling. Used when a todo row is next built. |
| `todo.metrics.rowMinHeight` · Todo row minimum height | `number` | Computed from current settings and layout | `next_use` | Override this measurement after the todo preset and global text scaling. Used when a todo row is next built. |
| `todo.metrics.textFontSize` · Todo text font size | `number` | Computed from current settings and layout | `next_use` | Override this measurement after the todo preset and global text scaling. Used when a todo row is next built. |
| `todo.metrics.textVerticalPadding` · Todo text vertical padding | `number` | Computed from current settings and layout | `next_use` | Override this measurement after the todo preset and global text scaling. Used when a todo row is next built. |
| `todo.metrics.trashGlyphFontSize` · Todo trash icon font size | `number` | Computed from current settings and layout | `next_use` | Override this measurement after the todo preset and global text scaling. Used when a todo row is next built. |
| `todo.paste.animatedItems` · Animated pasted todo count | `integer` | 15 items | `next_use` | Maximum todos included in staggered entrance animations during multiline paste. |
| `todo.paste.fadeMs` · Paste fade-in duration | `integer` | 200 ms | `next_use` | Base opacity transition duration for todos added by multiline paste. |
| `todo.paste.maxItems` · Todo batch input limit | `integer` | 200 items | `next_use` | Maximum todos in one multiline paste or one host batch-create operation. |
| `todo.paste.moveMs` · Paste movement duration | `integer` | 220 ms | `next_use` | Base vertical transition duration for todos added by multiline paste. |
| `todo.paste.staggerMs` · Paste animation stagger interval | `integer` | 40 ms | `next_use` | Time between the start of adjacent todos' entrance animations. |
| `todo.paste.startOffsetDip` · Paste animation starting offset | `number` | -15 DIP | `next_use` | Vertical displacement at the start of a todo's entrance animation. |
| `todo.pathLink.multilineMinWidthDip` · Multiline todo path button minimum width | `number` | 44 DIP | `restart` | Base minimum width of the path-name button in a multiline todo. |
| `todo.pathLink.multilineWidthRatio` · Multiline todo path button width ratio | `number` | 2 | `restart` | Path button width factor relative to the checkbox column width in multiline todos. |
| `todo.pathLink.namePaddingDip` · Path name measurement padding | `number` | 10 DIP | `restart` | Total space added to the measured text width when the full path name may be shown. |
| `todo.pathLink.singleLineMinWidthDip` · Single-line todo path button minimum width | `number` | 50 DIP | `restart` | Base minimum width of the path-name button in a single-line todo. |
| `todo.pathLink.singleLineWidthRatio` · Single-line todo path button width ratio | `number` | 2.2 | `restart` | Path button width factor relative to the checkbox column width in single-line todos. |
| `todo.pathLink.widthDip` · Final todo path button width | `number` | Computed from current settings and layout | `restart` | Override the path button width calculated from name, icon and todo row width rules. |
| `todo.selection.edgeDip` · Todo multiselection scroll zone | `number` | 28 DIP | `live` | Base size of the sweep-selection auto-scroll zone, before text scaling. |
| `todo.selection.edgeHeightRatio` · Todo multiselection scroll zone ratio | `number` | 0.25 | `live` | Maximum fraction of the visible list height occupied by the sweep-selection scroll zone. |
| `todo.selection.effectiveEdgeDip` · Final todo multiselection scroll zone | `number` | Computed from current settings and layout | `live` | Override the sweep-selection scroll zone after size and ratio calculations. |
| `todo.selection.effectiveStepDip` · Final todo multiselection acceleration distance | `number` | Computed from current settings and layout | `live` | Override the calculated distance required to add one scroll step. |
| `todo.selection.maxSteps` · Todo multiselection maximum scroll steps | `integer` | 6 steps | `live` | Maximum line-scroll steps per tracking tick during sweep-selection auto-scroll. |
| `todo.selection.stepDip` · Todo multiselection acceleration distance | `number` | 14 DIP | `live` | Base minimum distance required to add one scroll step. |
| `todo.selection.stepEdgeRatio` · Todo multiselection acceleration ratio | `number` | 0.7 | `live` | Distance required to add a scroll step, as a fraction of the scroll zone size. |
| `todo.selection.tickMs` · Todo multiselection tracking interval | `number` | 32 ms | `restart` | Pointer tracking timer interval during box selection or sweep selection. |
| `todo.strike.insetDip` · Todo strikethrough inset | `number` | 3 DIP | `next_use` | Inset at each end of a completed todo's strikethrough. |
| `todo.strike.thicknessDip` · Todo strikethrough thickness | `number` | 1.35 DIP | `next_use` | Stroke thickness of the strikethrough on completed todos. |
| `todo.strike.verticalRatio` · Todo strikethrough height ratio | `number` | 0.56 | `next_use` | Vertical position of the strikethrough as a fraction of each text line's rectangle. |

### Window dimensions, restoration, binding and following (18)

| Key / title | Type | Default / calculation | When | Description |
| --- | --- | --- | --- | --- |
| `window.autoCollapse.delayMs` · Auto-collapse delay after focus loss | `number` | 140 ms | `next_use` | Wait after focus is lost before automatically collapsing the paper. |
| `window.binding.dragOpacity` · Window binding drag opacity | `number` | 0.72 | `restart` | Binding button opacity during the window binding drag gesture. |
| `window.binding.sampleMs` · Window binding target sample interval | `number` | 32 ms | `live` | Interval between target window detection while dragging the binding button. |
| `window.follow.movingIdleMs` · Window movement following idle timeout | `number` | 2000 ms | `live` | Idle time required to stop following frames while the move gesture is still active. |
| `window.follow.retractDelayMs` · Following capsule retraction delay | `integer` | 180 ms | `restart` | Wait before retracting a following capsule after the pointer leaves it. |
| `window.follow.slideMs` · Following capsule slide duration | `number` | 130 ms | `next_use` | Slide duration when a window-bound capsule appears or retracts. |
| `window.follow.stableMs` · Window following stable duration | `number` | 100 ms | `live` | Time the target window must stay still before following frames stop. |
| `window.follow.stableSamples` · Window following stable sample count | `integer` | 3 samples | `live` | Number of stable position samples required before stopping continuous following frames. |
| `window.layout.capsuleHeightDip` · Overall capsule height | `number` | 46 DIP | `restart` | Base dimension shared by the existing layout and normalization rules. |
| `window.layout.capsuleWidthDip` · Default capsule width | `number` | 92 DIP | `restart` | Base dimension shared by the existing layout and normalization rules. |
| `window.layout.minHeightDip` · Minimum paper height | `number` | 120 DIP | `restart` | Base dimension shared by the existing layout and normalization rules. |
| `window.layout.minWidthDip` · Minimum paper width | `number` | 96 DIP | `restart` | Base dimension shared by the existing layout and normalization rules. |
| `window.layout.topBarHeightDip` · Paper top bar base height | `number` | 23.5 DIP | `restart` | Base dimension shared by the existing layout and normalization rules. |
| `window.opacity.transitionMs` · Unfocused opacity transition duration | `integer` | 120 ms | `next_use` | Base opacity transition duration for unfocused papers and idle capsules. |
| `window.restore.constrainSize` · Constrain paper size during restore | `boolean` | true | `next_use` | When disabled, restored windows keep their saved width and height; existing position recovery still applies. |
| `window.tether.detachDip` · Window tether detach distance | `number` | 72 DIP | `live` | Detach the tether once dragging reaches this distance. |
| `window.tether.minHandleHeightDip` · Window tether visible handle height | `number` | 12 DIP | `live` | Minimum visible height kept available as a draggable handle. |
| `window.tether.minHandleWidthDip` · Window tether visible handle width | `number` | 48 DIP | `live` | Minimum visible width kept available as a draggable handle. |

Catalog sources: [Desktop](../src/InjectionParameters.Desktop.cs), [Content](../src/InjectionParameters.Content.cs), [Preview](../src/InjectionParameters.Preview.cs).

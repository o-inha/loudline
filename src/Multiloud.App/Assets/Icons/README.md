These SVGs were supplied by the project owner, with attachment suffixes normalized. `diff.svg` uses the matching Lucide plus/minus path because no diff attachment was included.

The matching resources in `App.xaml` preserve the source paths and circle, the 24 × 24 coordinate frame, and the two-unit round stroke. WPF scales the whole frame for toolbar and export sizes; its current button foreground supplies SVG `currentColor`.

| Supplied SVG | WPF geometry |
| --- | --- |
| `folder-open.svg` | `FolderIconGeometry` |
| `settings.svg` | `GearIconGeometry` |
| `arrow-down-to-line.svg` | `ExportIconGeometry` |
| `slice.svg` | Retained source asset |
| `file-music.svg` | `OriginalIconGeometry` |
| `speaker.svg` | `ResultIconGeometry` |
| `scale.svg` | `ComparisonIconGeometry` |
| `chart-line.svg` | `EnvelopeIconGeometry` |
| `sliders-vertical.svg` | `GainsIconGeometry` |
| `mic-audio-lines.svg` | `BackgroundIconGeometry` |
| `scissors-line-dashed.svg` | `SliceIconGeometry` (rotated 90° clockwise) |
| `between-vertical-start.svg` | `CustomBoundariesIconGeometry` |
| `merge.svg` | `MergeIconGeometry` (rotated 180°) |
| `unfold-horizontal.svg` | `FitAllIconGeometry` |
| `fold-horizontal.svg` | `FitTrackIconGeometry` |
| `diff.svg` | `DiffIconGeometry` |
| `power.svg` | `PowerIconGeometry` |
| `ear.svg` | `SoloIconGeometry` |
| `trash.svg` | `TrashIconGeometry` |

`zap.svg` supplies the inline, unaltered path on `MainWindow.AutoProcessToggle`; it retains the same 24 × 24 frame and round two-unit stroke.

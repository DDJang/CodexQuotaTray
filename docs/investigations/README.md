# Engineering Investigations

These documents record historical investigations, experiments, root-cause analyses, and implementation
decisions. They may become outdated; current product, architecture, protocol, privacy, and release facts
belong in the authoritative documents in [`docs/`](../).

The status is maintained here so that an investigation does not need to be moved between `active`,
`resolved`, and `archived` directories. If an investigation changes a current project contract, update the
corresponding authoritative document and link back to the investigation for rationale.

| Area | Investigation | Status | Outcome | Resolved by | Last verified |
| --- | --- | --- | --- | --- | --- |
| Windows | [Panel wheel scrolling at 1080p](windows/panel-wheel-scroll-1080p.md) | Resolved | Conditional scrolling based on real content overflow | `61eaf12` | 2026-09-06 |
| Windows | [.NET trimming experiment](windows/trim-experiment.md) | Closed / Not ready | `PublishTrimmed` remains disabled; trimmed Preview failed functional smoke | `99294ab` | 2026-08-31 |
| Android | [Bottom-tabs chromatic text dispersion](android/bottom-tabs-chromatic-text-dispersion.md) | Closed / No production change | Options A–D rejected; production default rendering remains unchanged | `1c2d426` | 2026-09-06 |
| Android | [Long-press highlight](android/long-press-highlight.md) | Resolved | Preview-contact highlight path (方案 B) implemented and accepted | `e095759` | 2026-09-05 |

For a closed investigation without a production fix, `Resolved by` identifies the commit that recorded the
conclusion rather than a behavior change.

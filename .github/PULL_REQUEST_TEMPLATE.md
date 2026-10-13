<!--
  US-MOB-021: every PR must confirm adaptive/responsive behaviour before
  merge. Fill in each checklist item — do not delete unchecked items.
-->

## Summary

<!-- What does this change do, and why? -->

## Responsive & accessibility verification (required)

- [ ] **Phone** (≥360 CSS px): verified — no horizontal scrolling, no clipped primary actions.
- [ ] **Tablet** (≥768 CSS px): verified — no horizontal scrolling, no clipped primary actions.
- [ ] **Desktop** (≥1024 CSS px): verified — no horizontal scrolling, no clipped primary actions.
- [ ] **Safe areas**: fixed/edge-anchored elements respect `env(safe-area-inset-*)`; nothing is obscured on notched/gesture-nav devices.
- [ ] **Touch targets**: interactive controls meet the 44px minimum and are adequately spaced.
- [ ] **Touch gestures**: any long-press/drag-lift behaviour has a working, documented keyboard-accessible equivalent.
- [ ] **Keyboard & AT**: navigation/dialogs are reachable, operable, and announced via keyboard and assistive technology.

## Tests

- [ ] Unit/component tests updated or added (`bun run --filter @sutralekha/frontend test`).
- [ ] Responsive/E2E fixtures updated or added where this change affects layout or navigation (`bun run --filter @sutralekha/frontend e2e`).

## Notes

<!-- Anything reviewers should know: known gaps, follow-ups, screenshots. -->

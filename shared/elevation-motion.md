# Elevation status motion

`elevation-motion.json` contains 29 pairs of closed polygon outlines, sampled every
0.05 seconds. The first pose is traced from the shipped 1024px macOS app icon; the
last pose is a circle and check mark. Both native apps bundle this same file.

Regenerate from the repository root with:

```sh
python3 docs/design/elevate-animation/generate.py
```

The generator requires Pillow and NumPy. It also updates the standalone SVG/GIF
design previews. The apps implement their own indefinite upward pulses and only
advance through the 1.4-second morph after an actual activated result. Approval,
scheduling, and failures do not trigger it. Reduced motion uses static poses.

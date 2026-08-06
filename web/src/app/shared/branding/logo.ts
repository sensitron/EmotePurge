/**
 * The brand mark. One vector file for every size and both modes.
 *
 * It used to be four PNGs behind a theme-driven signal: a simplified mark and a full hero version,
 * each with a light twin, swapped by `ThemeService` because a `<picture media>` query answers what
 * the *system* prefers while this app lets the user overrule that. All of it is gone. The mark is
 * now a single flat colour, and a flat mark carries on graphite and on paper alike — so there is
 * nothing left to swap, no bounding boxes to keep in sync between twins, and no reason for this
 * module to inject anything.
 *
 * Kept as a constant rather than inlined into the three templates that use it, because the file
 * name is exactly the kind of detail that goes stale in two of three places.
 */
export const LOGO_SRC = 'logo.svg';
